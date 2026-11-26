# Stage C-3G — Unity 6 Verification Report (Junction + Bridge / Cut-Vertex Guard)

- **Branch:** `audit/cozy-diorama-stage-c3g-junction-growth`
- **Unity version:** `6000.6.5f1` (URP, batchmode with GPU rendering, isolated worktree `AFKIsland-c3e`)
- **Feature flag default:** `HomeSettlementVisualizer.useContinuousPathNetworkRenderer = false` (verified unchanged)
- **Unchanged systems:** Save v8 format, `WorldGrowthConfig`, growth pace, settlement caps, RNG, `ResidentPathGraph`, `Stone -> Hut` rule, existing saved huts
- **Protected scenes:** all 6 (`MainIsland.unity`, `CozyDioramaStageB2`, `C2`, `C3C`, `C3D`, `C3E`) verified unchanged by SHA-256

---

## Executive Summary

| # | Check | Initial C-3G (`7cf60e7`) | Final C-3G + Bridge Guard | Key Metrics / Evidence |
|---|---|---|---|---|
| 1 | Compile & regression tests (`CozyDioramaStageC3GJunctionTests`) | `CS0103 FloorDiv` → fixed → `8/8 PASS` | **PASS (`15/15`)** | 8 original junction tests + 6 bridge/cut-vertex/loop/ordinary-cell tests + 1 end-to-end `SettlementSimulation.Apply` test (`hutsAdded=3, protectedIntact=True, roadComponents=1`). |
| 2 | C-3F Save v8 immutability & seed-invalidation suite (`CozyDioramaStageC3FSaveAndSeedVerifier`) | **PASS (`7/7`)** | **PASS (`7/7`, exit 0)** | Save v8 SHA-256 `BABD519D13AE4041` unchanged across all rebuilds/toggles; seed-only invalidation round-trip OK; OFF rollback `created=84 == destroyed=84, liveOwned=0`. |
| 3 | Deterministic `+48h` offline growth on 2 identical Save v8 copies | **PASS** | **PASS** | `appliedSeconds=172800` on both copies; byte-for-byte identical JSON SHA-256: `4E2C829D91C8090F == 4E2C829D91C8090F`. |
| 4 | Junction & bridge protection | **PASS** (`4/4` junctions) | **PASS** (`4/4` junctions + all bridges) | `(1,0)@(5,8)=Path`, `(1,0)@(9,8)=Path`, `(0,1)@(8,5)=Path`, `(1,1)@(5,5)=Path`; `promotedToHut=0`; `0` corridor cuts. |
| 5a | Post-growth visual, geometry, immutability & hut attachment health | **PASS** | **PASS** | `connectedHuts=6/6`, `nodes=27`, `edges=26`, `tJunctions=6`, `fourWayJunctions=3`, `starterIssues=0`, `legacyPaths=0`, `pondIntrusions=0`, `downwardTris=0`, `foldbacks=0`, `maxBorderPos=0.00000m`, `maxBorderHeight=0.00000m`, `sharedMaterialsOnly=True`, `rollbackZeroLeaks=True`. |
| 5b | **Post-growth single-component road network connectivity** | **FAIL (`1 -> 3`)** | **PASS (`1 -> 1`)** | **`connectedComponents: before48h=1 -> after48h=1`**. |

---

## 1. Root Cause Found on Initial C-3G (`7cf60e7`) & Resolution

1. **Compile fix (`98f3e6c`):** On unmodified `7cf60e7`, `CozyDioramaStageC3GJunctionTests.cs(95-96)` failed with `error CS0103: FloorDiv` because `FloorDiv` lived inside nested class `MaskFixture`. Moved `FloorDiv` to the outer class (`0 errors`).
2. **Connectivity gap found on check #5 (`connectedComponents: 1 -> 3`):**
   - Protecting only `degree >= 3` saved all 4 crossroads, but `Path -> Hut` on straight bridge corridors (`chunk(1,0)@(4,8)` and `chunk(0,1)@(10,5)`, both `degree == 2`) severed the road network into 3 disconnected components.
   - Additionally, on legacy home island `(0,0)`, mask cell `(9,8)` lies inside authored starter `Hut_B`'s clearance (`0.76m < 0.85m`), so `SettlementPathTopology` excludes that edge; a mask-only BFS that walked through `(9,8)` falsely treated `(0,1)@(12,5)` as a redundant loop edge and still produced `connectedComponents = 2`.
