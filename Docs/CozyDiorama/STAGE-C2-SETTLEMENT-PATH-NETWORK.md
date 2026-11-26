# Stage C-2 — settlement path network

**Branch:** `feature/cozy-diorama-stage-c2-settlement-path-network`  
**Base:** Stage C verified snapshot `4c4b38187a1b3ce6e0f312ef743063f5a8eba787`  
**Result report:** [`STAGE-C2B-VERIFICATION-REPORT.md`](STAGE-C2B-VERIFICATION-REPORT.md)

## C-2A — anchor audit

- `Hut_A` and `Hut_B` both expose a child named `Door` facing local +Z; their door offsets differ, so the prototype reads the child transform instead of hard-coding a shared offset.
- The authored starter settlement in `IslandPrototypeBuilder` has a shared path (`Path_Main`, `Path_Cross`) and a hearth; the B-2 review scene contains two huts and an older single `Path_Ribbon`.
- Runtime hut placement applies a per-cell yaw and `HutVisualUniformScale`; the prototype resolves door positions after the complete transform.
- Resident navigation remains separate: `ResidentRoster` uses neighbouring walkable cells and `ResidentPathGraph` remains unchanged.

## C-2B — isolated prototype (decision: variant 1)

Decision: keep the B-2 layout (two huts + grove stay in place), add a hearth and a starter network at the `IslandPrototypeBuilder` coordinates, and use two extra huts as extension test points.

File: `Assets/_Project/Scripts/Editor/CozyDioramaStageC2Builder.cs`

Menu entries:
- `AFK Island → Cozy Diorama → Stage C-2 → Build Path Network Review Scene`
- `AFK Island → Cozy Diorama → Stage C-2 → Render Review Shots`

The builder:
1. Hashes the B-2 scene, opens it, and saves a separate copy as `CozyDioramaStageC2.unity`. The B-2 source is never written; the hash is re-checked after the build.
2. Removes the old `Path_Ribbon` from the copy only and adds a `Hearth` at (0.15, -1.5).
3. Adds two review-only huts (`Hut_Test_03`, `Hut_Test_04`) from the existing Hut_A / Hut_B prefabs.
4. Builds two starter polylines (Catmull-Rom): `Starter_Main` through hearth → (0, 2.3) and `Starter_Cross` through x -0.3 … 2.1.
5. For every hut (ordinal ID order) reads the real `Door` world transform, anchors at door + 0.06 m along its forward, collects up to 8 candidate join points on the existing network, tries 6 route variants (soft Bézier, hard Bézier, wraps around the hut footprint on either side, two wide wraps) and chooses the lowest violation score (hut footprints, obstacles/rocks, island outline, overlap with existing paths).
6. Writes two combined meshes (`PathNetwork_Core`, `PathNetwork_Fringe`) with up-facing normals into `Assets/_Project/Art/CozyDiorama/StageC2/`.
7. Writes a plain-text network report and eight review PNGs. Rendering is deferred by two editor ticks after reloading the saved scene, because the GPU Resident Drawer can otherwise draw stale deleted renderers.

This is a topology / visual prototype, not production integration. It uses the review scene's flat clearing: there is **no** terrain sampling, water-crossing test or chunk-boundary routing yet.

## Finding for production

A hut whose door faces away from the network (tested at (2.9, 0.2), yaw 100 / 160) has no clean route: the best candidate was a 5.26 m loop that crosses the hut footprint or leaves the island. Production placement must orient doors toward the nearest network point (or the settlement hearth). `Hut_A` already shows the cost of a poorly oriented door: its route is a 3.66 m wrap.

## Explicitly untouched

No changes to production `HomeSettlementVisualizer`, `SettlementSimulation`, `ResidentPathGraph`, `ResidentRoster`, Save v8, migrations, offline growth, `WorldGrowthConfig`, hut placement rules, resident AI, water layout, plant / animal IDs, scatter density or approved V2 tree ratios. The B-2 scene file is unchanged.
