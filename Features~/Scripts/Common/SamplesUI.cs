// <copyright file="SamplesUI.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Common
{
    using System;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Rendering;
    using Unity.Scenes;
    using Unity.Transforms;
    using UnityEngine;
    using Random = UnityEngine.Random;

    public class SamplesUI : MonoBehaviour
    {
        [SerializeField]
        private GameObject backButton;

        [SerializeField]
        private Sample[] samples;

        [SerializeField]
        private GameObject menuUI;

        public void StartSample(int index)
        {
            foreach (var scene in this.samples[index].Scenes)
            {
                SceneSystem.LoadSceneAsync(World.DefaultGameObjectInjectionWorld.Unmanaged, scene.SceneGUID);
            }

            foreach (var go in this.samples[index].Dependencies)
            {
                go.SetActive(true);
            }

            this.menuUI.SetActive(false);
            this.backButton.SetActive(true);
        }

        public void CloseSamples()
        {
            foreach (var sample in this.samples)
            {
                foreach (var scene in sample.Scenes)
                {
                    SceneSystem.UnloadScene(World.DefaultGameObjectInjectionWorld.Unmanaged, scene.SceneGUID);
                }

                foreach (var go in sample.Dependencies)
                {
                    go.SetActive(false);
                }
            }

            this.menuUI.SetActive(true);
            this.backButton.SetActive(false);

            // Generally entities should exist in sub scenes but if they don't they need to be destroyed when changing scene.
            using var savables = World.DefaultGameObjectInjectionWorld.EntityManager.CreateEntityQuery(typeof(SavablePrefab));
            World.DefaultGameObjectInjectionWorld.EntityManager.DestroyEntity(savables);
        }

        public void RandomizeScene()
        {
            RandomizeSystem.RunOnce();
        }

        public void CreateEntity()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Savable>(), ComponentType.ReadOnly<Prefab>());

            var entities = query.ToEntityArray(Allocator.Temp);
            if (entities.Length == 0)
            {
                return;
            }

            var min = Util.Min;
            var max = Util.Max;

            var prefab = entities[Random.Range(0, entities.Length)];
            var entity = em.Instantiate(prefab);
            var tr = LocalTransform.FromPosition(new float3(Random.Range(min.x, max.x), Random.Range(min.y, max.y), Random.Range(min.z, max.z)));
            em.SetComponentData(entity, tr);
        }

        public void DestroyEntity()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Savable>(), ComponentType.Exclude<SavablePrefabRecord>(), ComponentType.Exclude<SavableSceneRecord>());

            var entities = query.ToEntityArray(Allocator.Temp);
            if (entities.Length == 0)
            {
                return;
            }

            var entity = entities[Random.Range(0, entities.Length)];
            em.DestroyEntity(entity);
        }

        public void AddComponent()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<SavablePrefab>(), ComponentType.Exclude<URPMaterialPropertyBaseColor>());

            if (query.IsEmptyIgnoreFilter)
            {
                return;
            }

            var e = query.ToEntityArray(Allocator.Temp);
            em.AddComponentData(e[UnityEngine.Random.Range(0, e.Length)], new URPMaterialPropertyBaseColor { Value = new float4(0.7f, 0.7f, 0, 1) });
        }

        public void RemoveComponent()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<URPMaterialPropertyBaseColor>());

            if (query.IsEmptyIgnoreFilter)
            {
                return;
            }

            var e = query.ToEntityArray(Allocator.Temp);
            em.RemoveComponent<URPMaterialPropertyBaseColor>(e[UnityEngine.Random.Range(0, e.Length)]);
        }

        private void Start()
        {
            this.CloseSamples();
        }

        [Serializable]
        private struct Sample
        {
            public SubScene[] Scenes;
            public GameObject[] Dependencies;
        }
    }
}
