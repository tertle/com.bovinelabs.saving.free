// <copyright file="SavablePrefabSaver.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using BovineLabs.Core.Assertions;
    using BovineLabs.Core.Extensions;
    using BovineLabs.Core.Jobs;
    using BovineLabs.Core.Utility;
    using BovineLabs.Saving.Data;
    using Unity.Burst;
    using Unity.Burst.Intrinsics;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Profiling;
    using UnityEngine;

    internal unsafe struct SavablePrefabSaver : ISaver
    {
        private readonly bool usePrefabInstances;

        private readonly EntityQuery prefabQuery;
        private readonly EntityQuery instantiateQuery;
        private readonly EntityQuery initializedQuery;
        private EntityQuery savablePrefabQuery;
        private EntityQuery savablePrefabGUIDQuery;

        private EntityTypeHandle entityHandle;
        private BufferTypeHandle<SavableLinks> saveableLinksHandle;
        private ComponentTypeHandle<SavablePrefab> savablePrefabHandle;
        private BufferLookup<SavableLinks> saveableLinks;

        public SavablePrefabSaver(ref SystemState state, SaveBuilder builder)
        {
            this.Key = TypeManager.GetTypeInfo<SavablePrefab>().StableTypeHash;
            this.usePrefabInstances = builder.UseExistingInstances;

            this.entityHandle = state.GetEntityTypeHandle();
            this.saveableLinksHandle = state.GetBufferTypeHandle<SavableLinks>(true);
            this.savablePrefabHandle = state.GetComponentTypeHandle<SavablePrefab>(true);
            this.saveableLinks = state.GetBufferLookup<SavableLinks>(true);

            this.savablePrefabQuery = builder.GetQuery(ref state, ComponentType.ReadOnly<SavablePrefab>());
            this.savablePrefabGUIDQuery = builder.GetQuery(ref state, ComponentType.ReadOnly<SavablePrefabRecord>());

            var components = stackalloc[] { ComponentType.ReadOnly<SavablePrefab>(), ComponentType.ReadOnly<Prefab>() };
            this.prefabQuery = builder.GetPrefabQuery(ref state, new ReadOnlySpan<ComponentType>(components, 2));

            this.instantiateQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SavablePrefab, ToInitialize>()
                .Build(ref state);

            this.initializedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ToInitialize>()
                .WithOptions(EntityQueryOptions.IncludePrefab)
                .Build(ref state);
        }

        /// <inheritdoc/>
        public ulong Key { get; }

        /// <inheritdoc/>
        public (Serializer Serializer, JobHandle Dependency) Serialize(ref SystemState state, NativeList<ArchetypeChunk> chunks, JobHandle dependency)
        {
            this.entityHandle.Update(ref state);
            this.savablePrefabHandle.Update(ref state);
            this.saveableLinksHandle.Update(ref state);

            var savablePrefabs = this.savablePrefabGUIDQuery.TryGetSingletonBuffer<SavablePrefabRecord>(out var subSceneBuffer, true)
                ? subSceneBuffer.AsNativeArray()
                : CollectionHelper.CreateNativeArray<SavablePrefabRecord>(0, state.WorldUpdateAllocator);

            var serializer = new Serializer(1024, state.WorldUpdateAllocator);

            dependency = new SerializeJob
                {
                    SavablePrefabs = savablePrefabs,
                    Chunks = chunks.AsDeferredJobArray(),
                    Entity = this.entityHandle,
                    SavablePrefabHandle = this.savablePrefabHandle,
                    SavableLinksHandle = this.saveableLinksHandle,
                    Serializer = serializer,
                    Key = this.Key,
                }
                .Schedule(dependency);

            return (serializer, dependency);
        }

        /// <inheritdoc/>
        public JobHandle Deserialize(ref SystemState state, Deserializer deserializer, EntityMap entityMap, JobHandle dependency)
        {
            // We need to instantiate entities and doing other operations so just get ready
            dependency.Complete();

            var savedLinks = new NativeHashMap<Entity, UnsafeList<SavableLinks>>(0, state.WorldUpdateAllocator);
            var prefabLists = new NativeHashMap<SavablePrefabRecord, UnsafeList<Entity>>(0, state.WorldUpdateAllocator);
            var serializedPrefabs = new NativeReference<SerializedPrefabs>(state.WorldUpdateAllocator);

            var oldEntities = new NativeList<Entity>(0, state.WorldUpdateAllocator);
            var newEntities = new NativeList<Entity>(0, state.WorldUpdateAllocator);

            // We need to sync point before instantiate anyway so it's faster to run the jobs
            var prefabLookup = this.CreatePrefabLookup(ref state, state.WorldUpdateAllocator);

            using var e = prefabLookup.GetEnumerator();
            while (e.MoveNext())
            {
                prefabLists.Add(e.Current.Key, new UnsafeList<Entity>(0, Allocator.Persistent));
            }

            var savedEntities = new NativeParallelMultiHashMap<SavablePrefab, Entity>(0, state.WorldUpdateAllocator);

            new DeserializeJob
                {
                    Deserializer = deserializer,
                    SavedEntities = savedEntities,
                    SavedLinks = savedLinks,
                    SavablePrefabGUIDs = serializedPrefabs,
                }
                .Run();

            if (this.usePrefabInstances)
            {
                var existing = new NativeParallelHashSet<Entity>(this.savablePrefabQuery.CalculateEntityCount(), Allocator.TempJob);
                var existingEntities = this.savablePrefabQuery.ToEntityArray(Allocator.Temp);
                existing.AddBatchUnsafe(existingEntities);

                new SplitIntoListsWithExistingJob()
                    {
                        PrefabLists = prefabLists,
                        SavedEntities = savedEntities,
                        SavablePrefabGUIDs = serializedPrefabs,
                        ExistingEntities = existing,
                    }
                    .ScheduleParallel(savedEntities, 64).Complete();

                existing.Dispose();

                oldEntities.AddRange(existingEntities);
                newEntities.AddRange(existingEntities);
            }
            else
            {
                new SplitIntoListsJob
                    {
                        PrefabLists = prefabLists,
                        SavedEntities = savedEntities,
                        SavablePrefabGUIDs = serializedPrefabs,
                    }
                    .ScheduleParallel(savedEntities, 64).Complete();

                // can't use query because of linked entity groups
                state.EntityManager.DestroyEntity(this.savablePrefabQuery.ToEntityArray(Allocator.Temp));
            }

            using (new ProfilerMarker("Instantiate").Auto())
            {
                var hasSceneSection = this.savablePrefabQuery.QueryHasSharedFilter<SceneSection>(out var scdIndex);

                if (hasSceneSection)
                {
                    // We add a temp tag component to the prefabs so we can more efficiently execute a query on the created entities
                    // This should be faster in nearly all cases as batch query operations are much faster than per entity operations
                    // and there should always be more instances than prefabs.
                    // dependency.Complete();
                    state.EntityManager.AddComponent<ToInitialize>(prefabLookup.GetValueArray(Allocator.Temp));
                }

                using var saved = prefabLists.GetEnumerator();
                while (saved.MoveNext())
                {
                    var current = saved.Current;
                    var type = current.Key;
                    var entities = current.Value;
                    var prefab = prefabLookup[type]; // validated in the job
                    var start = newEntities.Length;

                    newEntities.ResizeUninitialized(start + entities.Length);
                    oldEntities.AddRange(entities.Ptr, entities.Length);

                    state.EntityManager.Instantiate(prefab, newEntities.AsArray().GetSubArray(start, entities.Length));

                    current.Value.Dispose();
                }

                // Add back their SceneSection shared component if it should exist
                if (hasSceneSection)
                {
                    using (new ProfilerMarker("AddSharedComponentData").Auto())
                    {
                        var sceneSection = state.EntityManager.GetSharedComponent<SceneSection>(scdIndex);
                        state.EntityManager.AddSharedComponent(this.instantiateQuery, sceneSection);
                    }

                    using (new ProfilerMarker("RemoveComponent").Auto())
                    {
                        // TODO we can probably defer this
                        state.EntityManager.RemoveComponent<ToInitialize>(this.initializedQuery);
                    }
                }
            }

            // We want to both read and write to this hashmap in RemapLinks so we need to clone it
            var entityMappingClone = new NativeParallelHashMap<Entity, Entity>(0, state.WorldUpdateAllocator);

            dependency = new PopulateEntityMaps
                {
                    EntityMapping = entityMap.EntityMapping,
                    EntityPartialMapping = entityMap.EntityPartialMapping,
                    Links = savedLinks,
                    OldEntities = oldEntities,
                    NewEntities = newEntities,
                    EntityMappingClone = entityMappingClone,
                }
                .Schedule(dependency);

            this.saveableLinks.Update(ref state);

            dependency = new RemapLinks
                {
                    EntityMappingWriter = entityMap.EntityMapping.AsParallelWriter(),
                    EntityPartialMappingWriter = entityMap.EntityPartialMapping.AsParallelWriter(),
                    SavedLinks = savedLinks,
                    EntityMapping = entityMappingClone,
                    Links = this.saveableLinks,
                }
                .ScheduleParallel(savedLinks, 256, dependency);

            return dependency;
        }

        private NativeHashMap<SavablePrefabRecord, Entity> CreatePrefabLookup(ref SystemState state, Allocator allocator)
        {
            this.entityHandle.Update(ref state);
            this.savablePrefabHandle.Update(ref state);

            if (!this.savablePrefabGUIDQuery.TryGetSingletonBuffer<SavablePrefabRecord>(out var prefabGUIDs, true))
            {
                return new NativeHashMap<SavablePrefabRecord, Entity>(0, allocator);
            }

            // state.EntityManager.GetAllUniqueSharedComponents<SavablePrefab>(out var savables, allocator);
            var lookup = new NativeHashMap<SavablePrefabRecord, Entity>(prefabGUIDs.Length, allocator);

            new CreatePrefabLookupJob
                {
                    Lookup = lookup,
                    SavablePrefabGUIDs = prefabGUIDs.AsNativeArray(),
                    EntityHandle = this.entityHandle,
                    SavablePrefabHandle = this.savablePrefabHandle,
                }
                .Run(this.prefabQuery);

            return lookup;
        }

        [BurstCompile]
        private struct CreatePrefabLookupJob : IJobChunk
        {
            public NativeHashMap<SavablePrefabRecord, Entity> Lookup;

            [ReadOnly]
            public NativeArray<SavablePrefabRecord> SavablePrefabGUIDs;

            [ReadOnly]
            public EntityTypeHandle EntityHandle;

            [ReadOnly]
            public ComponentTypeHandle<SavablePrefab> SavablePrefabHandle;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetEntityDataPtrRO(this.EntityHandle);
                var savablePrefabs = chunk.GetComponentDataPtrRO(ref this.SavablePrefabHandle);

                for (var i = 0; i < chunk.Count; i++)
                {
                    var savablePrefab = savablePrefabs[i];

                    if (savablePrefab.Value >= this.SavablePrefabGUIDs.Length)
                    {
                        Debug.LogError("WTF?");
                        continue;
                    }

                    var guid = this.SavablePrefabGUIDs[savablePrefab.Value];

                    if (!this.Lookup.TryAdd(guid, entities[i]))
                    {
                        Debug.LogError($"More than one prefab of savable index {guid.Value0},{guid.Value1},{guid.Value2},{guid.Value3}, found in world, using first");
                    }
                }
            }
        }

        [BurstCompile]
        private struct SerializeJob : IJob
        {
            [ReadOnly]
            public NativeArray<SavablePrefabRecord> SavablePrefabs;

            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;

            [ReadOnly]
            public EntityTypeHandle Entity;

            [ReadOnly]
            public ComponentTypeHandle<SavablePrefab> SavablePrefabHandle;

            [ReadOnly]
            public BufferTypeHandle<SavableLinks> SavableLinksHandle;

            public Serializer Serializer;

            public ulong Key;

            public void Execute()
            {
                this.EnsureCapacity();
                this.Serialize();
            }

            private void EnsureCapacity()
            {
                var capacity = 0;
                capacity += UnsafeUtility.SizeOf<HeaderSaver>() + UnsafeUtility.SizeOf<HeaderSavable>();
                capacity += this.SavablePrefabs.Length * UnsafeUtility.SizeOf<SavablePrefabRecord>();

                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(ref this.SavablePrefabHandle))
                    {
                        continue;
                    }

                    capacity += chunk.Count * (UnsafeUtility.SizeOf<Entity>() + UnsafeUtility.SizeOf<SavablePrefab>());
                    var savableLinks = chunk.GetBufferAccessor(ref this.SavableLinksHandle);

                    for (var i = 0; i < savableLinks.Length; i++)
                    {
                        var links = savableLinks[i];
                        capacity += UnsafeUtility.SizeOf<int>();
                        capacity += links.Length * UnsafeUtility.SizeOf<SavableLinks>();
                    }
                }

                this.Serializer.EnsureExtraCapacity(capacity);
            }

            private void Serialize()
            {
                // TODO precompute capacity
                var saveIdx = this.Serializer.AllocateNoResize<HeaderSaver>();
                var savableIdx = this.Serializer.AllocateNoResize<HeaderSavable>();

                var entityCount = 0;

                this.Serializer.AddBufferNoResize(this.SavablePrefabs);

                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(ref this.SavablePrefabHandle))
                    {
                        continue;
                    }

                    var entities = chunk.GetEntityDataPtrRO(this.Entity);
                    var savablePrefabs = chunk.GetComponentDataPtrRO(ref this.SavablePrefabHandle);

                    var savableLinks = chunk.GetBufferAccessor(ref this.SavableLinksHandle);
                    entityCount += chunk.Count;

                    this.Serializer.Add(new HeaderChunk
                    {
                        Length = chunk.Count,
                        SavableLinks = savableLinks.Length > 0,
                    });

                    this.Serializer.AddBufferNoResize(entities, chunk.Count);
                    this.Serializer.AddBufferNoResize(savablePrefabs, chunk.Count);

                    for (var i = 0; i < savableLinks.Length; i++)
                    {
                        var links = savableLinks[i];
                        this.Serializer.AddNoResize(links.Length);
                        this.Serializer.AddBufferNoResize((SavableLinks*)links.GetUnsafeReadOnlyPtr(), links.Length);
                    }
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
                    Prefabs = this.SavablePrefabs.Length,
                    Count = entityCount,
                };
            }
        }

        [BurstCompile]
        private struct DeserializeJob : IJob
        {
            [ReadOnly]
            public Deserializer Deserializer;

            public NativeParallelMultiHashMap<SavablePrefab, Entity> SavedEntities; // TODO make non-parallel
            public NativeHashMap<Entity, UnsafeList<SavableLinks>> SavedLinks;

            public NativeReference<SerializedPrefabs> SavablePrefabGUIDs;

            public void Execute()
            {
                this.Deserializer.Offset<HeaderSaver>();
                var header = this.Deserializer.Read<HeaderSavable>();

                var prefabs = this.Deserializer.ReadBuffer<SavablePrefabRecord>(header.Prefabs);
                this.SavablePrefabGUIDs.Value = new SerializedPrefabs(prefabs, header.Prefabs);

                var index = 0;

                this.SavedEntities.Capacity = header.Count;
                this.SavedEntities.SetAllocatedIndexLength(header.Count);

                var buckets = this.SavedEntities.GetUnsafeBucketData();
                var keys = (SavablePrefab*)buckets.keys;
                var values = (Entity*)buckets.values;

                while (index < header.Count)
                {
                    var headerChunk = this.Deserializer.Read<HeaderChunk>();

                    var entityPtr = this.Deserializer.ReadBuffer<Entity>(headerChunk.Length);
                    var savablePrefabsPtr = this.Deserializer.ReadBuffer<SavablePrefab>(headerChunk.Length);

                    UnsafeUtility.MemCpy(keys + index, savablePrefabsPtr, headerChunk.Length * sizeof(SavablePrefab));
                    UnsafeUtility.MemCpy(values + index, entityPtr, headerChunk.Length * sizeof(Entity));

                    index += headerChunk.Length;

                    if (headerChunk.SavableLinks)
                    {
                        for (var i = 0; i < headerChunk.Length; i++)
                        {
                            var linksLength = this.Deserializer.Read<int>();
                            var data = this.Deserializer.ReadBuffer<SavableLinks>(linksLength);

                            this.SavedLinks.Add(entityPtr[i], new UnsafeList<SavableLinks>(data, linksLength));
                        }
                    }
                }

                this.SavedEntities.RecalculateBuckets();
            }
        }

        [BurstCompile]
        private struct SplitIntoListsJob : IJobParallelHashMapDefer
        {
            [NativeDisableParallelForRestriction]
            public NativeHashMap<SavablePrefabRecord, UnsafeList<Entity>> PrefabLists;

            [ReadOnly]
            public NativeParallelMultiHashMap<SavablePrefab, Entity> SavedEntities;

            [ReadOnly]
            public NativeReference<SerializedPrefabs> SavablePrefabGUIDs;

            public void ExecuteNext(int entryIndex, int jobIndex)
            {
                this.Read(this.SavedEntities, entryIndex, out var savablePrefab, out var value);

                Check.Assume(savablePrefab.Value < this.SavablePrefabGUIDs.Value.Length);

                var guids = this.SavablePrefabGUIDs.Value.Guids;
                var guid = guids[savablePrefab.Value];

                if (!this.PrefabLists.TryGetValue(guid, out var entities))
                {
                    Debug.LogError($"Prefab missing for {savablePrefab} will not be deserialized");
                    return;
                }

                entities.Add(value);
                this.PrefabLists[guid] = entities;
            }
        }

        [BurstCompile]
        private struct SplitIntoListsWithExistingJob : IJobParallelHashMapDefer
        {
            [NativeDisableParallelForRestriction]
            public NativeHashMap<SavablePrefabRecord, UnsafeList<Entity>> PrefabLists;

            [ReadOnly]
            public NativeParallelMultiHashMap<SavablePrefab, Entity> SavedEntities;

            [ReadOnly]
            public NativeReference<SerializedPrefabs> SavablePrefabGUIDs;

            [ReadOnly]
            public NativeParallelHashSet<Entity> ExistingEntities;

            public void ExecuteNext(int entryIndex, int jobIndex)
            {
                this.Read(this.SavedEntities, entryIndex, out var savablePrefab, out var value);

                if (this.ExistingEntities.Contains(value))
                {
                    return;
                }

                Check.Assume(savablePrefab.Value < this.SavablePrefabGUIDs.Value.Length);

                var guids = this.SavablePrefabGUIDs.Value.Guids;
                var guid = guids[savablePrefab.Value];

                if (!this.PrefabLists.TryGetValue(guid, out var entities))
                {
                    Debug.LogError($"Prefab missing for {savablePrefab} will not be deserialized");
                    return;
                }

                entities.Add(value);
                this.PrefabLists[guid] = entities;
            }
        }

        [BurstCompile]
        private struct PopulateEntityMaps : IJob
        {
            public NativeParallelHashMap<Entity, Entity> EntityMapping;
            public NativeParallelHashMap<int, Entity> EntityPartialMapping;

            [ReadOnly]
            public NativeHashMap<Entity, UnsafeList<SavableLinks>> Links;

            [ReadOnly]
            public NativeList<Entity> OldEntities;

            [ReadOnly]
            public NativeList<Entity> NewEntities;

            public NativeParallelHashMap<Entity, Entity> EntityMappingClone;

            public void Execute()
            {
                var linkCapacity = 0;

                using var links = this.Links.GetEnumerator();
                while (links.MoveNext())
                {
                    linkCapacity += links.Current.Value.Length;
                }

                var capacity = this.NewEntities.Length + linkCapacity;

                if (this.EntityMapping.Capacity < capacity)
                {
                    this.EntityMapping.Capacity = capacity;
                    this.EntityPartialMapping.Capacity = capacity;
                }

                if (this.EntityMappingClone.Capacity < this.NewEntities.Length)
                {
                    this.EntityMappingClone.Capacity = this.NewEntities.Length;
                }

                this.EntityMapping.AddBatchUnsafe(this.OldEntities.AsArray(), this.NewEntities.AsArray());

                var n = this.OldEntities.AsArray().Slice().SliceWithStride<int>();
                this.EntityPartialMapping.AddBatchUnsafe(n, this.NewEntities.AsArray());

                // TODO we could actually memcpy entirely from EntityMapping to avoid bucket calculation
                this.EntityMappingClone.AddBatchUnsafe(this.OldEntities.AsArray(), this.NewEntities.AsArray());
            }
        }

        [BurstCompile]
        private struct RemapLinks : IJobHashMapDefer
        {
            public NativeParallelHashMap<Entity, Entity>.ParallelWriter EntityMappingWriter;

            public NativeParallelHashMap<int, Entity>.ParallelWriter EntityPartialMappingWriter;

            [ReadOnly]
            public NativeHashMap<Entity, UnsafeList<SavableLinks>> SavedLinks;

            [ReadOnly]
            public NativeParallelHashMap<Entity, Entity> EntityMapping;

            [ReadOnly]
            public BufferLookup<SavableLinks> Links;

            public void ExecuteNext(int entryIndex, int jobIndex)
            {
                this.Read(this.SavedLinks, entryIndex, out var key, out var value);
                var entity = this.EntityMapping[key];

                if (!this.Links.TryGetBuffer(entity, out var links))
                {
                    // No longer has links on the prefab
                    return;
                }

                var linkArray = links.AsNativeArray();

                foreach (var oldLink in value)
                {
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

        private struct HeaderChunk
        {
            public int Length;
            public bool SavableLinks;

            // Reserved for future
            public fixed byte Padding[9];
        }

        private struct HeaderSavable
        {
            public int Prefabs;
            public int Count;
            public fixed byte Padding[8];
        }

        private readonly struct SerializedPrefabs
        {
            public readonly SavablePrefabRecord* Guids;
            public readonly int Length;

            public SerializedPrefabs(SavablePrefabRecord* guids, int length)
            {
                this.Guids = guids;
                this.Length = length;
            }
        }

        private struct ToInitialize : IComponentData
        {
        }
    }
}
