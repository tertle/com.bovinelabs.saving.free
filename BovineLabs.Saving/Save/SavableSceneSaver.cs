// <copyright file="SavableSceneSaver.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using BovineLabs.Core.Extensions;
    using BovineLabs.Core.Utility;
    using BovineLabs.Saving.Data;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Profiling;
    using UnityEngine;

    internal unsafe struct SavableSceneSaver : ISaver
    {
        private EntityTypeHandle entityTypeHandle;
        private ComponentTypeHandle<SavableScene> savableSceneHandleRO;
        private BufferTypeHandle<SavableLinks> savableLinksHandleRO;
        private BufferLookup<SavableLinks> savableLinks;

        private EntityQuery savableUnfilteredQuery;
        private EntityQuery savableRecordQuery;
        private EntityQuery commandBufferQuery;

        public SavableSceneSaver(ref SystemState state, SaveBuilder builder)
        {
            this.Key = TypeManager.GetTypeInfo<SavableScene>().StableTypeHash;

            // This is used to match to the record for destroyed entity
            // If we used filtered it would think that all ignored entities were destroyed
            // and it's only used to build a hashset for matching, not actually saving
            var savableSceneComponents = stackalloc[] { ComponentType.ReadOnly<SavableScene>(), };
            this.savableUnfilteredQuery = builder.GetQueryUnfiltered(ref state, new ReadOnlySpan<ComponentType>(savableSceneComponents, 1));

            var recordComponents = stackalloc[] { ComponentType.ReadOnly<SavableSceneRecord>(), ComponentType.ReadOnly<SavableSceneRecordEntity>() };
            this.savableRecordQuery = builder.GetQueryUnfiltered(ref state, new ReadOnlySpan<ComponentType>(recordComponents, 2));

            this.entityTypeHandle = state.GetEntityTypeHandle();
            this.savableSceneHandleRO = state.GetComponentTypeHandle<SavableScene>(true);
            this.savableLinksHandleRO = state.GetBufferTypeHandle<SavableLinks>(true);
            this.savableLinks = state.GetBufferLookup<SavableLinks>();

            this.commandBufferQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<BeginSimulationEntityCommandBufferSystem.Singleton>()
                .WithOptions(EntityQueryOptions.IncludeSystems)
                .Build(ref state);
        }

        /// <inheritdoc/>
        public ulong Key { get; }

        /// <inheritdoc/>
        public (Serializer Serializer, JobHandle Dependency) Serialize(ref SystemState state, NativeList<ArchetypeChunk> chunks, JobHandle dependency)
        {
            this.entityTypeHandle.Update(ref state);
            this.savableSceneHandleRO.Update(ref state);
            this.savableLinksHandleRO.Update(ref state);

            var subSceneRecords = this.savableRecordQuery.TryGetSingletonBuffer<SavableSceneRecord>(out var subSceneBuffer, true)
                ? subSceneBuffer.AsNativeArray()
                : CollectionHelper.CreateNativeArray<SavableSceneRecord>(0, state.WorldUpdateAllocator);

            var subSceneRecordEntitys = this.savableRecordQuery.TryGetSingletonBuffer<SavableSceneRecordEntity>(out var subSceneEntityBuffer, true)
                ? subSceneEntityBuffer.AsNativeArray()
                : CollectionHelper.CreateNativeArray<SavableSceneRecordEntity>(0, state.WorldUpdateAllocator);

            var serializer = new Serializer(0, state.WorldUpdateAllocator);

            var unfilteredChunks = this.savableUnfilteredQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, dependency, out dependency);

            dependency = new SerializeJob
                {
                    SubSceneRecords = subSceneRecords,
                    SubSceneRecordEntitys = subSceneRecordEntitys,
                    Chunks = chunks.AsDeferredJobArray(),
                    ChunksUnfiltered = unfilteredChunks.AsDeferredJobArray(),
                    EntityHandle = this.entityTypeHandle,
                    SavableSceneHandle = this.savableSceneHandleRO,
                    SavableLinksHandle = this.savableLinksHandleRO,
                    Serializer = serializer,
                    Key = this.Key,
                }
                .Schedule(dependency);

            return (serializer, dependency);
        }

        /// <inheritdoc/>
        public JobHandle Deserialize(ref SystemState state, Deserializer deserializer, EntityMap entityMap, JobHandle dependency)
        {
            this.entityTypeHandle.Update(ref state);
            this.savableLinks.Update(ref state);

            var work = new NativeList<int>(16, state.WorldUpdateAllocator);

            var commandBufferSystem = this.commandBufferQuery.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>();
            var commandBuffer = commandBufferSystem.CreateCommandBuffer(state.WorldUnmanaged);

            var currentEntities = new NativeParallelHashMap<SavableSceneRecord, Entity>(0, state.WorldUpdateAllocator);

            if (!this.savableRecordQuery.IsEmpty)
            {
                var subSceneRecords = this.savableRecordQuery.GetSingletonBuffer<SavableSceneRecord>().AsNativeArray();
                var subSceneRecordEntities = this.savableRecordQuery.GetSingletonBuffer<SavableSceneRecordEntity>().AsNativeArray();

                dependency = new PopulateCurrentSceneEntities
                    {
                        CurrentEntities = currentEntities,
                        SubSceneRecords = subSceneRecords,
                        SubSceneRecordEntitys = subSceneRecordEntities,
                    }
                    .Schedule(dependency);
            }

            var entitySavableMapping = new NativeParallelHashMap<int, Entity>(0, state.WorldUpdateAllocator);

            dependency = new DeserializeSplitJob
                {
                    Deserializer = deserializer,
                    Work = work,
                    EntityMappingWriter = entityMap.EntityMapping,
                    EntityPartialMappingWriter = entityMap.EntityPartialMapping,
                    CurrentEntities = currentEntities,
                    CommandBuffer = commandBuffer,
                    EntitySavableMapping = entitySavableMapping,
                }
                .Schedule(dependency);

            dependency = new DeserializeJob
                {
                    Deserializer = deserializer,
                    EntitySavableMapping = entitySavableMapping,
                    Work = work.AsDeferredJobArray(),
                    Links = this.savableLinks,
                    EntityMappingWriter = entityMap.EntityMapping.AsParallelWriter(),
                    EntityPartialMappingWriter = entityMap.EntityPartialMapping.AsParallelWriter(),
                }
                .Schedule(work, 16, dependency);

            return dependency;
        }

        [BurstCompile]
        private struct SerializeJob : IJob
        {
            [ReadOnly]
            public NativeArray<SavableSceneRecord> SubSceneRecords;

            [ReadOnly]
            public NativeArray<SavableSceneRecordEntity> SubSceneRecordEntitys;

            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;

            [ReadOnly]
            public NativeArray<ArchetypeChunk> ChunksUnfiltered;

            [ReadOnly]
            public EntityTypeHandle EntityHandle;

            [ReadOnly]
            public ComponentTypeHandle<SavableScene> SavableSceneHandle;

            [ReadOnly]
            public BufferTypeHandle<SavableLinks> SavableLinksHandle;

            public Serializer Serializer;

            public ulong Key;

            public void Execute()
            {
                NativeParallelHashSet<Entity> existingRecords;
                using (new ProfilerMarker("CreateExistingRecords").Auto())
                {
                    existingRecords = this.CreateExistingRecords();
                }

                using (new ProfilerMarker("EnsureCapacity").Auto())
                {
                    this.EnsureCapacity();
                }

                using (new ProfilerMarker("Serialize").Auto())
                {
                    this.Serialize(existingRecords);
                }
            }

            private NativeParallelHashSet<Entity> CreateExistingRecords()
            {
                var setSize = 0;
                foreach (var chunk in this.ChunksUnfiltered)
                {
                    if (chunk.Has(ref this.SavableSceneHandle))
                    {
                        setSize += chunk.Count;
                    }
                }

                var existingRecords = new NativeParallelHashSet<Entity>(setSize, Allocator.Temp);

                foreach (var chunk in this.ChunksUnfiltered)
                {
                    if (chunk.Has(ref this.SavableSceneHandle))
                    {
                        var entities = chunk.GetEntityDataPtrRO(this.EntityHandle);
                        existingRecords.AddBatchUnsafe(entities, chunk.Count);
                    }
                }

                return existingRecords;
            }

            private void EnsureCapacity()
            {
                // Precompute total capacity to avoid a lot of allocations
                var capacity = UnsafeUtility.SizeOf<HeaderSaver>() + UnsafeUtility.SizeOf<HeaderSavable>();
                capacity += this.SubSceneRecords.Length * UnsafeUtility.SizeOf<Entity>(); // record entity
                capacity += this.SubSceneRecords.Length * UnsafeUtility.SizeOf<SavableSceneRecord>() * 2; // record entity + destroy

                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(ref this.SavableSceneHandle))
                    {
                        continue;
                    }

                    var savableLinks = chunk.GetBufferAccessor(ref this.SavableLinksHandle);

                    capacity += UnsafeUtility.SizeOf<HeaderChunk>();
                    capacity += chunk.Count * UnsafeUtility.SizeOf<Entity>();

                    capacity += savableLinks.Length * UnsafeUtility.SizeOf<int>();

                    for (var i = 0; i < savableLinks.Length; i++)
                    {
                        capacity += savableLinks[i].Length * UnsafeUtility.SizeOf<SavableLinks>();
                    }
                }

                this.Serializer.EnsureExtraCapacity(capacity);
            }

            private void Serialize(NativeParallelHashSet<Entity> existingRecords)
            {
                var saveIdx = this.Serializer.AllocateNoResize<HeaderSaver>();
                var savableIdx = this.Serializer.AllocateNoResize<HeaderSavable>();

                ushort destroyed = 0;
                var entityCount = 0;
                var linkCount = 0;

                var recordEntities = this.SubSceneRecordEntitys;
                var records = this.SubSceneRecords;

                this.Serializer.AddBufferNoResize(recordEntities);
                this.Serializer.AddBufferNoResize(records);

                for (var index = 0; index < records.Length; index++)
                {
                    if (existingRecords.Contains(recordEntities[index].Value))
                    {
                        // Still exists continue
                        continue;
                    }

                    // Has been destroyed need to record this
                    destroyed++;
                    this.Serializer.AddNoResize(records[index]);
                }

                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(ref this.SavableSceneHandle))
                    {
                        continue;
                    }

                    var savableLinks = chunk.GetBufferAccessor(ref this.SavableLinksHandle);
                    var entities = chunk.GetEntityDataPtrRO(this.EntityHandle);

                    entityCount += chunk.Count;

                    var chunkIdx = this.Serializer.AllocateNoResize<HeaderChunk>();
                    var size = this.Serializer.Length;
                    this.Serializer.AddBufferNoResize(entities, chunk.Count);

                    for (var i = 0; i < savableLinks.Length; i++)
                    {
                        var links = savableLinks[i];
                        linkCount += links.Length;
                        this.Serializer.AddNoResize(links.Length);
                        this.Serializer.AddBufferNoResize((SavableLinks*)links.GetUnsafeReadOnlyPtr(), links.Length);
                    }

                    var headerChunk = this.Serializer.GetAllocation<HeaderChunk>(chunkIdx);
                    *headerChunk = new HeaderChunk
                    {
                        EntityCount = chunk.Count,
                        SavableLinks = savableLinks.Length > 0,
                        SizeInBytes = this.Serializer.Length - size,
                    };
                }

                var headerSave = this.Serializer.GetAllocation<HeaderSaver>(saveIdx);
                *headerSave = new HeaderSaver
                {
                    Key = this.Key,
                    LengthInBytes = this.Serializer.Data->Length,
                };

                var headerSavable = this.Serializer.GetAllocation<HeaderSavable>(savableIdx);
                *headerSavable = new HeaderSavable
                {
                    RecordCount = (ushort)records.Length,
                    RecordDestroyed = destroyed,
                    EntityCount = entityCount,
                    LinkCount = linkCount,
                };
            }
        }

        [BurstCompile]
        private struct PopulateCurrentSceneEntities : IJob
        {
            public NativeParallelHashMap<SavableSceneRecord, Entity> CurrentEntities;

            [ReadOnly]
            public NativeArray<SavableSceneRecord> SubSceneRecords;

            [ReadOnly]
            public NativeArray<SavableSceneRecordEntity> SubSceneRecordEntitys;

            public void Execute()
            {
                this.CurrentEntities.AddBatchUnsafe(this.SubSceneRecords, this.SubSceneRecordEntitys.Reinterpret<Entity>());
            }
        }

        [BurstCompile]
        private struct DeserializeSplitJob : IJob
        {
            public Deserializer Deserializer;

            public NativeList<int> Work;

            public NativeParallelHashMap<Entity, Entity> EntityMappingWriter;

            public NativeParallelHashMap<int, Entity> EntityPartialMappingWriter;

            [ReadOnly]
            public NativeParallelHashMap<SavableSceneRecord, Entity> CurrentEntities;

            public NativeParallelHashMap<int, Entity> EntitySavableMapping;

            public EntityCommandBuffer CommandBuffer;

            public EntityQueryMask FilterMask;

            public void Execute()
            {
                this.Deserializer.Offset<HeaderSaver>();
                var header = this.Deserializer.Read<HeaderSavable>();

                var newCapacity = this.EntityMappingWriter.Count() + header.EntityCount + header.LinkCount;

                if (this.EntityMappingWriter.Capacity < newCapacity)
                {
                    this.EntityMappingWriter.Capacity = newCapacity;
                    this.EntityPartialMappingWriter.Capacity = newCapacity;
                }

                var savedEntities = this.Deserializer.ReadBuffer<Entity>(header.RecordCount);
                var record = this.Deserializer.ReadBuffer<SavableSceneRecord>(header.RecordCount);
                this.EntitySavableMapping.Capacity = header.RecordCount;

                // TODO we should do this in parallel probably otherwise will be pretty slow for a huge scene
                // Remap old SavableScene to new SavableScene
                for (ushort i = 0; i < header.RecordCount; i++)
                {
                    if (this.CurrentEntities.TryGetValue(record[i], out var entity))
                    {
                        this.EntitySavableMapping.Add(savedEntities[i].Index, entity);
                    }
                }

                for (var i = 0; i < header.RecordDestroyed; i++)
                {
                    var destroyedEntity = this.Deserializer.Read<SavableSceneRecord>();

                    if (!this.CurrentEntities.TryGetValue(destroyedEntity, out var entity))
                    {
                        continue;
                    }

                    this.CommandBuffer.DestroyEntity(entity);
                }

                var index = 0;

                while (index < header.EntityCount)
                {
                    this.Work.Add(this.Deserializer.CurrentIndex);
                    var headerChunk = this.Deserializer.Read<HeaderChunk>();
                    index += headerChunk.EntityCount;
                    this.Deserializer.Offset(headerChunk.SizeInBytes);
                }
            }
        }

        [BurstCompile]
        private struct DeserializeJob : IJobParallelForDefer
        {
            [ReadOnly]
            public Deserializer Deserializer;

            [ReadOnly]
            public NativeParallelHashMap<int, Entity> EntitySavableMapping;

            [ReadOnly]
            public NativeArray<int> Work;

            [ReadOnly]
            public BufferLookup<SavableLinks> Links;

            public NativeParallelHashMap<Entity, Entity>.ParallelWriter EntityMappingWriter;

            public NativeParallelHashMap<int, Entity>.ParallelWriter EntityPartialMappingWriter;

            public void Execute(int index)
            {
                var startIndex = this.Work[index];
                this.Deserializer.CurrentIndex = startIndex;

                var headerChunk = this.Deserializer.Read<HeaderChunk>();

                var entities = this.Deserializer.ReadBuffer<Entity>(headerChunk.EntityCount);

                for (var entityIndex = 0; entityIndex < headerChunk.EntityCount; entityIndex++)
                {
                    int linksLength = default;
                    SavableLinks* oldLinks = default;

                    // Still need to read any data to progress pointer.
                    if (headerChunk.SavableLinks)
                    {
                        linksLength = this.Deserializer.Read<int>();
                        oldLinks = this.Deserializer.ReadBuffer<SavableLinks>(linksLength);
                    }

                    if (!this.EntitySavableMapping.TryGetValue(entities[entityIndex].Index, out var newEntity))
                    {
                        Debug.LogError($"Savable {entities[entityIndex].ToFixedString()} saved but not found in current SubScene filter.");
                        continue;
                    }

                    var oldEntity = entities[entityIndex];
                    this.EntityMappingWriter.TryAdd(oldEntity, newEntity);
                    this.EntityPartialMappingWriter.TryAdd(oldEntity.Index, newEntity);

                    if (!headerChunk.SavableLinks)
                    {
                        continue;
                    }

                    if (!this.Links.TryGetBuffer(newEntity, out var newLinks))
                    {
                        // No longer has links on the prefab
                        continue;
                    }

                    var linkArray = newLinks.AsNativeArray();

                    for (var i = 0; i < linksLength; i++)
                    {
                        var oldLink = oldLinks[i];

                        foreach (var newLink in linkArray)
                        {
                            if (oldLink.LinkID != newLink.LinkID)
                            {
                                continue;
                            }

                            this.EntityMappingWriter.TryAdd(oldLink.Entity, newLink.Entity);
                            this.EntityPartialMappingWriter.TryAdd(oldLink.Entity.Index, newLink.Entity);
                            break;
                        }
                    }
                }
            }
        }

        private struct HeaderChunk
        {
            public int EntityCount;
            public int SizeInBytes; // Does not include this
            public bool SavableLinks;

            // Reserved for future
            public fixed byte Padding[7];
        }

        private struct HeaderSavable
        {
            public ushort RecordCount;
            public ushort RecordDestroyed;
            public int EntityCount;
            public int LinkCount;

            public fixed byte Padding[4];
        }
    }
}
