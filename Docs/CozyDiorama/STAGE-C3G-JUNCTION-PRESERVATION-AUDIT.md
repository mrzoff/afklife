# Stage C-3G — Settlement junction preservation audit

**Base:** `948cd4448c8cae105599178514ff739b41a8b796` (C-3F Unity 6 verification)
**Status:** source audit and proposed acceptance contract only. No simulation, save schema, growth pacing, or production visual code changed.
**Branch:** `audit/cozy-diorama-stage-c3g-junction-growth`

## 1. Observed behavior

The C-3F +48h fixture reported new huts at cells which had previously been part of a path / crossing. The renderer remained valid (no foldbacks, pond intrusions, downward triangles, border cracks, or mesh leaks), but the settlement mask can lose a path cell when its stage advances from `Path` to `Hut`.

This is a simulation/topology policy question, not a mesh-renderer failure.

## 2. Source-level cause

In `SettlementSimulation.CollectCandidates`, a `Path` cell can become a `Hut` when:

1. `alongRoad` is true (at least one 8-neighbour is Path or Hut); and
2. the cell is on grown inland land (`CellStage.Vegetation` or higher), or it has a neighbouring Hut; and
3. the candidate passes hut footprint spacing.

The `grownInland` condition does not distinguish an ordinary road segment from a junction/branch node. Therefore a path cell at a crossing can be selected as a hut candidate if it is mature inland land and has a road neighbour. The existing 3.2m hut-centre spacing check prevents nearby hut overlap, but it does not protect a road graph articulation/junction cell.

The same candidate rule is evaluated using an 8-neighbourhood in `HasNeighborAtLeast`, whereas the continuous path planner reasons about its own explicit topology. This difference needs to be handled deliberately rather than assuming every adjacent mask cell is a graph edge.

## 3. Proposed C-3G policy (not implemented)

Preserve road connectivity when upgrading a Path cell to a Hut:

- Derive the cell's local road degree from the same world-aware neighbourhood semantics used by the settlement mask, including chunk boundaries.
- Do not promote a Path cell to Hut when it is a junction/branch node (initial proposed threshold: degree >= 3).
- Allow ordinary non-junction road segments to remain eligible only under the existing maturity / hut-spacing rules.
- Do not alter existing saved Hut cells; this is a forward-growth candidate filter only.
- Keep hut spacing, per-session caps, random seed use, water/shore checks, and all growth timing unchanged.
- Do not alter `ResidentPathGraph`; it remains the resident navigation graph, separate from visual road topology.

Before implementing, validate whether junction degree should be measured with 4-cardinal neighbours or 8-neighbours. A diagonal adjacency must not accidentally classify a gentle bend as a junction, and chunk-boundary adjacency must behave identically to same-chunk adjacency.

## 4. Required regression fixtures

| Fixture | Expected result |
|---|---|
| Straight path, mature inland | Existing hut eligibility remains |
| 90-degree bend | Not treated as a branch solely because of diagonal proximity |
| T-junction | Junction cell remains Path |
| Four-way crossing | Crossing remains Path |
| Branch at chunk X boundary | Same result as equivalent interior junction |
| Branch at chunk Z boundary | Same result as equivalent interior junction |
| Path next to an existing Hut | Existing intended progression retained unless it is a protected junction |
| Stone adjacent to road | Existing Stone→Hut rule retained; spacing still enforced |
| Existing Save v8 with Hut on former junction | Load/render unchanged; no migration or automatic demotion |
| Repeated +48h simulation on clone | Deterministic mask/hash for same seed and input |
| World growth caps | Same advances/huts/stones limits and watermarks |

## 5. Acceptance checks

1. Run the current C-3F suite unchanged as a baseline.
2. Add deterministic unit/editor fixtures for degree classification and chunk seams.
3. Add a SaveClone-only before/after test demonstrating that only eligible candidate selection changes.
4. Compare `cellsAdvanced`, `hutsAdded`, and `settlementMaskBase64` for baseline and candidate policy across fixed seeds; explain expected differences.
5. Verify Save v8 load/migration and old saves remain unchanged until a normal future growth tick.
6. Re-run C-3F visual checks: connected huts, zero pond intrusions, zero foldbacks/downward triangles, exact chunk borders, no Mesh leaks.
7. Keep this change behind an explicit review; do not merge to `main` or enable Continuous globally.

## 6. Explicit non-goals

- No change to `WorldGrowthConfig`, expansion speed, offline duration handling, or AFK reward pacing.
- No Save v8 schema or migration changes.
- No changes to `ResidentPathGraph`, `ResidentRoster`, hut door yaw, or hut placement orientation.
- No visual-only workaround that hides a hut while leaving a broken logical road cell.
- No blanket prohibition on huts adjacent to roads; settlement must continue to grow.

## 7. Validation status

- Source inspection: completed against C-3F `SettlementSimulation.cs`, `SettlementCellQuery.cs`, and the C-3F verification report.
- Unity compile / Play Mode: not run (no production code changed).
- Proposed degree rule: requires confirmation against the path planner's cardinal/diagonal edge model before implementation.
