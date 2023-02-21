// <copyright file="SavablePrefabSaver.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using BovineLabs.Core.Extensions;
    using BovineLabs.Core.Jobs;
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
        private readonly SystemState* system;

        private readonly EntityQuery prefabQuery;
        private readonly EntityQuery instantiateQuery;
        private readonly EntityQuery initializedQuery;
        private EntityQuery savablePrefabQuery;

        private EntityTypeHandle entityHandle;
        private BufferTypeHandle<SavableLinks> saveableLinksHandle;
        private SharedComponentTypeHandle<SavablePrefab> savablePrefabHandle;
        private BufferLookup<SavableLinks> saveableLinks;

        public SavablePrefabSaver(SaveBuilder builder)
        {
            this.Key = TypeManager.GetTypeInfo<SavablePrefab>().StableTypeHash;
            this.system = builder.SystemPtr;
            this.usePrefabInstances = builder.UseExistingInstances;

            this.entityHandle = builder.System.GetEntityTypeHandle();
            this.saveableLinksHandle = builder.System.GetBufferTypeHandle<SavableLinks>(true);
            this.savablePrefabHandle = builder.System.GetSharedComponentTypeHandle<SavablePrefab>();
            this.saveableLinks = builder.System.GetBufferLookup<SavableLinks>(true);

            this.savablePrefabQuery = builder.GetQuery(ComponentType.ReadOnly<SavablePrefab>());

            this.prefabQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SavablePrefab, Prefab>()
                .WithOptions(EntityQueryOptions.IncludePrefab)
                .Build(builder.System.EntityManager);

            this.instantiateQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SavablePrefab, ToInitialize>()
                .Build(builder.System.EntityManager);

            this.initializedQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ToInitialize>()
                .WithOptions(EntityQueryOptions.IncludePrefab)
                .Build(builder.System.EntityManager);
        }

        /// <inheritdoc/>
        public ulong Key { get; }

        private ref SystemState System => ref *this.system;

        /// <inheritdoc/>
        public (Serializer Serializer, JobHandle Dependency) Serialize(NativeList<ArchetypeChunk> chunks, JobHandle dependency)
        {
            this.entityHandle.Update(ref this.System);
            this.savablePrefabHandle.Update(ref this.System);
            this.saveableLinksHandle.Update(ref this.System);

            var serializer = new Serializer(1024, this.System.WorldUpdateAllocator);

            dependency = new SerializeJob
                {
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
        public JobHandle Deserialize(Deserializer deserializer, EntityMap entityMap, JobHandle dependency)
        {
            // We need to instantiate entities and doing other operations so just get ready
            dependency.Complete();

            var savedEntities = new NativeParallelHashMap<SavablePrefab, UnsafeList<Entity>>(0, this.System.WorldUpdateAllocator);
            var savedLinks = new NativeParallelHashMap<Entity, UnsafeList<SavableLinks>>(0, this.System.WorldUpdateAllocator);

            // We need to sync point before instantiate anyway so it's faster to run the jobs
            var prefabLookup = this.CreatePrefabLookup(this.System.WorldUpdateAllocator);

            new AllocateLists { SavedEntities = savedEntities, PrefabLookup = prefabLookup }.Run();

            var oldEntities = new NativeList<Entity>(0, this.System.WorldUpdateAllocator);
            var newEntities = new NativeList<Entity>(0, this.System.WorldUpdateAllocator);

            if (this.usePrefabInstances)
            {
                var existing = new NativeParallelHashSet<Entity>(this.savablePrefabQuery.CalculateEntityCount(), Allocator.TempJob);
                var existingEntities = this.savablePrefabQuery.ToEntityArray(Allocator.Temp);
                existing.AddBatchUnsafe(existingEntities);

                new DeserializeWithCheckJob
                    {
                        Deserializer = deserializer,
                        SavedEntities = savedEntities,
                        SavedLinks = savedLinks,
                        ExistingEntities = existing,
                    }
                    .Run();

                existing.Dispose();

                oldEntities.AddRange(existingEntities);
                newEntities.AddRange(existingEntities);
            }
            else
            {
                new DeserializeJob
                    {
                        Deserializer = deserializer,
                        SavedEntities = savedEntities,
                        SavedLinks = savedLinks,
                    }
                    .Run();

                // can't use query because of linked entity groups
                this.System.EntityManager.DestroyEntity(this.savablePrefabQuery.ToEntityArray(Allocator.Temp));
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
                    this.System.EntityManager.AddComponent<ToInitialize>(prefabLookup.GetValueArray(Allocator.Temp));
                }

                using var saved = savedEntities.GetEnumerator();
                while (saved.MoveNext())
                {
                    var current = saved.Current;
                    var type = current.Key;
                    var entities = current.Value;
                    var prefab = prefabLookup[type]; // validated in the job
                    var start = newEntities.Length;

                    newEntities.ResizeUninitialized(start + entities.Length);
                    oldEntities.AddRange(entities.Ptr, entities.Length);

                    this.System.EntityManager.Instantiate(prefab, newEntities.AsArray().GetSubArray(start, entities.Length));

                    current.Value.Dispose();
                }

                // Add back their SceneSection shared component if it should exist
                if (hasSceneSection)
                {
                    using (new ProfilerMarker("AddSharedComponentData").Auto())
                    {
                        var sceneSection = this.System.EntityManager.GetSharedComponent<SceneSection>(scdIndex);
                        this.System.EntityManager.AddSharedComponent(this.instantiateQuery, sceneSection);
                    }

                    using (new ProfilerMarker("RemoveComponent").Auto())
                    {
                        // TODO we can probably defer this
                        this.System.EntityManager.RemoveComponent<ToInitialize>(this.initializedQuery);
                    }
                }
            }

            // We want to both read and write to this hashmap in RemapLinks so we need to clone it
            var entityMappingClone = new NativeParallelHashMap<Entity, Entity>(0, this.System.WorldUpdateAllocator);

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

            this.saveableLinks.Update(ref this.System);

            dependency = new RemapLinks
                {
                    EntityMappingWriter = entityMap.EntityMapping.AsParallelWriter(),
                    EntityPartialMappingWriter = entityMap.EntityPartialMapping.AsParallelWriter(),
                    EntityMapping = entityMappingClone,
                    Links = this.saveableLinks,
                }
                .ScheduleParallel(savedLinks, 256, dependency);

            return dependency;
        }

        private NativeParallelHashMap<SavablePrefab, Entity> CreatePrefabLookup(Allocator allocator)
        {
            this.entityHandle.Update(ref this.System);
            this.savablePrefabHandle.Update(ref this.System);

            this.System.EntityManager.GetAllUniqueSharedComponents<SavablePrefab>(out var savables, allocator);
            var lookup = new NativeParallelHashMap<SavablePrefab, Entity>(savables.Length, allocator);

            new CreatePrefabLookupJob
                {
                    Lookup = lookup,
                    EntityHandle = this.entityHandle,
                    SavableHandle = this.savablePrefabHandle,
                }
                .Run(this.prefabQuery);

            return lookup;
        }

        private struct CreatePrefabLookupJob : IJobChunk
        {
            public NativeParallelHashMap<SavablePrefab, Entity> Lookup;

            [ReadOnly]
            public EntityTypeHandle EntityHandle;

            [ReadOnly]
            public SharedComponentTypeHandle<SavablePrefab> SavableHandle;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(this.EntityHandle);
                var savablePrefab = chunk.GetSharedComponent(this.SavableHandle);

                if (!this.Lookup.TryAdd(savablePrefab, entities[0]) || entities.Length > 1)
                {
                    Debug.LogError(
                        $"More than one prefab of savable index {savablePrefab.Value0},{savablePrefab.Value1},{savablePrefab.Value2},{savablePrefab.Value3}, found in world, using first");
                }
            }
        }

        [BurstCompile]
        private struct SerializeJob : IJob
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;

            [ReadOnly]
            public EntityTypeHandle Entity;

            [ReadOnly]
            public SharedComponentTypeHandle<SavablePrefab> SavablePrefabHandle;

            [ReadOnly]
            public BufferTypeHandle<SavableLinks> SavableLinksHandle;

            public Serializer Serializer;

            public ulong Key;

            public void Execute()
            {
                // TODO precompute capacity
                var saveIdx = this.Serializer.Allocate<HeaderSaver>();
                var savableIdx = this.Serializer.Allocate<HeaderSavable>();

                var entityCount = 0;

                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(this.SavablePrefabHandle))
                    {
                        continue;
                    }

                    var savablePrefab = chunk.GetSharedComponent(this.SavablePrefabHandle);

                    var entities = chunk.GetNativeArray(this.Entity);
                    var savableLinks = chunk.GetBufferAccessor(ref this.SavableLinksHandle);
                    entityCount += entities.Length;

                    this.Serializer.Add(new HeaderChunk
                    {
                        SavablePrefab = savablePrefab,
                        Length = entities.Length,
                        SavableLinks = savableLinks.Length > 0,
                    });

                    this.Serializer.AddBuffer(entities);

                    for (var i = 0; i < savableLinks.Length; i++)
                    {
                        var links = savableLinks[i];
                        this.Serializer.Add(links.Length);
                        this.Serializer.AddBuffer((SavableLinks*)links.GetUnsafeReadOnlyPtr(), links.Length);
                    }
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
                    Count = entityCount,
                };
            }
        }

        [BurstCompile]
        private struct AllocateLists : IJob
        {
            public NativeParallelHashMap<SavablePrefab, UnsafeList<Entity>> SavedEntities;

            [ReadOnly]
            public NativeParallelHashMap<SavablePrefab, Entity> PrefabLookup;

            public void Execute()
            {
                using var e = this.PrefabLookup.GetEnumerator();
                while (e.MoveNext())
                {
                    this.SavedEntities.Add(e.Current.Key, new UnsafeList<Entity>(0, Allocator.Persistent));
                }
            }
        }

        [BurstCompile]
        private struct DeserializeJob : IJob
        {
            [ReadOnly]
            public Deserializer Deserializer;

            public NativeParallelHashMap<SavablePrefab, UnsafeList<Entity>> SavedEntities;
            public NativeParallelHashMap<Entity, UnsafeList<SavableLinks>> SavedLinks;

            public void Execute()
            {
                this.Deserializer.Offset<HeaderSaver>();
                var header = this.Deserializer.Read<HeaderSavable>();

                var index = 0;

                while (index < header.Count)
                {
                    var headerChunk = this.Deserializer.Read<HeaderChunk>();
                    index += headerChunk.Length;

                    var savablePrefab = headerChunk.SavablePrefab;

                    // Entities are grouped per type so we can instantiate all entities of a type in a single Instantiate call
                    // This is significantly faster than doing it per chunk.
                    var prefabFound = this.SavedEntities.TryGetValue(savablePrefab, out var entities);

                    // Even if prefab is not found we still need to read all the data to move the indexer
                    if (!prefabFound)
                    {
                        Debug.LogError($"Prefab missing for {savablePrefab}, {headerChunk.Length} will not be deserialized");
                    }

                    var entityPtr = this.Deserializer.ReadBuffer<Entity>(headerChunk.Length);

                    if (headerChunk.SavableLinks)
                    {
                        for (var i = 0; i < headerChunk.Length; i++)
                        {
                            var linksLength = this.Deserializer.Read<int>();
                            var data = this.Deserializer.ReadBuffer<SavableLinks>(linksLength);

                            // TODO this is gross
                            if (prefabFound)
                            {
                                this.SavedLinks.Add(entityPtr[i], new UnsafeList<SavableLinks>(data, linksLength));
                            }
                        }
                    }

                    if (prefabFound)
                    {
                        entities.AddRange(entityPtr, headerChunk.Length);
                        this.SavedEntities[savablePrefab] = entities;
                    }
                }
            }
        }

        [BurstCompile]
        private struct DeserializeWithCheckJob : IJob
        {
            [ReadOnly]
            public Deserializer Deserializer;

            public NativeParallelHashMap<SavablePrefab, UnsafeList<Entity>> SavedEntities;
            public NativeParallelHashMap<Entity, UnsafeList<SavableLinks>> SavedLinks;

            public NativeParallelHashSet<Entity> ExistingEntities;

            public void Execute()
            {
                this.Deserializer.Offset<HeaderSaver>();
                var header = this.Deserializer.Read<HeaderSavable>();

                var index = 0;

                while (index < header.Count)
                {
                    var headerChunk = this.Deserializer.Read<HeaderChunk>();
                    index += headerChunk.Length;

                    var savablePrefab = headerChunk.SavablePrefab;

                    // Entities are grouped per type so we can instantiate all entities of a type in a single Instantiate call
                    // This is significantly faster than doing it per chunk.
                    var prefabFound = this.SavedEntities.TryGetValue(savablePrefab, out var entities);

                    // Even if prefab is not found we still need to read all the data to move the indexer
                    if (!prefabFound)
                    {
                        Debug.LogError($"Prefab missing for {savablePrefab}, {headerChunk.Length} will not be deserialized");
                    }

                    var entityPtr = this.Deserializer.ReadBuffer<Entity>(headerChunk.Length);

                    if (headerChunk.SavableLinks)
                    {
                        for (var i = 0; i < headerChunk.Length; i++)
                        {
                            var linksLength = this.Deserializer.Read<int>();
                            var data = this.Deserializer.ReadBuffer<SavableLinks>(linksLength);

                            // TODO this is gross
                            if (prefabFound)
                            {
                                this.SavedLinks.Add(entityPtr[i], new UnsafeList<SavableLinks>(data, linksLength));
                            }
                        }
                    }

                    if (prefabFound)
                    {
                        for (var i = 0; i < headerChunk.Length; i++)
                        {
                            var entity = entityPtr[i];
                            if (!this.ExistingEntities.Contains(entity))
                            {
                                entities.Add(entity);
                            }
                        }

                        this.SavedEntities[savablePrefab] = entities;
                    }
                }
            }
        }

        [BurstCompile]
        private struct PopulateEntityMaps : IJob
        {
            public NativeParallelHashMap<Entity, Entity> EntityMapping;
            public NativeParallelHashMap<int, Entity> EntityPartialMapping;

            [ReadOnly]
            public NativeParallelHashMap<Entity, UnsafeList<SavableLinks>> Links;

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
        private struct RemapLinks : IJobHashMapVisitKeyValue
        {
            public NativeParallelHashMap<Entity, Entity>.ParallelWriter EntityMappingWriter;

            public NativeParallelHashMap<int, Entity>.ParallelWriter EntityPartialMappingWriter;

            [ReadOnly]
            public NativeParallelHashMap<Entity, Entity> EntityMapping;

            [ReadOnly]
            public BufferLookup<SavableLinks> Links;

            public void ExecuteNext(byte* keys, byte* values, int entryIndex)
            {
                this.Read(entryIndex, keys, values, out Entity key, out UnsafeList<SavableLinks> value);
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

        private struct HeaderChunk
        {
            public SavablePrefab SavablePrefab;
            public int Length;
            public bool SavableLinks;

            // Reserved for future
            public fixed byte Padding[3];
        }

        private struct HeaderSavable
        {
            public int Count;
            public fixed byte Padding[4];
        }

        private struct ToInitialize : IComponentData
        {
        }
    }
}
