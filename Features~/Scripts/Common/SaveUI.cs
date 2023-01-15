// <copyright file="SaveUI.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Common
{
    using BovineLabs.Saving.Samples.Filter;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Scenes;
    using UnityEngine;

    public class SaveUI : MonoBehaviour
    {
        public void Save()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            em.CreateEntity(typeof(Save));
        }

        public void Load()
        {
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<SaveFileSystem>().Load(default);
        }

        public void LoadFromDisk()
        {

            var bytes = Resources.Load<TextAsset>("save").bytes;
            var nativeArray = new NativeArray<byte>(bytes, Allocator.Persistent);

            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<SaveFileSystem>().Load(nativeArray, default);
        }

        public void CloseSubScene()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<SceneSectionData>(), ComponentType.ReadOnly<RequestSceneLoaded>());
            em.RemoveComponent<RequestSceneLoaded>(query);

            foreach (var e in query.ToEntityArray(Allocator.Temp))
            {
                Debug.Log(e);
            }
        }

        public void OpenSubScene()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<SceneSectionData>(), ComponentType.Exclude<RequestSceneLoaded>());
            em.AddComponent<RequestSceneLoaded>(query);

            foreach (var e in query.ToEntityArray(Allocator.Temp))
            {
                Debug.Log(e);
            }
        }

        public void SaveCloseSubScene(SubScene subScene)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>(), ComponentType.ReadOnly<RequestSceneLoaded>());

            var entities = query.ToEntityArray(Allocator.Temp);
            var sceneSectionDatas = query.ToComponentDataArray<SceneReference>(Allocator.Temp);

            for (var index = 0; index < entities.Length; index++)
            {
                var sceneSection = sceneSectionDatas[index];
                if (subScene != null && sceneSection.SceneGUID != subScene.SceneGUID)
                    continue;

                var entity = em.CreateEntity(typeof(SaveCloseSubScene));
                em.SetComponentData(entity, new SaveCloseSubScene { SubScene = entities[index] });
            }
        }

        public void OpenLoadSubScene(SubScene subScene)
        {
            if (subScene == null)
            {
                return;
            }

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<SceneReference>(), ComponentType.Exclude<RequestSceneLoaded>());

            var entities = query.ToEntityArray(Allocator.Temp);
            var sceneSectionDatas = query.ToComponentDataArray<SceneReference>(Allocator.Temp);

            for (var index = 0; index < entities.Length; index++)
            {
                var sceneSection = sceneSectionDatas[index];
                if (sceneSection.SceneGUID != subScene.SceneGUID)
                {
                    continue;
                }

                var entity = em.CreateEntity(typeof(OpenLoadSubScene));
                em.SetComponentData(entity, new OpenLoadSubScene { SubScene = entities[index] });
            }
        }

        public void SaveFilter()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            em.CreateEntity(typeof(Save), typeof(FilterComponentSave)); // Tell it to use the filter save system instead
        }

        public void SaveSharedFilter()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            em.CreateEntity(typeof(Save), typeof(FilterSharedComponentSave)); // Tell it to use the filter save system instead
        }

        public void LoadFilter()
        {
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<SaveFileSystem>().Load(typeof(FilterComponentSave));
        }

        public void LoadSharedFilter()
        {
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<SaveFileSystem>().Load(typeof(FilterSharedComponentSave));
        }
    }
}
