// <copyright file="SavableSceneSaver.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using BovineLabs.Core.Extensions;
    using Unity.Burst;
    using Unity.Burst.Intrinsics;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using UnityEngine;

    internal unsafe struct SavableSceneSaver : ISaver
    {
        private readonly SystemState* system;

        private EntityTypeHandle entityTypeHandle;
        private ComponentTypeHandle<SavableScene> saveableSceneHandle;
        private ComponentTypeHandle<SavableScene> saveableSceneHandleRO;
        private BufferTypeHandle<SavableLinks> savableLinksHandleRO;
        private BufferLookup<SavableLinks> savableLinks;

        private EntityQuery savableQuery;

        public SavableSceneSaver(SaveBuilder builder)
        {
            this.Key = TypeManager.GetTypeInfo<SavableScene>().StableTypeHash;
            this.system = builder.SystemPtr;

            this.savableQuery = builder.GetQuery(ComponentType.ReadOnly<SavableScene>());
            this.entityTypeHandle = builder.System.GetEntityTypeHandle();
            this.saveableSceneHandle = builder.System.GetComponentTypeHandle<SavableScene>();
            this.saveableSceneHandleRO = builder.System.GetComponentTypeHandle<SavableScene>(true);
            this.savableLinksHandleRO = builder.System.GetBufferTypeHandle<SavableLinks>(true);
            this.savableLinks = builder.System.GetBufferLookup<SavableLinks>();
        }

        /// <inheritdoc/>
        public ulong Key { get; }

        private ref SystemState System => ref *this.system;

        /// <inheritdoc/>
        public (Serializer Serializer, JobHandle Dependency) Serialize(NativeList<ArchetypeChunk> chunks, JobHandle dependency)
        {
            this.entityTypeHandle.Update(ref this.System);
            this.saveableSceneHandleRO.Update(ref this.System);
            this.savableLinksHandleRO.Update(ref this.System);

            var serializer = new Serializer(0, this.System.WorldUpdateAllocator);

            dependency = new SerializeJob
                {
                    Chunks = chunks.AsDeferredJobArray(),
                    Entity = this.entityTypeHandle,
                    SavableSceneType = this.saveableSceneHandleRO,
                    SavableLinksType = this.savableLinksHandleRO,
                    Serializer = serializer,
                    Key = this.Key,
                }
                .Schedule(dependency);

            return (serializer, dependency);
        }

        /// <inheritdoc/>
        public JobHandle Deserialize(Deserializer deserializer, EntityMap entityMap, JobHandle dependency)
        {
            this.entityTypeHandle.Update(ref this.System);
            this.saveableSceneHandle.Update(ref this.System);
            this.savableLinks.Update(ref this.System);

            var work = new NativeList<int>(16, this.System.WorldUpdateAllocator);

            var savable = entityMap.EntitySavableMapping;
            savable.Capacity = this.savableQuery.CalculateEntityCountWithoutFiltering(); // doesn't matter if we oversize, avoid sync of checking filtering

            var dependency1 = new PopulateCurrentSceneEntities
                {
                    CurrentEntities = savable.AsParallelWriter(),
                    EntityType = this.entityTypeHandle,
                    SavableSceneHandle = this.saveableSceneHandle,
                }
                .ScheduleParallel(this.savableQuery, dependency);

            var dependency2 = new DeserializeSplitJob
                {
                    Deserializer = deserializer,
                    Work = work,
                    EntityMappingWriter = entityMap.EntityMapping,
                    EntityPartialMappingWriter = entityMap.EntityPartialMapping,
                }
                .Schedule(dependency);

            dependency = JobHandle.CombineDependencies(dependency1, dependency2);

            dependency = new DeserializeJob
                {
                    Deserializer = deserializer,
                    CurrentEntities = entityMap.EntitySavableMapping,
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
            public NativeArray<ArchetypeChunk> Chunks;

            [ReadOnly]
            public EntityTypeHandle Entity;

            [ReadOnly]
            public ComponentTypeHandle<SavableScene> SavableSceneType;

            [ReadOnly]
            public BufferTypeHandle<SavableLinks> SavableLinksType;

            public Serializer Serializer;

            public ulong Key;

            public void Execute()
            {
                var saveIdx = this.Serializer.Allocate<HeaderSaver>();
                var savableIdx = this.Serializer.Allocate<HeaderSavable>();

                var entityCount = 0;
                var linkCount = 0;

                var capacity = 0;

                // Precompute total capacity to avoid a lot of allocations
                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(ref this.SavableSceneType))
                    {
                        continue;
                    }

                    var entities = chunk.GetNativeArray(this.Entity);
                    var savableLinks = chunk.GetBufferAccessor(ref this.SavableLinksType);

                    capacity += UnsafeUtility.SizeOf<HeaderChunk>();
                    capacity += entities.Length * UnsafeUtility.SizeOf<SavableScene>();
                    capacity += entities.Length * UnsafeUtility.SizeOf<Entity>();

                    capacity += savableLinks.Length * UnsafeUtility.SizeOf<int>();

                    for (var i = 0; i < savableLinks.Length; i++)
                    {
                        capacity += savableLinks[i].Length * UnsafeUtility.SizeOf<SavableLinks>();
                    }
                }

                this.Serializer.EnsureExtraCapacity(capacity);

                foreach (var chunk in this.Chunks)
                {
                    var savableScenes = chunk.GetNativeArray(ref this.SavableSceneType);

                    if (savableScenes.Length == 0)
                    {
                        continue;
                    }

                    var savableLinks = chunk.GetBufferAccessor(ref this.SavableLinksType);
                    var entities = chunk.GetNativeArray(this.Entity);

                    entityCount += entities.Length;

                    var chunkIdx = this.Serializer.AllocateNoResize<HeaderChunk>();
                    var size = this.Serializer.Length;
                    this.Serializer.AddBufferNoResize(entities);
                    this.Serializer.AddBufferNoResize(savableScenes);

                    linkCount += savableLinks.Length;

                    for (var i = 0; i < savableLinks.Length; i++)
                    {
                        var links = savableLinks[i];
                        this.Serializer.AddNoResize(links.Length);
                        this.Serializer.AddBufferNoResize((SavableLinks*)links.GetUnsafeReadOnlyPtr(), links.Length);
                    }

                    var headerChunk = this.Serializer.GetAllocation<HeaderChunk>(chunkIdx);
                    *headerChunk = new HeaderChunk
                    {
                        EntityCount = savableScenes.Length,
                        SavableLinks = savableLinks.Length > 0,
                        SizeInBytes = this.Serializer.Length - size,
                    };
                }

                var headerSave = this.Serializer.GetAllocation<HeaderSaver>(saveIdx);
                *headerSave = new HeaderSaver
                {
                    Key = this.Key,
                    LengthInBytes = this.Serializer.Data.Length,
                };

                var headerSavable = this.Serializer.GetAllocation<HeaderSavable>(savableIdx);
                *headerSavable = new HeaderSavable
                {
                    EntityCount = entityCount,
                    LinkCount = linkCount,
                };
            }
        }

        [BurstCompile]
        private struct PopulateCurrentSceneEntities : IJobChunk
        {
            public NativeParallelHashMap<SavableScene, Entity>.ParallelWriter CurrentEntities;

            [ReadOnly]
            public EntityTypeHandle EntityType;

            [ReadOnly]
            public ComponentTypeHandle<SavableScene> SavableSceneHandle;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(this.EntityType);
                var savableScenes = chunk.GetNativeArray(ref this.SavableSceneHandle);
                this.CurrentEntities.AddBatchUnsafe(savableScenes, entities);
            }
        }

        [BurstCompile]
        private struct DeserializeSplitJob : IJob
        {
            public Deserializer Deserializer;

            public NativeList<int> Work;

            public NativeParallelHashMap<Entity, Entity> EntityMappingWriter;

            public NativeParallelHashMap<int, Entity> EntityPartialMappingWriter;

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
            public NativeParallelHashMap<SavableScene, Entity> CurrentEntities;

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
                var savables = this.Deserializer.ReadBuffer<SavableScene>(headerChunk.EntityCount);

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

                    if (!this.CurrentEntities.TryGetValue(savables[entityIndex], out var newEntity))
                    {
                        Debug.LogError($"Savable {savables[entityIndex]} saved but not found in current SubScene filter.");
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
                            if (oldLink.Value != newLink.Value)
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
            public fixed byte Padding[4];
        }

        private struct HeaderSavable
        {
            public int EntityCount;
            public int LinkCount;
            public fixed byte Padding[8];
        }
    }
}
