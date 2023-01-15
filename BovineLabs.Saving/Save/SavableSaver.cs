// <copyright file="SavableSaver.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;

    /// <summary> Base class for handling the core prefab, scene etc saving management. </summary>
    public abstract class SavableSaver : ISaver
    {
        /// <summary> Initializes a new instance of the <see cref="SavableSaver"/> class. </summary>
        /// <param name="type"> The component type being saved. </param>
        protected SavableSaver(ComponentType type)
        {
            this.Key = TypeManager.GetTypeInfo(type.TypeIndex).StableTypeHash;
        }

        /// <inheritdoc />
        public ulong Key { get; }

        /// <inheritdoc/>
        public abstract (Serializer Serializer, JobHandle Dependency) Serialize(NativeList<ArchetypeChunk> chunks, JobHandle dependency);

        /// <inheritdoc/>
        public abstract JobHandle Deserialize(Deserializer deserializer, EntityMap entityMap, JobHandle dependency);
    }
}
