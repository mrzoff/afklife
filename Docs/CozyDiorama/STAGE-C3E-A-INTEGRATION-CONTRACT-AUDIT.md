# Stage C-3E-A — production integration contract audit

**Status:** read-only audit complete; no production code, scenes, prefabs, save data or materials changed.  
**Branch:** `feature/cozy-diorama-stage-c3e-integration`  
**Baseline:** `eaba92f6786b3a633d6f598818ee9eff2137f4eb` (Stage C-3D accepted).  
**Scope:** determine the smallest safe adapter boundary for C-3E. This document does not implement or enable the feature flag.

## 1. Findings at a glance

1. `HomeSettlementVisualizer` is the correct integration owner: it already owns save-driven settlement visuals and receives `SaveService` + `ChunkManager`.
2. The standalone renderer is suitable as a child component, but it must live under a world-origin root, not under an individual `SettlementChunk_x_z` root. Its chunk slots position themselves using `GrowthWorldPlacement.ChunkRootLocalPosition`.
3. The planner input is not yet assembled anywhere in production. C-3E must add a read-only adapter from `GameSaveData.worldGrowth.chunks` into `TopologyInput`.
4. The terrain sampler currently lives inside `IslandMeshVisualizer` as private `SampleWorldStage` / `SampleWorldBiome` methods backed by private caches. `HomeSettlementVisualizer` cannot call them. This is the primary integration contract gap.
5. `WaterSourceLayout.BlocksSettlement` is a cell-centre footprint test (radius 0.7 m) and is not equivalent to a continuous road clearance test. The planner's `IsBlockedByWater` callback must use the actual water ellipse plus the configured path half-width / margin, not just `BlocksSettlement`.
6. Keep the old cell props renderer intact when the flag is false. When true, suppress only old Path tiles; keep Clearing, Stone decoration, Hut prefabs, hut suppression, authored soft-dimming and all save/simulation behaviour.
7. Terrain invalidation cannot rely on settlement-mask signatures alone. Land-mask changes, water transform changes and island expansion must advance a terrain revision and force relevant renderer chunks dirty.

## 2. Existing production lifecycle

### HomeSettlementVisualizer

At HEAD, `RebuildFromSave(GameSaveData data = null)`:
- resolves the authoritative save snapshot when no data is passed;
- clears views if `worldGrowth.chunks` or `chunkManager` is unavailable;
- uses `BuildSignature(data)` as a global early-out;
- decodes each chunk's settlement mask;
- skips chunks without settlement cells;
- keys roots by packed (chunkX, chunkZ);
- uses per-view signature `settlementMaskBase64:stageMaskBase64:isLegacyBase`;
- rebuilds that chunk's Clearing / Path / Stone / Hut props when that signature changes;
- removes views for chunks no longer represented;
- soft-dims authored starter paths/huts based on home-mask counts.

`LateUpdate` calls `BuildSignature` each frame and invokes `RebuildFromSave` only when the settlement signature changes. The global signature currently contains chunk coordinates + settlement mask only; it does not contain land masks, biome masks or water pose.

`PlaceChunkProps` iterates every cell:
- Clearing → clearing visual;
- Path → Path tile;
- Stone → Path tile + stone visual;
- Hut → Path tile + visible hut unless `SettlementCellQuery.IsHutVisualSuppressed`.

Do not change the save writer or the simulation. The new adapter must consume this snapshot read-only.

### Renderer

`SettlementPathNetworkRenderer` contract:
- `ConfigureMaterials(core, fringe)`;
- `ApplyPlan(plan, sampleWorldY, config, forceFullRebuild, terrainVersion)`;
- `TryGetChunkMeshes`, `GetStats`, `Clear`;
- owns per-chunk GameObjects and runtime meshes and releases them on rebuild, removal, Clear and OnDestroy;
- uses `sharedMaterial` only.

The renderer's chunk roots are positioned relative to its own transform. Put the renderer under a dedicated identity root at the same world origin convention as `SettlementGrown`; do not parent it to a per-chunk view.

## 3. Planner input mapping