3. **Bridge / articulation-vertex guard implemented in `SettlementSimulation.cs`:**
   - Added `WouldDisconnectRoadNetwork(save, chunkX, chunkZ, cellIndex)` (BFS over navigable cardinal `Path`/`Stone` road cells across all chunks, skipping pond cells, non-Moss cells, and legacy starter-hut-blocked cells, with `O(1)` fast path for `degree <= 1`) and `IsProtectedRoadCell = IsRoadJunctionCell || WouldDisconnectRoadNetwork`.
   - Checked both in `CollectCandidates` and dynamically at apply time in `TryApplyUpgrade` (so opening a closed loop once immediately protects the remaining cells on the opened loop).
   - Exposed `SettlementCellQuery.IsNearAuthoredStarterHut` as `public static` so the starter-hut clearance check reuses the single source of truth.
   - Result: **`connectedComponents: before48h=1 -> after48h=1`**, all 15 unit/end-to-end tests PASS (including `hutsAdded=3, protectedIntact=True, roadComponents=1`), and all 7 C-3F tests PASS.

---

## 2. All 15 Regression Tests (`CozyDioramaStageC3GJunctionTests`)

1. `straight` (`degree=2, junction=False`) — PASS
2. `90-degree bend` (`degree=2, junction=False`) — PASS
3. `T-junction` (`degree=3, junction=True`) — PASS
4. `four-way crossing` (`degree=4, junction=True`) — PASS
5. `diagonal-only adjacency` (`degree=0, junction=False`) — PASS
6. `X chunk seam T-junction` (`degree=3, junction=True`) — PASS
7. `X/Z corner seam four-way` (`degree=4, junction=True`) — PASS
8. `Stone is road; Hut is door-branch only` (`degree=2, junction=False`) — PASS
9. `straight corridor without loop is protected cut-vertex` (`disconnects=True, protected=True`) — PASS
10. `cross-chunk bridge at X seam is protected cut-vertex` (`disconnects=True, protected=True`) — PASS
11. `dead-end terminal road cell remains eligible (ordinary cell)` (`disconnects=False, protected=False`) — PASS
12. `straight cell on closed loop remains eligible (alternate route exists)` (`disconnects=False, protected=False`) — PASS
13. `second cut on opened loop becomes protected cut-vertex` (`disconnects=True, protected=True`) — PASS
14. `four-way junction inside dense 3x3 block stays protected` (`disconnects=False, protected=True`) — PASS
15. `end-to-end Apply grows hut on non-critical spur without breaking bridge or junctions` (`hutsAdded=3, protectedIntact=True, roadComponents=1`) — PASS

---

## 3. Visual Evidence (Rendered in Unity 6)

- `StageC3G-Shots/c3g_01_after_48h_offline_overview.png` — Full island overview after `+48h` offline growth: single connected road network (`connectedComponents = 1`), all 4 crossroads intact, zero broken corridors.
- `StageC3G-Shots/c3g_02_chunk10_4way_preserved.png` — Close-up of chunk `(1,0)`: 4-way crossroads `(5,8)` and the main corridor from home island are completely unbroken; new clearings expand cleanly beside the road.
- `StageC3G-Shots/c3g_03_chunk11_junction_preserved.png` — Close-up of chunk `(1,1)`: 4-way crossroads `(5,5)` preserved and seamless across chunk borders (`0.00000 m` delta).
- Raw machine-generated verification report: `StageC3G-Shots/c3g-verification-report.txt`.

---

## 4. What Was NOT Run / Scope Boundaries

- Interactive Play Mode on `MainIsland.unity` and real user `MainIsland` save testing were **not run yet** (ready as the next step now that all automated C-3G + C-3F checks pass).
- Android APK build and on-device testing were **not run**.
- Persistent user saves were **not touched**; `useContinuousPathNetworkRenderer` remains `false` by default.
