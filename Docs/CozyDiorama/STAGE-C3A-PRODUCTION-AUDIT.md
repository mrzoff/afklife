# Stage C-3A — production audit (read-only)

**Branch:** `feature/cozy-diorama-stage-c2-settlement-path-network`  
**Plan:** [STAGE-C3-PRODUCTION-PATH-NETWORK-INTEGRATION-PLAN.md](STAGE-C3-PRODUCTION-PATH-NETWORK-INTEGRATION-PLAN.md)  
**Scope:** read-only audit of production code. No production code, scenes, saves or prefabs were changed.

## 0. Baseline and decisions

- **Starter source of truth:** production layout from `IslandPrototypeBuilder.CreateStarterSettlement` (decision of 2026-11-26). The C-2B B-2 poses stay as test-only data.
  - `Hut_A` local (-1.6, 1.85, 0.9), yaw 12°
  - `Hut_B` local (1.8, 1.85, -0.2), yaw -25°
  - `Path_Main` (0, 1.88, 0.2), 1.1 × 4.2 m
  - `Path_Cross` (0.9, 1.88, -0.6), 2.4 × 0.7 m
  - `Hearth` (0.15, 1.92, -1.5)
- **Road visuals:** the uncommitted 11G working copy of `HomeSettlementVisualizer.cs` contains `PlacePathRibbon`. It is **not** committed and is **not** the HEAD version (HEAD has `PlacePath(yaw)`). It is kept locally and must not be committed as part of C-3A. A backup copy and patch are in `Builds/cozy-diorama/c3a-backup/` (git-ignored).

## 1. Source data and lifecycle

| Item | Where | Notes |
|---|---|---|
| Save version | `Save/SaveMigrationService.CurrentVersion = 8` | Must stay 8 |
| Settlement mask | `ChunkGrowthSaveData.settlementMaskBase64`, decoded by `ChunkMaskCodec` | One `SettlementStage` byte per cell (None, Clearing, Path, Stone, Hut) |
| Land mask | `ChunkGrowthSaveData.stageMaskBase64` | `CellStage` per cell; drives terrain height |
| Seed | `ChunkGrowthSaveData.growthSeed` | Used by visual jitter (`Hash01`) |
| Grid | 16 × 16 cells per chunk, 16 m chunk, 1 m cell | `GrowthWorldPlacement` |
| Only writer | `SettlementSimulation.Apply` → `TryApplyUpgrade` | Also `RebalanceExcessStones` (demotes Stone → Path) |
| Offline path | `LivingOfflineNaturePass` calls `SettlementSimulation.Apply` | Same writer |
| Visual rebuild | `HomeSettlementVisualizer.RebuildFromSave` | Called by `GameBootstrap` (load and offline paths) and by `LateUpdate` on signature change |

**Hut direction is not chosen.** `PlaceHut` uses `yaw = Hash01(seed, i, 7) * 360`. Door orientation is visual-only and is derived from the cell hash. Changing it is a visual change. Rejecting a hut because of its door is a **simulation** change: placement rules live in `SettlementSimulation.CollectCandidates` / `TryApplyUpgrade` and `SettlementCellQuery.IsHutFootprintBlocked` (spacing 3.2 m).

## 2. Terrain, water and chunk APIs

- **Surface height:** `ChunkTerrainMesher.SampleVisualHeight(worldX, worldZ, sampleWorldStage, sampleWorldBiome, chunkSize)`. It already drives tree placement in `IslandMeshVisualizer` (around line 574). It is the closest existing surface sampler for path nodes. The land mask is an input, so path heights change when land grows.
- **Water:** `WaterSourceLayout.BlocksSettlement(x, z)` (footprint radius 0.7). The pond centre comes from the scene object named `WaterSource` at runtime. The exact shape of the blocked area needs verification in C-3B.
- **Shoreline:** `GrowthWorldPlacement.IsShoreCell` (void neighbour check, radius 1). Used by the simulation.
- **Cell ↔ world:** `GrowthWorldPlacement.CellCenterWorld`, `SettlementCellQuery.TryWorldToCell`. Origin is the chunk centre convention (chunk (0,0) centred on the world origin).
- **Chunk views:** `HomeSettlementVisualizer` keys its own `SettlementChunk_x_z` roots by save chunk. It is **not** tied to loaded `IslandChunk` views, so unloading a `ChunkManager` view does not remove settlement visuals.

## 3. Current road visuals (`HomeSettlementVisualizer`)

### 3.1 HEAD (committed)
`PlacePath(parent, local, cellIndex, yaw)`: one tile per Path/Stone/Hut cell, rotated by `yaw * 0.15`. No connectivity.

