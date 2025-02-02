// <copyright file="SavablePrefab.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Data
{
    using System;
    using Unity.Entities;

    public struct SavablePrefab : IComponentData, IEquatable<SavablePrefab>
    {
        public ushort Value;

        public bool Equals(SavablePrefab other)
        {
            return this.Value == other.Value;
        }

        public override int GetHashCode()
        {
            return this.Value.GetHashCode();
        }
    }
}