The planner public contract is `SettlementPathTopology.TopologyInput`:
- `Config`;
- `StarterPaths`;
- `RoadCells` (world-cell coordinates, Stage 2 Path and Stage 3 Stone);
- `Huts`;
- `Obstacles`;
- `IsInsideLand(worldX, worldZ)`;
- `IsBlockedByWater(worldX, worldZ)`.

Recommended read-only mapping:

1. Add production starter geometry once using `CreateProductionStarterPaths(config)` and `CreateProductionStarterHuts()`. These are already expressed in the production world coordinate convention around chunk (0,0).
2. For every `ChunkGrowthSaveData`, decode `settlementMaskBase64`. Convert local cell coordinates to global world-cell coordinates:
   `worldCellX = chunkX * GridSize + localX`, `worldCellZ = chunkZ * GridSize + localZ`.
3. Add only Stage.Path and Stage.Stone to `RoadCells`. Stage.Hut is represented by a `HutInput`, not a road cell.
4. For each visible, non-suppressed Hut stage, compute its centre with `GrowthWorldPlacement.CellCenterWorld`, use the same deterministic visual yaw currently used by `PlaceChunkProps` (`Hash01(seed, cellIndex, 7) * 360`), and use the production prefab door/footprint contract. Do not alter the simulation yaw or hut save state.
5. Exclude the two authored starter huts from mask-derived huts only if their legacy mask entries correspond to the same authored huts. This needs an explicit deterministic deduplication rule (stable IDs and position tolerance), not an assumption based only on `isLegacyBase`.
6. Add explicit hut/rock obstacles only when the relevant source data is available and grounded. Do not invent obstacle positions from decor visuals.

The planner's cell stage contract is literal: Path = 2, Stone = 3, Hut = 4. Prefer casting the existing enum to byte rather than duplicating numeric constants in the adapter.

## 4. Terrain sampler gap and required resolution

`ChunkTerrainMesher.SampleVisualHeight` requires delegates for world land stage and biome. In `IslandMeshVisualizer`, those delegates are private instance methods backed by `_stageSamples` and `_biomeSamples`, populated from the same save snapshot. They cannot be called from `HomeSettlementVisualizer`.

Do not copy the private sampling implementation into the settlement visualizer. That would create two divergent definitions of world terrain and could make paths float or sink when biome/land logic changes.

Preferred C-3E implementation:
- extract the existing stage/biome cache population and world-cell sampling into a small shared read-only sampler owned by the world visualization layer; or
- expose a narrow public read-only sampling contract from the existing terrain/world visualizer and have the settlement adapter use that exact instance.

Before selecting either implementation, inspect `IslandMeshVisualizer` initialization, save refresh and teardown to ensure the shared sampler is built from the same authoritative snapshot and updated before path meshes are rebuilt. Avoid a second independent cache if a shared cache can be introduced without changing the terrain visualizer's behaviour.

Legacy base handling is special: `IslandMeshVisualizer` treats the legacy home chunk as land in its stage cache. Preserve that behaviour. Missing chunk samples must remain `CellStage.Void` / `BiomeType.None`, not be silently treated as land.

## 5. Water and land blockers

`WaterSourceLayout`:
- authored local centre: (1.8, 1.90, -1.4);
- blue water half extents: X = 1.1 m, Z = 0.8 m;
- `BlocksSettlement` uses a larger 0.7 m trunk/settlement reserve around the cached pond centre;
- `GetWorldCenter()` resolves the scene `WaterSource` transform when available and caches it.

For road geometry, the planner tests points and segments with road half-width and `WaterClearanceMarginMeters`. The callback should therefore test the water ellipse expanded by the current path half-width + configured margin. A simple point-in-ellipse callback without width expansion is insufficient. The current `BlocksSettlementCell` may still be used to keep existing simulation/settlement cell exclusion semantics, but do not change it.

The planner's `IsInsideLand` must query the same world land field as `ChunkTerrainMesher.SampleVisualHeight`; it must not use `IsInsideAuthoredHomeFootprint` as a substitute for the full grown-island shape.

## 6. Feature flag and visual ownership proposal

Add a serialized opt-in flag on `HomeSettlementVisualizer`, default `false`, e.g. `useContinuousPathNetworkRenderer`.

When false:
- execute the current code path exactly as before;
- do not create the new renderer root/component;
- preserve existing Path tiles, Stone/Hut/Clearing props and soft-dim behaviour.

