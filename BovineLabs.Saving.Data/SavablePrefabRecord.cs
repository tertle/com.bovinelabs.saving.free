// <copyright file="SavablePrefabRecord.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Data
{
    using System;
    using Unity.Entities;

    public struct SavablePrefabRecord : IBufferElementData, IEquatable<SavablePrefabRecord>
    {
        public uint Value0;
        public uint Value1;
        public uint Value2;
        public uint Value3;

        public bool Equals(SavablePrefabRecord other)
        {
            return this.Value0 == other.Value0 && this.Value1 == other.Value1 && this.Value2 == other.Value2 && this.Value3 == other.Value3;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (int)this.Value0;
                hashCode = hashCode * 397 ^ (int)this.Value1;
                hashCode = hashCode * 397 ^ (int)this.Value2;
                hashCode = hashCode * 397 ^ (int)this.Value3;
                return hashCode;
            }
        }
    }
}
