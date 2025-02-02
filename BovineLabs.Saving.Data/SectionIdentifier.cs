// <copyright file="SectionIdentifier.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Data
{
    using System;
    using Unity.Collections;
    using Unity.Entities;

    public readonly struct SectionIdentifier : IEquatable<SectionIdentifier>
    {
        public readonly Hash128 SceneGuid;
        public readonly int SubSectionIndex;

        public SectionIdentifier(Hash128 sceneGuid, int subSectionIndex)
        {
            this.SceneGuid = sceneGuid;
            this.SubSectionIndex = subSectionIndex;
        }

        public bool Equals(SectionIdentifier other)
        {
            return this.SceneGuid.Equals(other.SceneGuid) && this.SubSectionIndex == other.SubSectionIndex;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return this.SceneGuid.GetHashCode() * 397 ^ this.SubSectionIndex;
            }
        }
    }
}
