// <copyright file="SavableAuthoring.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Authoring
{
    using System.Linq;
    using Unity.Entities;
    using UnityEditor;
    using UnityEngine;

    public class SavableAuthoring : MonoBehaviour
    {
    }

    [TemporaryBakingType]
    internal struct RootSavable : IComponentData
    {
        public GlobalObjectId GlobalObjectId;
        public bool IsPrefab;
    }

    public class SavableBaker : Baker<SavableAuthoring>
    {
        public override void Bake(SavableAuthoring authoring)
        {
            this.AddComponent<Savable>();

            if (authoring.transform.parent != null)
            {
                return;
            }

            this.AddComponent(new RootSavable
            {
                GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(authoring),
                IsPrefab = !authoring.gameObject.scene.IsValid(),
            });

            var childrenLinks = authoring.GetComponentsInChildren<SavableAuthoring>(false).Where(s => s != authoring).ToArray();
            if (childrenLinks.Length > 0)
            {
                var savableLinks = this.AddBuffer<SavableLinks>();

                foreach (var link in childrenLinks)
                {
                    var linkEntity = this.GetEntity(link.gameObject);
                    savableLinks.Add(new SavableLinks { Entity = linkEntity, Value = GlobalObjectId.GetGlobalObjectIdSlow(link).targetObjectId });
                }
            }
        }
    }
}
