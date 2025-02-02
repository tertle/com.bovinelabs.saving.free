// <copyright file="SavableSceneRecord.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Data
{
    using System;
    using Unity.Entities;

    /// <summary>
    /// This record is used to track deleted sub scene objects.
    /// See also <see cref="SavableSceneRecordEntity"/>. These buffers are kept separate for fast hashmap building.
    /// </summary>/// <summary> This record is used to track deleted sub scene objects. </summary>
    [InternalBufferCapacity(0)]
    public struct SavableSceneRecord : IBufferElementData, IEquatable<SavableSceneRecord>
    {
        public ulong TargetObjectId;
        public ulong TargetPrefabId;

        public bool Equals(SavableSceneRecord other)
        {
            return this.TargetObjectId == other.TargetObjectId && this.TargetPrefabId == other.TargetPrefabId;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (this.TargetObjectId.GetHashCode() * 397) ^ this.TargetPrefabId.GetHashCode();
            }
        }
    }
}
