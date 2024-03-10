// <copyright file="RollbackSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Rollback
{
    using System.Collections.Generic;
    using BovineLabs.Saving.Samples.Common;
    using Unity.Collections;
    using Unity.Entities;

    [RequireMatchingQueriesForUpdate]
    public partial class RollbackSystem : SystemBase
    {
        private readonly List<NativeArray<byte>> saveData = new();
        private int index = -1;

        private State state;
        private EntityQuery saveDataQuery;

        private enum State
        {
            None,
            Paused,
            PlayForward,
            PlayBackward,
        }

        public void StepForward(bool reset = true)
        {
            if (reset)
            {
                this.state = State.Paused;
            }

            if (this.index >= this.saveData.Count - 1)
            {
                return;
            }

            var sd = this.saveData[++this.index];

            var entity = this.EntityManager.CreateEntity(typeof(Load), typeof(RollbackSave));
            var buffer = SystemAPI.GetBuffer<Load>(entity);
            buffer.Reinterpret<byte>().AddRange(sd);
        }

        public void StepBackwards(bool reset = true)
        {
            if (reset)
            {
                this.state = State.Paused;
            }

            if (this.index <= 0)
            {
                return;
            }

            var sd = this.saveData[--this.index];

            var entity = this.EntityManager.CreateEntity(typeof(Load), typeof(RollbackSave));
            var buffer = SystemAPI.GetBuffer<Load>(entity);
            buffer.Reinterpret<byte>().AddRange(sd);
        }

        public void PlayBackwards()
        {
            this.state = State.PlayBackward;
        }

        public void PlayForward()
        {
            this.state = State.PlayForward;
        }

        public void Reset(bool paused)
        {
            this.state = paused ? State.Paused : State.None;
        }

        /// <inheritdoc/>
        protected override void OnCreate()
        {
            this.RequireForUpdate<RollbackSample>();

            this.saveDataQuery = this.GetEntityQuery(ComponentType.ReadOnly<SaveBuffer>(), ComponentType.ReadOnly<RollbackSave>());
        }

        /// <inheritdoc/>
        protected override void OnDestroy()
        {
            foreach (var save in this.saveData)
            {
                save.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override void OnUpdate()
        {
            this.AddDataFromSave();

            switch (this.state)
            {
                case State.PlayForward:
                    this.StepForward(false);
                    break;
                case State.PlayBackward:
                    this.StepBackwards(false);
                    break;
                case State.None:
                    this.EntityManager.CreateEntity(typeof(Save), typeof(RollbackSave));
                    break;
            }
        }

        private void AddDataFromSave()
        {
            if (this.saveDataQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var entity = this.saveDataQuery.GetSingletonEntity();

            var buffer = SystemAPI.GetBuffer<SaveBuffer>(entity);

            for (var i = this.saveData.Count - 1; i > this.index; i--)
            {
                this.saveData[i].Dispose();
                this.saveData.RemoveAtSwapBack(i);
            }

            this.saveData.Add(buffer.Reinterpret<byte>().ToNativeArray(Allocator.Persistent));
            this.index = this.saveData.Count - 1;

            this.EntityManager.DestroyEntity(entity);
        }
    }
}