### 3.2 11G working copy (uncommitted)
`PlacePathRibbon(parent, local, cellIndex, growth)`:
1. A Hut cell draws no arms of its own (early return).
2. Four neighbours are checked with `IsRoadCell` (Path, Stone or Hut, world coordinates, crosses chunk borders).
3. Count ≥ 3: draws a through street and extra arms only toward Hut neighbours.
4. Corner of a filled block (count 2, perpendicular, both neighbours with road degree ≥ 3) draws nothing.
5. Otherwise draws one arm per road neighbour.
6. Arm geometry: prefab `Path_Tile` scaled to 0.25 × 0.55 (or a cube 0.28 × 0.62), offset 0.34 m from the cell centre. Lateral jitter ±0.18 m and yaw jitter ±9° come from `Hash01(1, worldCell, …)`, so a straight run keeps one column/row value.
7. Hut cells (non-legacy) also get a cylinder yard, 0.78 m, under them.

**Result:** the road is made of rectilinear arms per cell edge. It is not a continuous polyline and has no explicit junctions. This is the visual the player sees today (once 11G is committed).

### 3.3 Extraction assessment
- **Rule part** (neighbour flags → list of arm specs) is pure once given a stage sampler. It can be lifted to a static function with no behaviour change. Verification: compare arm lists for every cell of a fixed mask before and after the move (determinism test).
- **Render part** depends on `_pathPrefab`, `_pathMat`, `Instantiate` and `StripCollider`. It stays in the visualizer.
- **Names** use `cellIndex + k·1000`, not stable IDs. Fine for visuals, not for save identity.
- **Decision for C-3:** keep the rule behaviour as the visual baseline and treat it as the renderer candidate. Build the topology (nodes, edges, junctions) separately, as in C-2B. Do not extract until the baseline comparison is done.

## 4. Gaps and risks found by reading the code

1. **Chunk-border staleness (likely bug).** `chunkSig` only includes the chunk's own masks. `PlacePathRibbon` reads neighbours across chunk borders. If a neighbouring chunk gains a Path cell, the border arms in the unchanged chunk are not rebuilt. Not reproduced yet; must be tested in C-3B/C-3C.
2. **Per-frame cost.** `LateUpdate` calls `BuildSignature(data)` every frame. It concatenates and sorts every chunk's mask string.
3. **Stage lookup cost.** `SampleSettlementStage` scans all chunks linearly per call. `IsRoadCell` runs 4× per cell plus up to 8 `RoadDegree` lookups. Cost grows with chunk count.
4. **Terrain dependency.** `SampleVisualHeight` takes the land mask. Path node heights must be resampled when a chunk's land mask changes, not only when the settlement mask changes.
5. **Pose mismatch.** C-2B used the B-2 review poses. Production poses differ (see section 0). C-2B checks do not transfer to production without a re-run.
6. **Door alignment in production (estimate from code, not from scene).** With the door on local +Z and the production poses, the door of `Hut_A` points almost sideways to `Path_Main` (cosine ≈ 0.2). `Hut_B` is closer (≈ 0.4–0.6). Needs a scene check in C-3B.
7. **Plan §3.3 conflict.** "Reject or defer hut placement" changes simulation. Only visual orientation is allowed without a separate decision.
8. **Plan §3.1 conflict.** The plan does not mention the existing mask-driven road renderer in the 11G copy.

## 5. Smallest isolated implementation boundary (proposal)

1. **Pure planner** (no `GameSaveData`, no `MonoBehaviour`): input = a stage sampler `(worldX, worldZ) → SettlementStage`, the starter layout, the hut poses and the door poses. Output = nodes, edges, junctions, and an ordered key per element. Lives in `Assets/_Project/Scripts/World/` in a new file. Not wired into anything yet.
2. **Test harness** (editor menu) for C-3B: starter-only, four-hut fixture, reversed doors, blocked joins, duplicate input, determinism.
3. **Renderer adapter** (later, C-3D/E): consumes planner output, produces meshes per chunk, and is called from `HomeSettlementVisualizer` behind a flag.

Nothing above changes save data, the simulation, Resident navigation or the tree density.

## 6. Open items for the next stage (C-3B)

- Compare 11G `PlacePathRibbon` with the C-2B topology on the same mask (visual diff in a review scene).
- Confirm the water blocked-area shape from `WaterSourceLayout`.
- Confirm the chunk-border staleness bug with a two-chunk mask.
- Measure `BuildSignature` and `SampleSettlementStage` cost on a worst-case mask.
- Decide whether the 11G copy is rebased onto HEAD or dropped (user decision).

## 7. Not done in C-3A

- No Unity Editor run, no Play Mode, no Android build.
- No production code, scene, prefab or save changes.
- The 9 "11G" working-tree files and `Builds/save-forest-fixture*` were not touched or committed.
