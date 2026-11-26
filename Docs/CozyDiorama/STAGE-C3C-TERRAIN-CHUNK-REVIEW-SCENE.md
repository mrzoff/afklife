# Stage C-3C — isolated terrain and chunk-boundary review scene

**Status:** review-scene specification and implementation gate; no production code or scene is modified by this document.  
**Branch:** `feature/cozy-diorama-stage-c3c-terrain-review`  
**Baseline:** C-3B report at `cd8eb81ac54ee098de494684446d4d2585ea3f90`.  
**Source:** [C-3B topology and comparison report](STAGE-C3B-TOPOLOGY-AND-COMPARISON-REPORT.md).

## 1. Decision carried forward from C-3B

Use `SettlementPathTopology` as the only topology planner candidate. Do not port 11G `PlacePathRibbon` rules. C-3B established 10/10 planner tests and reproduced three 11G defects: missing four-way junction, incomplete T-junction, and stale neighbouring-chunk cache.

C-3C is a visual/geometry review gate. It does not authorize production integration.

## 2. Scene isolation

Create a new editor-only review scene:

- Source composition: duplicate `Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity`.
- Destination: `Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3C.unity`.
- Never overwrite B-2, C-2, any production scene, prefab or material.
- Keep the review scene and all generated meshes/materials in C-3C-specific folders.
- Use a deterministic fixture containing two adjacent 16 m chunks, with the boundary at world X = 8 m, matching the C-3B world-grid convention.
- Include the production starter path layout, production Hut_A/Hut_B poses, representative grown paths/huts, a pond/water source, and the actual terrain surface. The review fixture may add clearly named test-only objects; it must not write to `GameSaveData` or invoke `SettlementSimulation`.

The review scene is an editor artifact only. It is not added to build scenes and is not referenced by bootstrap/runtime code.

## 3. Terrain fixture

The scene must expose visibly varied terrain beneath and around the roads, not a flat plane disguised with texture.

Required review areas:
1. A gently sloped section where the path changes height.
2. A convex/concave transition where surface normals change.
3. A road crossing the chunk boundary at X = 8 m.
4. A junction close enough to the boundary to reveal a seam or tangent mismatch.
5. The pond shoreline, including the C-3B finding that the authored Starter_Cross intersects the clean-water ellipse.

Use the existing terrain sampling contract identified in C-3A/C-3B: `ChunkTerrainMesher.SampleVisualHeight` with the same world-stage/biome inputs as the terrain. Do not hard-code a constant Y for the generated path surface. If the review scene cannot supply the required world-stage/biome samplers without bootstrapping production state, create an explicit deterministic review-only terrain fixture and document the limitation; do not silently pass empty or fabricated production masks.

## 4. Topology fixture

The review-only topology input must contain:

- Production starter paths from `SettlementPathTopology.CreateProductionStarterPaths`.
- Production starter huts from `CreateProductionStarterHuts`.
- A multi-cell Path/Stone road that crosses the chunk boundary.
- At least one T-junction and one four-way junction.
- One branch that must route around the pond.
- One hut whose connection uses visual door correction, plus one blocked-join case.
- Stable IDs for every fixture hut and road segment.

The planner output is generated once per explicit rebuild command. It must not be rebuilt from `Update`/`LateUpdate`.

## 5. Review geometry requirements

The C-3C scene should render planner output as review geometry only. It is not yet the production renderer.

- Sample terrain height for every path sample after topology planning.
- Preserve shared world-space boundary samples on both sides of X = 8 m; no cracks, overlaps, vertical steps or tangent flips.
- Build junction patches from explicit `PathJunction` data; do not rely on overlapping independent ribbons to hide gaps.
- Keep path width consistent through turns and junctions; avoid pinching.
- Keep all generated edges outside the clean-water ellipse using path half-width and an explicit shoreline margin.
- For `Starter_Cross`, visualize the C-3B-recommended northward adjustment/clearance as a review-only variant. Preserve the unmodified authored starter line as a comparison overlay. Do not change `IslandPrototypeBuilder`.
- Show hut footprints, door anchors, corrected door-forward vectors, chunk boundary and water-clearance envelope with editor-only gizmos/overlays.

Every review object must be parented under a root named `C3C_ReviewOnly`. Generated meshes/materials must be owned and cleaned up by the builder so repeated rebuilds do not duplicate assets or leak runtime objects.

## 6. Required views and evidence

Capture at least:
1. Isometric overview of the complete two-chunk settlement and terrain.
2. Low oblique view of the road following a slope.
3. Close-up of a junction.
4. Close-up of the X = 8 m chunk boundary, with the same road visible on both sides.
5. Pond shoreline showing the authored Starter_Cross comparison and the review-only adjusted variant.
6. Top-down diagnostic view with chunk grid, topology nodes/edges, hut footprints, doors and water exclusion boundary.

Save review screenshots and a machine-readable/text report under `Docs/CozyDiorama/StageC3C-Report/`; temporary editor captures may also be written under `Builds/cozy-diorama/stage-c3c/`.

## 7. Acceptance checklist

C-3C passes only when all of the following are checked in Unity Editor:

- [ ] The new scene opens and compiles without new C# errors.
- [ ] B-2 and C-2 scene hashes remain unchanged.
- [ ] The scene contains exactly the intended two review chunks and a visible boundary at X = 8 m.
- [ ] Terrain sampling follows the visible terrain; no floating or buried path samples in the reviewed slopes.
- [ ] Path position, height and tangent are continuous across the chunk boundary.
- [ ] T- and four-way junctions have no holes, cracks, z-fighting or doubled caps.
- [ ] No generated road surface enters the clean-water ellipse or shoreline clearance margin.
- [ ] The original and adjusted Starter_Cross variants are both visible and labelled.
- [ ] Rebuilding twice produces identical topology and geometry hashes.
- [ ] Rebuilding does not duplicate generated objects, meshes or materials.
- [ ] The report distinguishes Editor verification from Play Mode, Android and device verification.

A successful C-3B unit suite is not sufficient to mark these visual checks as passed. C-3C must be inspected in Unity Editor. Play Mode/Android are not required for this isolated review gate, but must not be claimed unless run.

## 8. Explicit non-goals

Do not edit or call into:
- `HomeSettlementVisualizer` production integration paths;
- `SettlementSimulation`, `SettlementCellQuery` placement rules or save data;
- Save v8 or migrations;
- `ResidentPathGraph`, `ResidentRoster` or resident movement;
- `IslandPrototypeBuilder` production starter layout;
- `WorldGrowthConfig`, growth timing, plant/animal systems, fishing or collection.

Do not commit the local 11G working copy. Do not merge this branch into `main`.

## 9. C-3C exit gate

Only after the Unity Editor review checklist is complete should C-3D begin: a separately testable renderer adapter that consumes the planner output. Production wiring remains a later C-3E gate and requires a separate review.
