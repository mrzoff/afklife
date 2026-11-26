# Stage C-3E-B — opt-in production integration prototype

**Status:** code integration committed to the isolated C-3E branch; Unity validation is still required.  
**Branch:** `feature/cozy-diorama-stage-c3e-integration`  
**Feature flag:** `HomeSettlementVisualizer.useContinuousPathNetworkRenderer`, default **false**.  
**Save compatibility:** no save schema, writer, migration, simulation or resident-navigation changes.

## Implemented

### Shared terrain sampling implementation
- Added `WorldTerrainSampleCache` with the same world-cell mapping, legacy-home-as-Moss rule, missing-cell-as-Void rule, biome lookup and `ChunkTerrainMesher.SampleVisualHeight` entry point.
- `IslandMeshVisualizer` now delegates its existing stage/biome sampling methods to this class.
- `HomeSettlementVisualizer` builds the sampler from the same authoritative `GameSaveData` snapshot before generating the path plan.
- This shares one sampling implementation, but the two visualizers currently own separate cache instances. A future optimization could inject one shared instance; this is not required for correctness of the current prototype.

### Feature-flagged renderer
- Added a serialized opt-in flag to `HomeSettlementVisualizer`; it defaults to false.
- With the flag off, the legacy code path continues to draw Path tiles as before.
- With the flag on, the continuous renderer is created under an identity root at the World origin. The old Path tile is suppressed for Path, Stone and Hut cells only; Clearing, Stone props, Hut prefabs, suppression rules and authored soft-dimming remain in the existing visualizer.
- The renderer receives the existing runtime path material through `ConfigureMaterials`; it does not create per-renderer material instances.
- Renderer slots are cleared on flag-off, missing save data and visualizer destruction.

### Topology adapter
- Uses production starter path/hut definitions from `SettlementPathTopology`.
- Converts saved Path/Stone cells into world-cell road inputs, including across chunk boundaries.
- Converts non-suppressed saved Hut cells into planner hut inputs.
- Excludes saved hut cells within 0.75 m of either authored starter hut centre to prevent duplicate starter branches. This is a visual-only de-duplication rule; it does not alter the save.
- Uses the existing deterministic visual yaw hash and matches Hut_A / Hut_B door offsets and footprint dimensions.
- Uses the terrain sampler for land tests and height sampling.
- Water blocking uses the authored pond ellipse; planner-owned half-width + clearance offsets are applied by the planner, avoiding double expansion.

### Invalidation
- When the feature flag is enabled, the settlement visual signature also includes land and biome masks.
- WaterSource position, rotation and lossy scale are included in the signature; its cached centre is cleared before a rebuild.
- The sampler revision is passed as renderer `terrainVersion`, so terrain-input changes invalidate existing renderer chunks. This intentionally uses a global revision for correctness.

## Static checks performed

- Reviewed the edited source after each update.
- Checked balanced braces in `HomeSettlementVisualizer`, `IslandMeshVisualizer` and `WorldTerrainSampleCache`.
- Confirmed all three old `PlacePath` call sites are guarded by the opt-in flag.
- Confirmed the feature flag default is false.
- Added the Unity `.meta` file for `WorldTerrainSampleCache.cs`.

These are source-level checks only. No Unity editor, C# compiler, Play Mode, Android build or device test was run.

## Required Unity validation before enabling

1. Open the branch in Unity 6 and confirm compilation with no CS errors.
2. Confirm the feature flag is false by default and the original C-2/C-3D visuals remain unchanged.
3. Enable the flag on a copy of the review scene first; compare old and new road visuals.
4. Verify the authored starter paths/huts are not duplicated and that both Hut_A/Hut_B doors connect from the correct side.
5. Verify 4-way, T and Y junctions, chunk seams, pond clearance, land edge/height conformity and hut clearances.
6. Mutate settlement and terrain masks independently; confirm the renderer rebuilds when required and reuses unchanged chunks otherwise.
7. Move/rotate/scale WaterSource and confirm the plan refreshes without stale pond blocking.
8. Toggle the flag off/on and remove settlement chunks; confirm no duplicate Path tiles, stale chunk roots or owned Mesh leaks.
9. Run Play Mode in `MainIsland.unity`, then Android build/device checks before considering production enablement.

## Known prototype limitations

- The water blocker is an axis-aligned ellipse based on the authored cylinder extents. If the production pond is rotated or non-uniformly scaled beyond the authored convention, its blocker needs to transform into pond-local XZ before final acceptance.
- The adapter currently has no extra decor-rock obstacle list because the production visualizer does not expose a stable world-space obstacle contract. Hut footprints and water are handled; rock clearance must be checked in the review scene.
- Starter-hut de-duplication uses a 0.75 m centre tolerance. Validate against representative real Save v8 masks; do not broaden it without evidence.
- The renderer currently uses the same existing path material for core and fringe. Dedicated C-3D material assets can be assigned in a later scene/prefab authoring pass.
- Terrain cache instances are separate but share the same implementation and snapshot contract.
- Water transform changes are included in the signature while the feature flag is on. Other scene changes that alter land geometry without changing save masks (e.g. manually moving terrain objects) still require an explicit revision source.

## Non-goals preserved

No changes to Save v8, `SettlementSimulation`, `WorldGrowthConfig`, growth speed, hut placement/yaw generation, `ResidentPathGraph`, `ResidentRoster`, plant/fauna/weather/fishing/collection systems, or the nine local 11G files. No merge to `main`; the feature remains opt-in and disabled by default.
