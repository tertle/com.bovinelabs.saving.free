// <copyright file="TestComponentData.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Saving
{
    using Unity.Entities;
    using UnityEngine;

    [Save]
    public struct TestComponentData : IComponentData
    {
        public int Value0;
        [SaveIgnore] public int Value1;
        public byte Value2;
    }

    public class TestComponentDataAuthoring : MonoBehaviour
    {
        public int Value0;
        [SaveIgnore] public int Value1;
        public byte Value2;
    }

    public class TestComponentDataBaker : Baker<TestComponentDataAuthoring>
    {
        public override void Bake(TestComponentDataAuthoring authoring)
        {
            AddComponent(new TestComponentData
            {
                Value0 = authoring.Value0,
                Value1 = authoring.Value1,
                Value2 = authoring.Value2,
            });
        }
    }
}
