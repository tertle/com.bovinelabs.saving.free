// <copyright file="EntityMap.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;

    /// <summary> A collection of maps that can convert serialized entities to the deserialized. </summary>
    public struct EntityMap
    {
        [ReadOnly]
        private NativeParallelHashMap<Entity, Entity> entityMapping;

        [ReadOnly]
        private NativeParallelHashMap<int, Entity> entityPartialMapping;

        [ReadOnly]
        private NativeParallelHashMap<SavableScene, Entity> entitySavableMapping;

        internal EntityMap(Allocator allocator)
        {
            this.entityMapping = new NativeParallelHashMap<Entity, Entity>(0, allocator);
            this.entityPartialMapping = new NativeParallelHashMap<int, Entity>(0, allocator);
            this.entitySavableMapping = new NativeParallelHashMap<SavableScene, Entity>(0, allocator);
        }

        internal NativeParallelHashMap<Entity, Entity> EntityMapping => this.entityMapping;

        internal NativeParallelHashMap<int, Entity> EntityPartialMapping => this.entityPartialMapping;

        internal NativeParallelHashMap<SavableScene, Entity> EntitySavableMapping => this.entitySavableMapping;

        public Entity this[Entity entitySaved] => this.entityMapping[entitySaved];

        public Entity this[int entitySaved] => this.entityPartialMapping[entitySaved];

        public bool TryGetEntity(Entity entitySaved, out Entity entityNew)
        {
            return this.entityMapping.TryGetValue(entitySaved, out entityNew);
        }

        public bool TryGetEntity(int entitySaved, out Entity entityNew)
        {
            return this.entityPartialMapping.TryGetValue(entitySaved, out entityNew);
        }

        public bool TryGetEntity(SavableScene entityId, out Entity entity)
        {
            return this.entitySavableMapping.TryGetValue(entityId, out entity);
        }

        internal void Dispose(JobHandle dependency)
        {
            this.entityMapping.Dispose(dependency);
            this.entityPartialMapping.Dispose(dependency);
            this.entitySavableMapping.Dispose(dependency);
        }
    }
}
