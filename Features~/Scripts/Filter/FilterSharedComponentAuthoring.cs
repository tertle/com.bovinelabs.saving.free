// <copyright file="FilterSharedComponentAuthoring.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Filter
{
    using Unity.Entities;
    using UnityEngine;

    public struct FilterSharedComponent : ISharedComponentData
    {
        public int Value;
    }

    public class FilterSharedComponentAuthoring : MonoBehaviour
    {
        [SerializeField]
        private int value;

        public int Value => this.value;
    }

    public class FilterSharedComponentBaker : Baker<FilterSharedComponentAuthoring>
    {
        public override void Bake(FilterSharedComponentAuthoring authoring)
        {
            this.AddSharedComponent(this.GetEntity(TransformUsageFlags.None), new FilterSharedComponent { Value = authoring.Value });
        }
    }
}
