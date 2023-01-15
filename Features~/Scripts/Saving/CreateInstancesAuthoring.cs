// <copyright file="CreateInstancesAuthoring.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Saving
{
    using System;
    using BovineLabs.Saving.Samples.Common;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using UnityEngine;
    using Random = UnityEngine.Random;

    [Serializable]
    public struct CreateInstances : IBufferElementData
    {
        public Entity Prefab;
        public int Count;
        public float3 MinPosition;
        public float3 MaxPosition;
    }

    public class CreateInstancesAuthoring : MonoBehaviour
    {
        [SerializeField]
        private Pair[] spawners;

        public Pair[] Spawners => this.spawners;


        [Serializable]
        public class Pair
        {
            public GameObject Prefab;
            public int Count;
            public Vector3 MinPosition = Util.Min;
            public Vector3 MaxPosition = Util.Max;
        }
    }

    public class CreateInstanceBaker : Baker<CreateInstancesAuthoring>
    {
        public override void Bake(CreateInstancesAuthoring authoring)
        {
            var spawner = this.AddBuffer<CreateInstances>();

            foreach (var p in authoring.Spawners)
            {
                spawner.Add(new CreateInstances
                {
                    Prefab = this.GetEntity(p.Prefab),
                    Count = p.Count,
                    MinPosition = p.MinPosition,
                    MaxPosition = p.MaxPosition,
                });
            }
        }
    }

    [BurstCompile]
    public partial struct CreateInstancesSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
        }

        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var r = new Unity.Mathematics.Random((uint)Random.Range(1, 10000));

            var query = SystemAPI.QueryBuilder().WithAll<CreateInstances>().Build();
            query.CompleteDependency();
            var entities = query.ToEntityArray(Allocator.Temp);

            foreach (var entity in entities)
            {
                var spawners = state.EntityManager.GetBuffer<CreateInstances>(entity).ToNativeArray(Allocator.Temp);
                foreach (var spawner in spawners)
                {
                    var newEntities = state.EntityManager.Instantiate(spawner.Prefab, spawner.Count, Allocator.Temp);

                    if (state.EntityManager.HasComponent<LocalTransform>(spawner.Prefab))
                    {
                        foreach (var e in newEntities)
                        {
                            state.EntityManager.SetComponentData(e, LocalTransform.FromPosition(r.NextFloat3(spawner.MinPosition, spawner.MaxPosition)));
                        }
                    }
                    else if (state.EntityManager.HasComponent<LocalToWorld>(spawner.Prefab))
                    {
                        foreach (var e in newEntities)
                        {
                            state.EntityManager.SetComponentData(e, new LocalToWorld { Value = float4x4.Translate(r.NextFloat3(spawner.MinPosition, spawner.MaxPosition)) });
                        }
                    }
                }

                state.EntityManager.DestroyEntity(entity);
            }

            state.Enabled = false;
        }
    }
}
