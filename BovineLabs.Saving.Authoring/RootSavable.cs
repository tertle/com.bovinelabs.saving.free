// <copyright file="RootSavable.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Authoring
{
    using Unity.Entities;
    using UnityEditor;

    [BakingType]
    internal struct RootSavable : IComponentData
    {
        public GlobalObjectId GlobalObjectId;
        public bool IsPrefab;
    }
}
