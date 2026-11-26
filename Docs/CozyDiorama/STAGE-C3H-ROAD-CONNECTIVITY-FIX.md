# Stage C-3H — Road connectivity during settlement growth

## Problem

The 84-day stress run showed that the existing Path/Stone network remained intact, but settlement growth could promote a Clearing to Path without a cardinal connection to the existing road graph. Repeated sessions therefore created isolated one-cell Path components.

## Change

`SettlementSimulation` now requires every newly-created Path cell to touch at least one existing cardinal Path or Stone cell:

- Candidate collection rejects a Clearing → Path promotion when the four-cardinal-neighbour count is zero.
- `TryApplyUpgrade` repeats the same check immediately before writing the mask, so a stale candidate cannot create an isolated Path after earlier upgrades in the same Apply pass.
- The check uses the existing world-coordinate sampler and therefore crosses chunk boundaries.
- Diagonal neighbours do not count.
- Hut cells do not count as road edges: huts remain door destinations, while the continuous mask graph is Path/Stone.
- Existing Path → Hut junction/articulation protection is unchanged.

The rule is deliberately incremental: a new road can extend one cell at a time, but each new cell must attach to the already-connected network. It does not alter growth budgets, Save v8, the renderer, ResidentPathGraph, or the renderer feature flag.

## Stone → Hut protection (found by the corrected tests)

The corrected tests assert the final topology after `Apply`. They exposed a gap in the C-3G protection: a `Stone` cell with a `Path`/`Hut` neighbour could become a `Hut` without the junction/articulation check that already guarded `Path` cells. A `Stone` bridge between two `Path` cells was turned into a `Hut` for all 8 RNG seeds and split the road into two components.

Fix: the junction/cut-vertex protection now applies to `Stone` as well as `Path`, both in candidate collection and in the revalidation inside `TryApplyUpgrade`. A leaf `Stone` (not a junction, not a cut vertex) may still become a `Hut`, as a leaf `Path` can.

## Regression tests

Unity menu:

`AFK Island → Cozy Diorama → Stage C-3H → Run Road Connectivity Tests`

All assertions are made on the final topology after `SettlementSimulation.Apply` (the four-neighbour road graph across chunks), not on the pre-`Apply` neighbourhood. In-memory Save v8 fixtures:

1. An isolated Clearing with no roads creates no road.
2. An empty settlement creates no Path/Stone/Hut over several sessions.
3. A diagonal-only seed: the final network is one connected component, and the target Path is cardinally attached.
4. A cardinal Path seed permits growth into a single component.
5. A real cardinal `Stone` seed permits growth into a single component.
6. `Apply` grows a Path across a chunk seam from a `Stone` seed; the final network is one component.
7. A `Stone` bridge between two `Path` cells is never turned into a `Hut` (8 RNG seeds).

The tests use synthetic in-memory data and do not read or write the user's real save.

## Validation status

Unity 6000.6.5f1 batch run: compiles, 7 / 7 road connectivity tests pass, 15 / 15 C-3G junction and bridge tests pass. Test 7 failed before the `Stone → Hut` protection fix (components = 2 in all 8 seeds) and passes after it.

## Protected scope

- No changes to `main`.
- No Save v8 schema or migration changes.
- No changes to growth pacing or `AbsoluteMaxAdvancesPerSession`.
- No changes to ResidentPathGraph or road rendering.
- The continuous renderer default remains `false`.
- The real save and local uncommitted Play Mode probe files are not part of this branch change.
