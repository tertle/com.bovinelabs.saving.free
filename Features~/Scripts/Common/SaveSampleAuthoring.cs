namespace BovineLabs.Saving.Samples.Common
{
    using Unity.Entities;
    using UnityEngine;

    public struct SaveSample : IComponentData
    {
    }

    public class SaveSampleAuthoring : MonoBehaviour
    {
    }

    public class SaveSampleBaker : Baker<SaveSampleAuthoring>
    {
        public override void Bake(SaveSampleAuthoring authoring)
        {
            this.AddComponent<SaveSample>();
        }
    }
}
