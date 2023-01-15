// <copyright file="FilterComponentAuthoring.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Filter
{
    using Unity.Entities;
    using UnityEngine;

    public struct FilterComponent : IComponentData
    {
    }

    public class FilterComponentAuthoring : MonoBehaviour
    {
    }

    public class FilterComponentBaker : Baker<FilterComponentAuthoring>
    {
        public override void Bake(FilterComponentAuthoring authoring)
        {
            AddComponent<FilterComponent>();
        }
    }
}
