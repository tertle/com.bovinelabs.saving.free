// <copyright file="SavableSceneRecordEntity.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Data
{
    using System;
    using Unity.Entities;

    /// <summary>
    /// This record is used to track deleted sub scene objects.
    /// See also <see cref="SavableSceneRecord"/>. These buffers are kept separate for fast hashmap building.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SavableSceneRecordEntity : IBufferElementData, IEquatable<SavableSceneRecordEntity>
    {
        public Entity Value;

        public bool Equals(SavableSceneRecordEntity other)
        {
            return this.Value == other.Value;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return this.Value.GetHashCode() * 397;
            }
        }
    }
}
