# Stage C-3G — Unity 6 Verification Report

- **Branch:** `audit/cozy-diorama-stage-c3g-junction-growth`
- **Verified baseline commit:** `7cf60e7` (`SettlementSimulation.cs` untouched during verification)
- **Unity version:** `6000.6.5f1` (URP, batchmode with GPU rendering, isolated worktree `AFKIsland-c3e`)
- **Feature flag default:** `HomeSettlementVisualizer.useContinuousPathNetworkRenderer = false` (verified unchanged)
- **Protected scenes:** all 6 (`MainIsland.unity`, `CozyDioramaStageB2`, `C2`, `C3C`, `C3D`, `C3E`) verified unchanged by SHA-256

---

## Executive Summary

| # | Check | Result | Key Metrics / Evidence |
|---|---|---|---|
| 1a | Compile baseline on unmodified `7cf60e7` | **FAIL → FIXED** | `SettlementSimulation.cs`: 0 errors. Caught `CS0103: FloorDiv` in `CozyDioramaStageC3GJunctionTests.cs(95-96)`. Fixed scope of `FloorDiv`; clean compile (`0 errors`). |
| 1b | C-3G 8 regression tests (`CozyDioramaStageC3GJunctionTests`) | **PASS** | `passed=8, failed=0` (straight road, 90° bend, T-junction, 4-way, diagonals-only, X-chunk T, X/Z-border 4-way, Stone/Hut/Clearing type rules). |
| 2 | C-3F Save v8 immutability & seed-invalidation suite (`CozyDioramaStageC3FSaveAndSeedVerifier`) | **PASS** | `7/7` tests PASS (`exit 0`). Save v8 SHA-256 `BABD519D13AE4041` unchanged across all rebuilds/toggles; seed-only invalidation round-trip OK; OFF rollback `created=84 == destroyed=84, liveOwned=0`. |
| 3 | Deterministic `+48h` offline growth on 2 identical Save v8 copies | **PASS** | `appliedSeconds=172800` on both copies; byte-for-byte identical JSON SHA-256: `3C9C81B9CD65306C == 3C9C81B9CD65306C`. |
| 4 | Junction preservation & hut growth delta | **PASS** | `4/4` pre-existing `Path` junctions preserved (`promotedToHut=0`): `(1,0)@(5,8)=Path`, `(1,0)@(9,8)=Path`, `(0,1)@(8,5)=Path`, `(1,1)@(5,5)=Path`. Mask huts `5 -> 7` (total topology huts `6 -> 8`, vs `6 -> 9` in C-3F before C-3G); `0` new huts on junctions. |
| 5a | Post-growth visual, geometry, immutability & hut attachment health | **PASS** | `connectedHuts=8/8`, `tJunctions=7`, `fourWayJunctions=2`, `starterIssues=0`, `legacyPaths=0`, `pondIntrusions=0`, `downwardTris=0`, `foldbacks=0`, `maxBorderPos=0.00000m`, `maxBorderHeight=0.00000m`, `sharedMaterialsOnly=True`, `rollbackZeroLeaks=True`. |
| 5b | **Post-growth path network single-component connectivity** | **FAIL (real finding)** | **`connectedComponents: before48h=1 -> after48h=3` (expected `1`)**. See Root Cause below. |

---

## 1. Compile Fix Caught on `7cf60e7`

Before adding any new verifier, compiling unmodified `7cf60e7` in Unity 6 produced:
- `Assets/_Project/Scripts/Editor/CozyDioramaStageC3GJunctionTests.cs(95,31): error CS0103: The name 'FloorDiv' does not exist in the current context`
- `Assets/_Project/Scripts/Editor/CozyDioramaStageC3GJunctionTests.cs(96,31): error CS0103: The name 'FloorDiv' does not exist in the current context`

`AssertDegree` on `CozyDioramaStageC3GJunctionTests` referenced `FloorDiv`, which was declared inside the nested class `MaskFixture`. Moving `FloorDiv` up to `CozyDioramaStageC3GJunctionTests` fixed compilation cleanly (`0 errors`) without changing any test assertions or `SettlementSimulation.cs`.

---

## 2. Detailed Findings on Check 5b (`connectedComponents: 1 -> 3`)

