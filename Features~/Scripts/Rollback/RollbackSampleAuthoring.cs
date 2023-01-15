// <copyright file="RollbackSampleAuthoring.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Rollback
{
    using Unity.Entities;
    using UnityEngine;

    public struct RollbackSample : IComponentData
    {
    }

    public class RollbackSampleAuthoring : MonoBehaviour
    {
    }

    public class RollbackSampleBaker : Baker<RollbackSampleAuthoring>
    {
        public override void Bake(RollbackSampleAuthoring authoring)
        {
            AddComponent<RollbackSample>();
        }
    }
}
