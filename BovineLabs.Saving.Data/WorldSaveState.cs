// <copyright file="WorldSaveState.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Data
{
    using Unity.Collections;
    using Unity.Entities;

    public struct WorldSaveState : IComponentData
    {
        public NativeHashMap<SectionIdentifier, NativeList<byte>> SubSceneSavedData;

        // The current set of opening and loading subScenes
        public NativeHashSet<Entity> LoadSet;
    }
}