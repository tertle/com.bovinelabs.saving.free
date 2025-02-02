// <copyright file="SavableAuthoring.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Authoring
{
    using System.Linq;
    using BovineLabs.Saving.Data;
    using Unity.Entities;
    using UnityEditor;
    using UnityEngine;

    public class SavableAuthoring : MonoBehaviour
    {
        private class Baker : Baker<SavableAuthoring>
        {
            /// <inheritdoc/>
            public override void Bake(SavableAuthoring authoring)
            {
                var entity = this.GetEntity(TransformUsageFlags.None);

                this.AddComponent<Savable>(entity);

                if (authoring.transform.parent != null)
                {
                    return;
                }

                this.AddComponent(entity, new RootSavable
                {
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(authoring), // TODO faster to batch do this with GlobalObjectIdentifiersToObjectsSlow
                    IsPrefab = !authoring.gameObject.scene.IsValid(),
                });

                var childrenLinks = authoring.GetComponentsInChildren<SavableAuthoring>(false).Where(s => s != authoring).ToArray();
                if (childrenLinks.Length > 0)
                {
                    var savableLinks = this.AddBuffer<SavableLinks>(entity);

                    foreach (var link in childrenLinks)
                    {
                        var linkEntity = this.GetEntity(link.gameObject, TransformUsageFlags.None);
                        savableLinks.Add(new SavableLinks { Entity = linkEntity, LinkID = GlobalObjectId.GetGlobalObjectIdSlow(link).targetObjectId });
                    }
                }
            }
        }
    }
}
