// <copyright file="TestComponentBufferAuthoring.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Saving
{
    using Unity.Entities;
    using UnityEngine;

    [Save]
    public struct TestComponentBuffer : IBufferElementData
    {
        public int Value;
    }

    public class TestComponentBufferAuthoring : MonoBehaviour
    {
        public int[] Value;

        private class Baker : Baker<TestComponentBufferAuthoring>
        {
            public override void Bake(TestComponentBufferAuthoring authoring)
            {
                var buffer = this.AddBuffer<TestComponentBuffer>(this.GetEntity(TransformUsageFlags.None)).Reinterpret<int>();
                if (authoring.Value != null)
                {
                    foreach (var v in authoring.Value)
                    {
                        buffer.Add(v);
                    }
                }
            }
        }
    }
}
