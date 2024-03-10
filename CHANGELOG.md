# Changelog
## [0.6.0] 2024-03-11
- Updated to support entities 1.2.0-pre.12
- AddComponent support
- SaveablePrefab no longer an ISharedComponentData
- SavableScene is now a tag to save significant chunk space
- Added support for ignoring fields without attributes
- Fixed a variety of bug

## [0.5.2] - 2022-02-22
- Updated to support pre.44
- Fixed InvalidOperationException on BufferLookup<BovineLabs.Saving.SavableLinks> when loading

## [0.5.1] - 2022-01-22
- Fixed hash map hash map is full exception when serializing child entities

## [0.5.0] - 2022-11-28
- Initial release