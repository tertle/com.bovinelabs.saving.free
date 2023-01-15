// <copyright file="RollbackUI.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Rollback
{
    // using global::Samples.Boids;
    using global::Samples.Boids;
    using Unity.Entities;
    using UnityEngine;
    using UnityEngine.UI;

    public class RollbackUI : MonoBehaviour
    {
        [SerializeField]
        private Button pauseButton;

        [SerializeField]
        private Button[] buttonsToDisablePaused;

        [SerializeField]
        private Sprite pauseIcon;

        [SerializeField]
        private Sprite playIcon;

        public void Pause()
        {
            this.Pause(World.DefaultGameObjectInjectionWorld.Unmanaged.GetExistingSystemState<BoidSystem>().Enabled);
        }

        public void StepForward()
        {
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<RollbackSystem>().StepForward();
        }

        public void StepBackwards()
        {
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<RollbackSystem>().StepBackwards();
        }

        public void PlayForward()
        {
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<RollbackSystem>().PlayForward();
        }

        public void PlayBackward()
        {
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<RollbackSystem>().PlayBackwards();
        }

        private void OnEnable()
        {
            foreach (var b in this.buttonsToDisablePaused)
            {
                b.interactable = false;
            }

            this.Pause(true);
        }

        private void Pause(bool pause)
        {
            ref var bs = ref World.DefaultGameObjectInjectionWorld.Unmanaged.GetExistingSystemState<BoidSystem>();
            ref var sa = ref World.DefaultGameObjectInjectionWorld.Unmanaged.GetExistingSystemState<SampledAnimationClipPlaybackSystem>();

            bs.Enabled = !pause;
            sa.Enabled = !pause;

            this.pauseButton.image.sprite = pause ? this.playIcon : this.pauseIcon;

            foreach (var b in this.buttonsToDisablePaused)
            {
                b.interactable = pause;
            }

            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<RollbackSystem>().Reset(pause);
        }
    }
}
