// <copyright file="SavableScene.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using Unity.Entities;

    public struct SavableScene : IComponentData, IEquatable<SavableScene>
    {
        public ulong TargetObjectId;
        public ulong TargetPrefabId;

        public bool Equals(SavableScene other)
        {
            return this.TargetObjectId == other.TargetObjectId && this.TargetPrefabId == other.TargetPrefabId;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (int)this.TargetObjectId;
                hashCode = hashCode * 397 ^ (int)this.TargetPrefabId;
                return hashCode;
            }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"TargetObjectId={this.TargetObjectId}, TargetPrefabId={this.TargetPrefabId}";
        }
    }
}
