// <copyright file="InstantiateAuthoring.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Common
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using UnityEngine;

    public class InstantiateAuthoring : MonoBehaviour
    {
        public GameObject Prefab;
    }

    public struct Instantiate : IComponentData
    {
        public Entity Prefab;
    }

    public class InstantiateBaker : Baker<InstantiateAuthoring>
    {
        public override void Bake(InstantiateAuthoring authoring)
        {
            this.AddComponent(
                this.GetEntity(TransformUsageFlags.None),
                new Instantiate
                {
                    Prefab = this.GetEntity(authoring.Prefab, TransformUsageFlags.None),
                });
        }
    }

    public partial struct InstantiateSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (i, e) in SystemAPI.Query<Instantiate>().WithEntityAccess())
            {
                ecb.Instantiate(i.Prefab);
                ecb.DestroyEntity(e);
            }

            ecb.Playback(state.EntityManager);
        }
    }
}
