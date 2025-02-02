// <copyright file="BufferElementDataSave.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using BovineLabs.Core.Extensions;
    using BovineLabs.Core.Jobs;
    using BovineLabs.Core.Utility;
    using Unity.Assertions;
    using Unity.Burst;
    using Unity.Burst.CompilerServices;
    using Unity.Burst.Intrinsics;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Profiling;
    using UnityEngine;

    /// <summary> Saves <see cref="IBufferElementData"/>. </summary>
    public unsafe struct BufferElementDataSave : ISaver, IDisposable
    {
        private ComponentSave componentSave;

        private EntityTypeHandle entityHandle;
        private DynamicComponentTypeHandle dynamicHandle;
        private DynamicComponentTypeHandle dynamicHandleRW;

        public BufferElementDataSave(ref SystemState state, SaveBuilder builder, ComponentSaveState saveState)
        {
            this.Key = saveState.StableTypeHash;
            this.componentSave = new ComponentSave(ref state, builder, saveState);

            Assert.IsTrue(this.componentSave.TypeInfo.Category == TypeManager.TypeCategory.BufferData);

            this.entityHandle = state.GetEntityTypeHandle();
            this.dynamicHandle = state.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(this.componentSave.TypeIndex));
            this.dynamicHandleRW = state.GetDynamicComponentTypeHandle(ComponentType.ReadWrite(this.componentSave.TypeIndex));
        }

        public ulong Key { get; }

        public void Dispose()
        {
            this.componentSave.Dispose();
        }

        /// <inheritdoc />
        public (Serializer Serializer, JobHandle Dependency) Serialize(ref SystemState state, NativeList<ArchetypeChunk> chunks, JobHandle dependency)
        {
            this.entityHandle.Update(ref state);
            this.dynamicHandle.Update(ref state);

            var serializer = new Serializer(0, state.WorldUpdateAllocator);

            dependency = new SerializeJob
                {
                    Chunks = chunks.AsDeferredJobArray(),
                    Entity = this.entityHandle,
                    BufferType = this.dynamicHandle,
                    Key = this.Key,
                    ElementSize = this.componentSave.TypeInfo.ElementSize,
                    IsEnableable = this.componentSave.TypeIndex.IsEnableable,
                    Serializer = serializer,
                }
                .Schedule(dependency);

            return (serializer, dependency);
        }

        /// <inheritdoc />
        public JobHandle Deserialize(ref SystemState state, Deserializer deserializer, EntityMap entityMap, JobHandle dependency)
        {
            this.entityHandle.Update(ref state);
            this.dynamicHandleRW.Update(ref state);

            var deserializedData = new NativeHashMap<Entity, DeserializedEntityData>(128, state.WorldUpdateAllocator);
            var setEnableable = new NativeReference<bool>(this.componentSave.TypeIndex.IsEnableable, state.WorldUpdateAllocator);

            dependency = new DeserializeJob
                {
                    Deserializer = deserializer,
                    SetEnableable = setEnableable,
                    Remap = entityMap,
                    DerializedData = deserializedData,
                    ElementSize = this.componentSave.TypeInfo.ElementSize,
                }
                .Schedule(dependency);

            if (this.componentSave.Feature == SaveFeature.None)
            {
                dependency = new ApplyJob
                    {
                        DeserializedData = deserializedData,
                        SetEnableable = setEnableable,
                        EntityType = this.entityHandle,
                        BufferType = this.dynamicHandleRW,
                        SaveChunks = this.componentSave.SaveChunks,
                        EntityOffsets = this.componentSave.EntityOffsets,
                        Remap = entityMap,
                    }
                    .ScheduleParallel(this.componentSave.QueryWrite, dependency);
            }
            else
            {
                if ((this.componentSave.Feature & SaveFeature.AddComponent) != 0)
                {
                    dependency = new ApplyAddJob
                        {
                            ComponentType = ComponentType.FromTypeIndex(this.componentSave.TypeIndex),
                            DeserializedData = deserializedData,
                            SetEnableable = setEnableable,
                            SaveChunks = this.componentSave.SaveChunks,
                            EntityOffsets = this.componentSave.EntityOffsets,
                            Remap = entityMap,
                            CommandBuffer = this.componentSave.CreateCommandBuffer(ref state).AsParallelWriter(),
                        }
                        .ScheduleParallel(deserializedData, 64, dependency);
                }
            }

            return dependency;
        }

        [BurstCompile]
        private struct SerializeJob : IJob
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;

            [ReadOnly]
            public EntityTypeHandle Entity;

            [ReadOnly]
            public DynamicComponentTypeHandle BufferType;

            public ulong Key;
            public int ElementSize;
            public bool IsEnableable;

            public Serializer Serializer;

            public void Execute()
            {
                using (new ProfilerMarker("EnsureCapacity").Auto())
                {
                    if (!this.EnsureCapacity())
                    {
                        return;
                    }
                }

                using (new ProfilerMarker("Serialize").Auto())
                {
                    this.Serialize();
                }
            }

            private bool EnsureCapacity()
            {
                var capacity = 0;
                foreach (var chunk in this.Chunks)
                {
                    var buffers = chunk.GetUntypedBufferAccessor(ref this.BufferType);

                    if (buffers.Length == 0)
                    {
                        continue;
                    }

                    capacity += UnsafeUtility.SizeOf<ComponentSave.HeaderChunk>();
                    if (this.IsEnableable)
                    {
                        capacity += chunk.Count * UnsafeUtility.SizeOf<v128>();
                    }

                    capacity += buffers.Length * UnsafeUtility.SizeOf<int>(); // entity
                    capacity += buffers.Length * UnsafeUtility.SizeOf<int>(); // lengths

                    for (var index = 0; index < buffers.Length; index++)
                    {
                        buffers.GetUnsafeReadOnlyPtrAndLength(index, out var bufferLength);
                        capacity += bufferLength * buffers.ElementSize;
                    }
                }

                if (capacity == 0)
                {
                    return false;
                }

                capacity += UnsafeUtility.SizeOf<HeaderSaver>() + UnsafeUtility.SizeOf<ComponentSave.HeaderComponent>();
                this.Serializer.EnsureExtraCapacity(capacity);
                return true;
            }

            private void Serialize()
            {
                var saveIdx = this.Serializer.AllocateNoResize<HeaderSaver>();
                var compIdx = this.Serializer.AllocateNoResize<ComponentSave.HeaderComponent>();

                var entityCount = 0;

                foreach (var chunk in this.Chunks)
                {
                    var buffers = chunk.GetUntypedBufferAccessor(ref this.BufferType);

                    if (buffers.Length == 0)
                    {
                        continue;
                    }

                    var entities = chunk.GetNativeArray(this.Entity).Slice().SliceWithStride<int>();
                    entityCount += entities.Length;

                    var chunkHeader = new ComponentSave.HeaderChunk { Length = entities.Length };
                    this.Serializer.AddNoResize(chunkHeader);
                    this.Serializer.AddBufferNoResize(entities);

                    if (this.IsEnableable)
                    {
                        this.Serializer.AddNoResize(chunk.GetEnableableBits(ref this.BufferType));
                    }

                    // We group all lengths, then all elements - this makes migration much easier.
                    var lengthIdx = this.Serializer.AllocateNoResize<int>(buffers.Length);
                    var lengthSave = this.Serializer.GetAllocation<int>(lengthIdx);

                    for (var index = 0; index < buffers.Length; index++)
                    {
                        var ptr = buffers.GetUnsafeReadOnlyPtrAndLength(index, out var bufferLength);
                        *(lengthSave + index) = bufferLength;
                        this.Serializer.AddBufferNoResize((byte*)ptr, bufferLength * buffers.ElementSize);
                    }
                }

                var headerSave = this.Serializer.GetAllocation<HeaderSaver>(saveIdx);
                *headerSave = new HeaderSaver
                {
                    Key = this.Key,
                    LengthInBytes = this.Serializer.Data->Length,
                };

                var compSave = this.Serializer.GetAllocation<ComponentSave.HeaderComponent>(compIdx);
                *compSave = new ComponentSave.HeaderComponent
                {
                    Count = entityCount,
                    ElementSize = this.ElementSize,
                    IsEnableable = this.IsEnableable,
                };
            }
        }

        [BurstCompile]
        private struct DeserializeJob : IJob
        {
            [ReadOnly]
            public Deserializer Deserializer;

            [ReadOnly]
            public EntityMap Remap;

            public NativeHashMap<Entity, DeserializedEntityData> DerializedData;

            public NativeReference<bool> SetEnableable;

            public int ElementSize;

            public void Execute()
            {
                this.Deserializer.Offset<HeaderSaver>();
                var header = this.Deserializer.Read<ComponentSave.HeaderComponent>();

                if (header.ElementSize != this.ElementSize)
                {
                    Debug.LogError("Critical error, element size does not match expected.");
                    return;
                }

                // If it's enableable and was saved as enableable
                this.SetEnableable.Value &= header.IsEnableable;

                var index = 0;

                while (index < header.Count)
                {
                    var headerChunk = this.Deserializer.Read<ComponentSave.HeaderChunk>();
                    var entities = this.Deserializer.ReadBuffer<int>(headerChunk.Length);

                    v128 enabledBits = default;
                    if (header.IsEnableable)
                    {
                        enabledBits = this.Deserializer.Read<v128>();
                    }

                    var bufferLengths = this.Deserializer.ReadBuffer<int>(headerChunk.Length);

                    for (var i = 0; i < headerChunk.Length; i++)
                    {
                        // We have to read to push ptr even if it's not required
                        var bufferLength = *(bufferLengths + i);
                        var bufferPtr = this.Deserializer.ReadBuffer<byte>(bufferLength * this.ElementSize);

                        if (this.Remap.TryGetEntity(entities[i], out var entity))
                        {
                            this.DerializedData.Add(entity, new DeserializedEntityData
                            {
                                Ptr = bufferPtr,
                                Length = bufferLength,
                                IsEnabled = ComponentSave.IsSet(enabledBits, i),
                            });
                        }
                        else
                        {
                            Debug.LogError($"Entity {entities[i]} wasn't found");
                        }
                    }

                    index += headerChunk.Length;
                }
            }
        }

        [BurstCompile]
        private struct ApplyJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle EntityType;

            public DynamicComponentTypeHandle BufferType;

            [ReadOnly]
            public NativeHashMap<Entity, DeserializedEntityData> DeserializedData;

            [ReadOnly]
            public NativeReference<bool> SetEnableable;

            [ReadOnly]
            public NativeArray<ComponentSave.SaveChunk> SaveChunks;

            [ReadOnly]
            public NativeArray<int> EntityOffsets;

            [ReadOnly]
            public EntityMap Remap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var buffers = chunk.GetUntypedBufferAccessor(ref this.BufferType);

                if (buffers.Length == 0)
                {
                    return;
                }

                var entities = chunk.GetNativeArray(this.EntityType);

                var setEnableable = this.SetEnableable.Value;

                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];

                    if (!this.DeserializedData.TryGetValue(entity, out var entityData))
                    {
                        continue;
                    }

                    buffers.ResizeUninitialized(i, entityData.Length); // Clear

                    var dst = (byte*)buffers.GetUnsafePtr(i);
                    var src = entityData.Ptr;

                    foreach (var element in this.SaveChunks)
                    {
                        UnsafeUtility.MemCpyStride(
                            dst + element.Index,
                            buffers.ElementSize,
                            src + element.Index,
                            buffers.ElementSize,
                            element.Length,
                            entityData.Length);
                    }

                    // Loop offsets so if there is no remapping it doesn't iterate the entire buffer
                    foreach (var offset in this.EntityOffsets)
                    {
                        var ptr = dst;
                        for (var index = 0; index < entityData.Length; index++)
                        {
                            ComponentSave.RemapEntityField(ptr, offset, this.Remap);
                            ptr += buffers.ElementSize;
                        }
                    }

                    if (Hint.Unlikely(setEnableable))
                    {
                        chunk.SetComponentEnabled(ref this.BufferType, i, entityData.IsEnabled);
                    }
                }
            }
        }

        [BurstCompile]
        private struct ApplyAddJob : IJobHashMapDefer
        {
            public ComponentType ComponentType;

            [ReadOnly]
            public NativeHashMap<Entity, DeserializedEntityData> DeserializedData;

            [ReadOnly]
            public NativeReference<bool> SetEnableable;

            [ReadOnly]
            public NativeArray<ComponentSave.SaveChunk> SaveChunks;

            [ReadOnly]
            public NativeArray<int> EntityOffsets;

            [ReadOnly]
            public EntityMap Remap;

            public EntityCommandBuffer.ParallelWriter CommandBuffer;

            public void ExecuteNext(int entryIndex, int jobIndex)
            {
                this.Read(this.DeserializedData, entryIndex, out var entity, out var entityData);

                var buffer = this.CommandBuffer.AddUntypedBuffer(jobIndex, entity, this.ComponentType);
                buffer.ResizeUninitialized(entityData.Length);

                var dst = (byte*)buffer.GetUnsafePtr();
                var src = entityData.Ptr;

                foreach (var element in this.SaveChunks)
                {
                    UnsafeUtility.MemCpyStride(
                        dst + element.Index,
                        buffer.ElementSize,
                        src + element.Index,
                        buffer.ElementSize,
                        element.Length,
                        entityData.Length);
                }

                // Loop offsets so if there is no remapping it doesn't iterate the entire buffer
                foreach (var offset in this.EntityOffsets)
                {
                    var ptr = dst;
                    for (var index = 0; index < entityData.Length; index++)
                    {
                        ComponentSave.RemapEntityField(ptr, offset, this.Remap);
                        ptr += buffer.ElementSize;
                    }
                }

                if (Hint.Unlikely(this.SetEnableable.Value))
                {
                    this.CommandBuffer.SetComponentEnabled(jobIndex, entity, this.ComponentType, entityData.IsEnabled);
                }
            }
        }

        private struct DeserializedEntityData
        {
            public byte* Ptr;
            public int Length;
            public bool IsEnabled;
        }
    }
}
