// <copyright file="SavableSaver.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using BovineLabs.Core.Utility;
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

        public abstract (Serializer Serializer, JobHandle Dependency) Serialize(ref SystemState state, NativeList<ArchetypeChunk> chunks, JobHandle dependency);

        public abstract JobHandle Deserialize(ref SystemState state, Deserializer deserializer, EntityMap entityMap, JobHandle dependency);
    }
}
