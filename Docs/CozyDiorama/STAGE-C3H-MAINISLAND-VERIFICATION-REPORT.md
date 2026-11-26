# Stage C-3H — MainIsland & Real Save v8 Verification (Unity 6)

- **Branch:** `feature/cozy-diorama-stage-c3h-mainisland-verification` (from `audit/cozy-diorama-stage-c3g-junction-growth` @ `4ca74d5`)
- **Unity:** `6000.6.5f1`, batchmode with GPU. `MainIsland.unity` opened read-only and never saved; the real save was only read, and all growth ran on in-memory `SaveClone` copies.
- **Flag:** `useContinuousPathNetworkRenderer` stays `false` by default (not changed).
- **Disk integrity:** SHA of `MainIsland.unity`, the 5 other protected scenes and the real `afk_island_save.json` is identical before and after.

## Key facts about the real project state (found, not assumed)

1. **The real save is a fresh one.** `afk_island_save.json` is Save v8, 1 chunk `(0,0)`, `settlementMask` empty: 0 clearings / paths / stones / huts. There is no mature settlement to check yet.
2. **`MainIsland.unity` has no authored settlement.** `Island` contains `PlantPlots`, `CentralMeadow`, `WaterSource` (1.80, 1.90, -1.40), but **no `Settlement/Hut_A/Hut_B/Path_Main/Path_Cross`**. `IslandPrototypeBuilder.EnsureSettlementLayout` returns early when `PlantPlots` exists, so the settlement is not rebuilt either.
3. **Bug found by the MainIsland check (fixed):** with the flag ON, the continuous renderer always injected the *production starter* paths and two starter huts. On the real MainIsland this drew a phantom starter road and doors to huts that do not exist (see `c3h_02`/`c3h_03` before the fix: a beige path blob next to the pond). Fix in `HomeSettlementVisualizer`: starter paths/huts are only injected when the authored settlement exists (`HasAuthoredStarterSettlement()`); mask huts are only deduplicated against authored starter huts in that case. With the authored settlement present (prototype scenes, C-3E/C-3F verifiers) behaviour is unchanged.

## Results

| Step | Result | Metrics |
|---|---|---|
| 1. Scene wiring, flag default | PASS | bootstrap/save/chunk/visualizers present, flag `false`, `WaterSource` present |
| 2. Real save, no growth, OFF vs ON | PASS | save SHA unchanged in OFF and ON; ON draws no phantom paths (`nodes=0, edges=0`), `pondIntrusions=0`, `legacyPaths=0`, 0 foldbacks, OFF and ON visually identical (`c3h_01`, `c3h_02`, `c3h_03`) |
| 3. Controlled growth, +48h and +21d (3 x +7d) on clones | PASS | both deterministic (`67DD500740DD90CD`, `62AF0AFD70B27666`), save unchanged by visual rebuild, `pondIntrusions=0`, `legacyPaths=0`, `downwardTris=0`, `foldbacks=0`, border delta `0.00000 m` |
| 4. OFF -> ON -> OFF | PASS | `created=4, destroyed=4, liveOwned=0`, authored/legacy paths restored, save unchanged |
| 5. Performance | PASS (see below) | exact BFS vs Tarjan equivalence, 0 mismatches on 3 saves |
| 6. Disk integrity | PASS | 6 scenes + real save file unchanged |

### Growth on the real save (what it actually shows)

After +21d: 9 chunks, 18 clearings, 15 paths, **0 huts** (the 3-week trajectory never reached the Path->Hut stage; hutsAdded was 0 in each session). So on this save the bridge guard was **not exercised through hut growth** — it was only exercised by the synthetic C-3G tests and the stress saves (`hutsAdded` 1 and 6).

Connectivity after +21d: `connectedComponents = 2` (no huts). These are two independently grown road fragments (6 cells near the home island and 2 cells in a far chunk), not a cut caused by a hut. The C-3G invariant "1 -> 1" applies to a network that was already connected; a fresh save that grows from several clearings is legitimately multi-component. This is a *finding about expectations*, not a regression: the guard cannot join separate roads.

### Performance of the guard (real finding + fix)

Per-candidate `WouldDisconnectRoadNetwork` re-decodes every chunk mask and runs a BFS per Path cell, so cost grows as cells x chunks:

| Save | Path cells | per-cell BFS scan | single-pass Tarjan |
|---|---|---|---|
| Real +21d (9 chunks) | 15 | 0.55 ms | 0.062 ms |
| Stress 3x3 | 315 | 49 ms | 0.28 ms |
| Stress 5x5 | 875 | 329 ms | 0.74 ms |

`SettlementSimulation.FindRoadArticulationPoints` (iterative Tarjan, O(V+E)) now provides the candidate-collection check once per pass; per-cell BFS stays only for apply-time revalidation. Equivalence with the BFS was checked on every Path cell of the 3 saves (0 mismatches).

Remaining cost: full `Apply(+7d)` on the stress saves is still 0.49 s (3x3) and 1.7 s (5x5) in the Editor. This is not dominated by the articulation search any more (that part is < 1 ms); it was not profiled further here. It matters only for very large late-game saves and should be profiled before Android.

## Not run

- Interactive Play Mode on `MainIsland.unity` and the real runtime flow (Continue/Start Fresh, `WorldGrowthService` commit path). `WorldGrowthService` was not found in the scene when opened in edit mode (`growthService=False`); it is added by `GameBootstrap.Awake` at runtime.
- Growth of huts on a real, matured save (none exists yet).
- Android build / device.
- The runtime `LateUpdate` signature path of `HomeSettlementVisualizer` with the new starter condition (only edit-mode `RebuildFromSave` was exercised).

## Recommendation

Do not enable the flag globally yet. Before that: (a) a Play Mode pass on MainIsland with a save that has grown huts, (b) decide whether a MainIsland without an authored settlement is intended (the starter-road fix assumes yes), (c) profile `Apply` on large saves.