C-3G's rule (`IsRoadJunctionCell`: `CountCardinalRoadNeighbors >= 3`) worked **exactly as specified**:
- All 4 pre-existing `Path` junctions (`(1,0)@(5,8)`, `(1,0)@(9,8)`, `(0,1)@(8,5)`, `(1,1)@(5,5)`) remained `Path` after `+48h` offline growth (`promotedToHut=0`).
- Previously (in Stage C-3F), `(1,0)@(5,8)` and `(1,1)@(5,5)` turned into `Hut`s (`totalHuts: 6 -> 9`).
- With C-3G active, `SettlementSimulation` skipped those junctions and promoted two **straight corridor** cells (`preCardinalDeg == 2`, `wasJunction == False`) instead (`maskHuts: 5 -> 7`, `totalHuts: 6 -> 8`):
  1. `chunk(1,0) local(4,8)` — straight corridor cell immediately west of the preserved 4-way crossroads `(5,8)`, carrying the sole road link from home island `(0,0)` into `(1,0)`.
  2. `chunk(0,1) local(10,5)` — straight corridor cell on the `z=5` road linking chunk `(0,1)` to chunk `(1,1)`.

Because both cells are **bridge / articulation vertices** (degree 2 with no alternate cycle around them) and `SettlementPathTopology` treats `Hut` cells as buildings rather than through-roads, converting them from `Path -> Hut` split the previously single-component network (`connectedComponents = 1`) into **3 disconnected components (`connectedComponents = 3`)**:
1. Home island `(0,0)` + western stub of `(1,0)` + `(0,1)` north-west stub up to `(9,5)`
2. Eastern `(1,0)` crossroads `(5,8)` + `(1,1)` crossroads `(5,5)` + eastern half of `(0,1)` corridor `(11..15, 5)`
3. (Plus stub split around `(4,8)` / `(10,5)` verified by topology BFS: `nodes=32, edges=29, connectedComponents=3`)

Every hut still connects visually to its adjacent local road segment (`connectedHuts=8/8`), and geometry/border continuity remain spotless (`0` foldbacks, `0.00000 m` border delta), **but the road network is no longer a single connected graph after offline growth.**

---

## 3. Visual Evidence (Rendered in Unity 6)

- `StageC3G-Shots/c3g_01_after_48h_offline_overview.png` — Full island overview after `+48h` offline growth. Both 4-way crossroads in `(1,0)` and `(1,1)` are preserved, while the two new huts sit directly on the straight corridors at `(1,0)@(4,8)` and `(0,1)@(10,5)`, severing continuity.
- `StageC3G-Shots/c3g_02_chunk10_4way_preserved.png` — Close-up of chunk `(1,0)`: the 4-way crossroads `(5,8)` is cleanly preserved, while the newly grown hut at `(4,8)` sits immediately to its left, cutting the road from the home island.
- `StageC3G-Shots/c3g_03_chunk11_junction_preserved.png` — Close-up of chunk `(1,1)`: the 4-way crossroads `(5,5)` is cleanly preserved and seamless across chunk borders.
- Raw machine-generated verification report: `StageC3G-Shots/c3g-verification-report.txt`.

---

## 4. What Was NOT Run / Scope Boundaries

- `SettlementSimulation.cs` was **not modified** during this verification pass — C-3G's rule was tested strictly as authored.
- Interactive Play Mode on `MainIsland.unity` and Android APK build / on-device testing were **not run**.
- Persistent user saves were **not touched** (all offline growth tests ran on in-memory `SaveClone.DeepClone` copies).
- Feature flag `useContinuousPathNetworkRenderer` remains `false` by default.

---

## 5. Recommended Next Step Before `MainIsland` Check

Before testing C-3G on real `MainIsland` saves, decide how straight corridor cells (`degree == 2`) should behave when `Path -> Hut` would disconnect the road graph:
- **Option A (Articulation / cut-vertex guard in `SettlementSimulation`):** Protect any `Path` (and optionally `Stone` on a road) whose removal disconnects its cardinal road neighbors (`WouldDisconnectRoadNetwork`), while keeping dead-end/terminal (`degree <= 1`) or redundant loop (`degree == 2` with an alternate path) cells eligible.
- **Option B (Off-road hut placement):** Allow `Hut`s to grow on `Clearing` cells adjacent to a road rather than overwriting active corridor `Path` cells.
- **Option C (Keep C-3G strictly as junction-only protection):** Accept that straight roads can still be severed by huts, and treat single-component road connectivity as a separate later stage.
