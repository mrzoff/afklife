# Stage C-3G — Junction preservation implementation

**Base:** C-3F verified source at `948cd4448c8cae105599178514ff739b41a8b796`.
**Branch:** `audit/cozy-diorama-stage-c3g-junction-growth`
**Status:** implementation and Editor regression fixtures committed; Unity compile/tests not yet run here.

## Change

`SettlementSimulation` now exposes two read-only helpers:

- `CountCardinalRoadNeighbors(save, chunkX, chunkZ, cellIndex)`
- `IsRoadJunctionCell(save, chunkX, chunkZ, cellIndex)`

The degree is calculated in world-cell coordinates using exactly four cardinal neighbours. `Path` and `Stone` count as road cells, matching `SettlementPathTopology.BuildMaskRoadGraph` (stages 2 and 3). `Hut` does not count as a mask-road edge because huts connect through the separate door branch. Neighbours are sampled through `SampleSettlementStage`, so the same rule applies across chunk borders.

A `Path → Hut` candidate is now rejected when its cardinal road degree is 3 or 4. The condition is checked both while collecting candidates and again immediately before applying an upgrade, so a stale candidate cannot bypass the rule after earlier upgrades in the same simulation pass.

Unchanged:
- `Stone → Hut` eligibility.
- Existing hut footprint spacing and authored starter clearance.
- Settlement caps, RNG selection, per-session advances, water/shore checks and watermarks.
- Save v8 schema/migration, `WorldGrowthConfig`, offline timing, Resident AI and visual topology.
- Existing huts in loaded saves are never demoted or migrated by this change.

## Editor regression suite

Menu:

`AFK Island → Cozy Diorama → Stage C-3G → Run Junction Preservation Tests`

Fixtures:
1. Straight road — degree 2, not a junction.
2. 90-degree bend — degree 2, not a junction.
3. T-junction — degree 3, protected.
4. Four-way crossing — degree 4, protected.
5. Diagonal-only adjacency — degree 0, not a junction.
6. T-junction crossing an X chunk seam — degree 3, protected.
7. Four-way crossing at an X/Z chunk corner — degree 4, protected.
8. Typed neighbours — Stone counts as road; Hut and Clearing do not.

The fixtures construct disposable in-memory Save v8 masks only. They do not call `SettlementSimulation.Apply`, write files, or touch a user save.

## Commits

- `61fb895` — preserve cardinal road junction cells during hut growth.
- `be73d87` — add cardinal junction and chunk seam fixtures.
- `aace17d`, `9ded2cd` — fixture coordinate corrections.
- `6c8b4bf` — add Unity meta file.

## Validation still required in Unity 6

1. Compile and run the C-3G menu suite; expected result: 8 PASS, 0 FAIL.
2. Run the existing C-3F Save v8 immutability/seed/offline suite unchanged.
3. Run deterministic +48h growth on two identical SaveClone fixtures and compare serialized save hashes.
4. Compare old/new hut counts and masks for fixed seeds; expected differences should be limited to Path cells at cardinal degree 3+ that previously became huts.
5. Verify no change to growth caps, watermark, Save v8 schema, or offline simulation timing.
6. Re-run continuous renderer checks for hut connectivity, pond clearance, foldbacks, downward triangles, chunk seams and mesh lifecycle.

No Unity runtime, Play Mode, MainIsland or Android test is claimed by this report.
