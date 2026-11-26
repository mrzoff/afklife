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

## Regression tests added

Unity menu:

`AFK Island → Cozy Diorama → Stage C-3H → Run Road Connectivity Tests`

The in-memory Save v8 fixtures cover:

1. An isolated Clearing cannot become Path.
2. Diagonal-only adjacency does not permit Path.
3. Cardinal Path adjacency permits Path.
4. Cardinal Stone adjacency permits Path.
5. A road neighbour across a chunk seam is detected.

The tests use synthetic in-memory data and do not read or write the user's real save.

## Validation status

The source changes and regression fixtures are committed on the isolated branch. Unity compilation, the new menu tests, the 12 × 7-day stress run, and MainIsland Play Mode verification still need to be run in Unity 6000.6.5f1. Do not enable the continuous renderer globally or proceed to Android until those checks pass.

## Protected scope

- No changes to `main`.
- No Save v8 schema or migration changes.
- No changes to growth pacing or `AbsoluteMaxAdvancesPerSession`.
- No changes to ResidentPathGraph or road rendering.
- The continuous renderer default remains `false`.
- The real save and local uncommitted Play Mode probe files are not part of this branch change.
