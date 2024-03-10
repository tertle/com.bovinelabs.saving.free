// <copyright file="RandomizeSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Common
{
    using BovineLabs.Saving.Samples.Saving;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;

    [RequireMatchingQueriesForUpdate]
    public partial class RandomizeSystem : SystemBase
    {
        public static void RunOnce()
        {
            var rand = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<RandomizeSystem>();
            rand.Enabled = true;
            rand.Update();
        }

        protected override void OnCreate()
        {
            this.Enabled = false;
        }

        protected override void OnUpdate()
        {
            var seed = (uint)UnityEngine.Random.Range(1, 100000);

            this.Entities.WithAll<Savable>().ForEach((int entityInQueryIndex, ref LocalTransform tr) =>
                {
                    var random = Random.CreateFromIndex((uint)(seed + entityInQueryIndex));
                    tr.Position = random.NextFloat3(Util.Min, Util.Max);
                    tr.Rotation = random.NextQuaternionRotation();
                })
                .ScheduleParallel();

            seed += 1;

            this.Entities.WithAll<Savable>().ForEach((int entityInQueryIndex, ref TestComponentData tcd) =>
                {
                    var random = Random.CreateFromIndex((uint)(seed + entityInQueryIndex));
                    tcd.Value0 = random.NextInt();
                    tcd.Value1 = random.NextInt();
                    tcd.Value2 = (byte)(random.NextInt() % 255);
                })
                .ScheduleParallel();

            seed += 1;

            this.Entities.WithAll<Savable>().ForEach((int entityInQueryIndex, ref DynamicBuffer<TestComponentBuffer> tcb) =>
                {
                    var random = Random.CreateFromIndex((uint)(seed + entityInQueryIndex));
                    tcb.ResizeUninitialized(random.NextInt(3, 8));
                    for (var i = 0; i < tcb.Length; i++)
                    {
                        tcb[i] = new TestComponentBuffer { Value = random.NextInt() };
                    }
                })
                .ScheduleParallel();

            this.Enabled = false;
        }
    }
}
