// <copyright file="SaveSampleUI.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Saving
{
    using Unity.Entities;
    using UnityEngine;
    using UnityEngine.UI;

    public class SaveSampleUI : MonoBehaviour
    {
        [SerializeField]
        private Text text;

        private void Update()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<TestComponentData>(),
                ComponentType.ReadOnly<TestComponentBuffer>());

            if (query.IsEmptyIgnoreFilter)
            {
                return;
            }

            var buffer = em.GetBuffer<TestComponentBuffer>(query.GetSingletonEntity());
            var component = query.GetSingleton<TestComponentData>();

            this.text.text = "TestComponentData\n" +
                       $"  Value0 = {component.Value0}\n" +
                       $"  Value1 = {component.Value1} [Ignored]\n" +
                       $"  Value2 = {component.Value2}\n" +
                       "TestComponentBuffer";

            for (var i = 0; i < buffer.Length; i++)
            {
                this.text.text += $"\n  ({i}) Value = {buffer[i].Value}";
            }
        }
    }
}
