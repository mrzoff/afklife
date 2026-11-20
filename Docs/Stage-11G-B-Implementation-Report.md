# Stage 11G-B — implementation report

**Repository:** `mrzoff/afklife`  
**Branch:** `feature/stage-11g-b-organic-trees`  
**Base:** `main`  
**Scope:** Organic placement of tall decorative trees only  
**Status:** IMPLEMENTED IN BRANCH — EDITOR SMOKE / UNITY BUILD / DEVICE PASS PENDING

## Changes

### Added: `Assets/_Project/Scripts/World/WorldScatterField.cs`

- Deterministic world-space candidate sites based on integer lattice coordinates and a stable hash.
- Candidate jitter is derived only from world seed and candidate coordinates.
- Smooth continuous value-noise helper for large-scale density modulation.
- Local-minimum blue-noise filtering with a configurable minimum spacing.
- Halo sampling around requested bounds, so a candidate near a chunk edge evaluates the same neighboring candidates regardless of which chunk requests it.
- Optional blocked world points for existing saved plants and authored trunks.
- No mutable per-chunk random state and no runtime Update loop.

Initial tree parameters:
- minimum trunk spacing: 3.5 m;
- candidate spacing: 2.2 m;
- candidate jitter: 0.44 of candidate spacing.

### Updated: `IslandMeshVisualizer.cs`

- Tall tree prefabs are removed from the old cell-by-cell decor placement pass.
- Tall trees are placed in a separate world-space scatter pass.
- Candidate eligibility uses the final world-space point and checks:
  - land stage is Vegetation;
  - biome is Forest;
  - shore exclusion radius;
  - authored WaterSource trunk exclusion;
  - hut-cell exclusion;
  - minimum distance from existing saved plant positions and the authored yard cedar.
- Tree scale, rotation and prefab choice are deterministic from world seed and candidate coordinates.
- Removed `maxDecorPerChunk` from the cell-loop condition; the loop no longer terminates early and therefore cannot create the former first-cells-only striping defect.
- Stage and biome masks are decoded once into per-rebuild caches instead of being decoded repeatedly for each world-space sample.
- Rebuild signature includes stage/biome/settlement masks, per-chunk seed, stable scatter seed and saved plant blocker positions.

The scatter seed uses the home logical chunk's stable `randomSeed`; old saves with a zero value use a fixed deterministic fallback. It deliberately does not use `worldGrowth.growthSeed`, because that seed advances during offline simulation and would otherwise move decorative trees after AFK sessions.

### Updated: `ChunkTerrainMesher.cs`

- Added read-only `SampleVisualHeight`, which evaluates the same SDF/domain-warp/height field used by the terrain mesh.
- Tree bases use this sampled height rather than the old per-cell constant stage height.

### Added: `Assets/_Project/Scripts/Editor/WorldScatterFieldSmoke.cs`

Menu: `AFK Island/Smoke Organic Tree Placement (11G-B)`

Checks:
- deterministic repeated generation;
- request-order independence for neighboring chunk bounds;
- partitioned chunk requests equal one whole-world request;
- minimum spacing;
- all points remain inside requested bounds;
- saved/authored blocker exclusion;
- zero-density field produces no points.

## Explicitly unchanged

- Save schema/version remains v8.
- No save fields or migrations added.
- Existing saved plant positions are not changed.
- Plant colonization, offline simulation, water layout, authored start platform, settlement simulation, residents and wind shader are not modified.
- Bushes and rocks remain on the existing placement path; they are Stage 11G-C.

## Verification status

| Check | Status |
|---|---|
| Source diff reviewed against `main` | DONE |
| New scatter Editor Smoke authored | DONE — NOT EXECUTED |
| Unity C# compilation | NOT RUN |
| Unity Editor Smoke | NOT RUN |
| `Builds/save056.json` before/after comparison | NOT RUN |
| 20-chunk RebuildDecor timing | NOT RUN |
| BlueStacks / device visual pass | NOT RUN |

## Acceptance still required

Do not mark 11G-B accepted until the Editor Smoke is executed in Unity and the following are verified on the reference save:
- 120 existing plant coordinates remain byte-for-byte/value-for-value unchanged;
- no decorative trees on shore, water, WaterSource or hut footprints;
- tree roots align with terrain visually (target error <= 2 cm);
- no visible chunk seams or density striping;
- stable placement after restart and after offline growth;
- measured rebuild time and mobile performance are acceptable.

A Unity Editor/device run is required before merging this branch into `main`.