When true:
- create/configure one renderer under an identity world-origin root;
- build a `TopologyInput` from the current save snapshot;
- apply the topology plan and terrain sampler;
- skip only `PlacePath` for Path/Stone/Hut cells to prevent duplicate road visuals;
- continue to render Clearing, Stone decoration, Hut prefabs and suppression exactly as before;
- keep `CountHomeStages` and `SoftDimAuthored` behaviour unchanged;
- call renderer `Clear` and destroy its root on disable/teardown or when the save becomes unavailable.

Do not use a scene-wide `FindObjectOfType` on every rebuild. Cache explicit references and clean them up deterministically. Do not create runtime Materials in the adapter; assign dedicated shared material assets.

The old visual path must remain available behind the false flag until C-3E and C-3F acceptance.

## 7. Invalidation and update order

Current `HomeSettlementVisualizer.BuildSignature` does not detect land/biome/water changes. C-3E must add a separate visual input revision, not change save data or the global simulation signature.

Minimum invalidation inputs:
- settlement mask changes (topology changes);
- land stage mask changes (terrain height / land boundary changes);
- biome mask changes only if they alter `SampleVisualHeight` output or the land predicate;
- water centre/pose/shape changes;
- island expansion or chunk availability changes that alter the terrain sampler domain;
- renderer config/material changes that alter mesh output.

Recommended update order:
1. receive authoritative `GameSaveData`;
2. refresh/rebuild the shared world terrain sampler for that snapshot;
3. detect settlement/terrain/water revision changes;
4. build topology only when settlement topology inputs change;
5. call `ApplyPlan` when topology, terrain revision or render config changes;
6. pass a monotonically increasing terrainVersion when terrain inputs change, forcing the renderer to rebuild affected chunks;
7. remove renderer chunk slots no longer represented by the plan.

Important: `terrainVersion` is currently a single integer applied to all renderer slots. Incrementing it invalidates all currently active chunks, not just a selected subset. This is safe but may be more expensive than necessary. C-3E should initially favour correctness (global revision) over premature selective terrain invalidation; C-3G can refine per-chunk revisions.

Avoid calling `ApplyPlan` every frame. The existing `LateUpdate` may continue to monitor the save signature for compatibility, but the renderer should only be updated when a stable input revision changes.

## 8. Integration risks and decisions required before code

| Risk | Required treatment |
|---|---|
| Private terrain stage/biome samplers | Extract or expose one shared read-only sampler; do not duplicate |
| Legacy home chunk special case | Preserve legacy land semantics |
| Duplicate authored + mask starter paths/huts | Define stable de-duplication for starter objects |
| Old Path tiles overlap new continuous road | Under flag true, suppress only old Path tiles |
| Hut visuals / stone props accidentally disappear | Keep current hut/stone/clearing placement path |
| Water clearance | Use expanded ellipse for road width + margin |
| Terrain invalidation | Separate terrain revision; first implementation may globally invalidate |
| Renderer root coordinate mismatch | Identity root at world origin, not chunk view |
| Runtime mesh/material leaks | Renderer owns meshes; adapter uses shared material assets |
| Save missing / component disabled | Clear renderer slots/root safely; legacy branch remains intact |

## 9. C-3E-A exit checklist

- [x] Exact production visualizer lifecycle reviewed.
- [x] Planner public input/output contract reviewed.
- [x] Renderer lifecycle and transform contract reviewed.
- [x] Terrain sampler access gap identified.
- [x] Water blocker contract reviewed.
- [x] Feature-flag ownership boundary defined.
- [ ] Shared terrain sampler design validated against full `IslandMeshVisualizer` lifecycle.
- [ ] Stable starter hut de-duplication rule verified against real save masks.
- [ ] C-3E-B implementation scope approved.

## 10. Explicit non-goals

No edits to `SettlementSimulation`, save writers/migrations, Save v8, `ResidentPathGraph`, `ResidentRoster`, hut placement rules, growth speed, `WorldGrowthConfig`, plant/fauna/weather/fishing/collection systems, production starter layout, or the 9 local 11G files.

No feature flag has been added and no production renderer has been wired. No Unity compile, Play Mode or Android build was run during this read-only audit.
