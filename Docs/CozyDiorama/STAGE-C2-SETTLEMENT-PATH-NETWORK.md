# Stage C-2 — settlement path network (design and prototype gate)

**Status:** anchor audit complete; isolated visual prototype is the next implementation step.  
**Branch:** `feature/cozy-diorama-stage-c2-settlement-path-network`  
**Base:** Stage C verified snapshot `4c4b38187a1b3ce6e0f312ef743063f5a8eba787`  
**Scope:** path-network presentation and connection rules. No production integration in this stage.

## Goal

Replace the impression of one decorative strip with a settlement that grows a readable, connected footpath network as new huts appear. The authored starter settlement already has two huts and a shared hearth/path area; new huts should extend that network by joining its nearest suitable point. Roads should not be generated as unrelated hut-to-hut chords.

## Repository facts checked

- `SettlementSimulation` advances settlement cells through `None → Clearing → Path → Hut`, with Stone as an alternate stage. It scores candidates using neighboring settlement stages and checks hut spacing and blocked cells.
- `HomeSettlementVisualizer` is currently a read-only renderer of the saved settlement mask. A Path cell is rendered as an instance of `Path_Tile`; a Hut cell renders a hut and also calls `PlacePath`. It does not currently build a continuous road mesh from the mask.
- `ResidentPathGraph` already performs a bounded BFS over the settlement mask. It treats Path and Stone as walkable and huts as destination-only. It is resident navigation, not a road mesh generator; it must not be repurposed as a visual path renderer without a separate design.
- The authored starter settlement is created by `IslandPrototypeBuilder.CreateStarterSettlement`: `Path_Main`, `Path_Cross`, `Hut_A`, `Hut_B`, and `Hearth` are already present. The hearth is at local `(0.15, 1.92, -1.5)`; the two path pieces are centred at `(0, 1.88, 0.2)` and `(0.9, 1.88, -0.6)`. These are authored-scene objects, not settlement-mask cells.
- The same builder places starter huts at `Hut_A=(-1.6, 1.85, 0.9)`, yaw `12°`; and `Hut_B=(1.8, 1.85, -0.2)`, yaw `-25°`. The authored visual roots are unit scale.
- Both hut prefabs have a child named `Door`, facing local +Z. Hut_A's door anchor is `(0, 0.42, 0.598)`; Hut_B's is `(0, 0.42, 0.65)`. Their door offsets are not identical, so use each prefab's actual child transform rather than one shared hard-coded offset.
- Runtime huts placed by `HomeSettlementVisualizer` use per-cell yaw and `SettlementCellQuery.HutVisualUniformScale`. The door anchor must therefore be transformed through the exact runtime hut transform. The authored starter huts and mask-generated huts are separate placement paths and must both be accounted for in any future production design.
- `ResidentRoster` derives resident stand points from walkable Path/Stone neighbour cells; it does not currently use the prefab Door transform. `ResidentPathGraph` is keyed by hut cells and uses those stand points. Do not silently change this navigation contract as part of visual road work.
- `HomeSettlementVisualizer` is intentionally read-only. Settlement progression and mask commits belong to `SettlementSimulation` / the world-growth commit path, not the visualizer.

## Proposed network rules

1. **Starter network:** treat the existing authored `Path_Main` + `Path_Cross` as the initial shared network, with the hearth as its landmark / hub. Do not replace or move these authored objects during the prototype.
2. **Starter hut approaches:** connect both authored hut door anchors into the existing path network. Do not assume the current authored path already meets either door.
3. **New huts:** find the closest point on the already-built network that can be reached over valid land, then add one branch from that point to the new hut's transformed door anchor. Prefer a nearby branch junction over a direct line to another hut.
4. **One connected graph:** each accepted hut connection joins the same network. Avoid duplicate/overlapping segments and unnecessary loops. A loop is allowed only when a later explicit design rule calls for a secondary route.
5. **Stable ordering:** derive connections deterministically from stable hut cell identity, not scene instance order or random frame timing. Rebuilding from the same authored scene + save must produce the same graph.
6. **Door-aware endpoint:** use the actual child Door transform after the hut's position, yaw and visual scale have been applied. The route must visibly meet the threshold, not stop at the hut centre.
7. **Terrain-aware route:** stay on valid land, avoid the water-source exclusion and hut footprints, and respect chunk boundaries. Do not route through a hut or across water.
8. **Visual style:** continuous low-profile ribbon; warmer/lighter earth tone than the current dark tile material; gently irregular edges and subtle edge blending into grass. Width variation should be restrained. Small edge stones/tufts are optional secondary dressing, not a substitute for continuity.
9. **Growth presentation:** a newly completed hut may receive its branch in the same visual rebuild after the save mask changes. The route must not alter the simulation's growth rate or hut spawn schedule.

## Architecture boundary

Keep three concerns separate:

- **Settlement progression:** owns which cells become Clearing, Path, Stone or Hut. No changes to Save v8, migration, offline growth, `WorldGrowthConfig`, candidate scoring, or hut caps.
- **Network topology:** a deterministic, derived representation of authored starter network / hub / hut door anchors / branch connections. Initially this is a prototype-only data structure, not persisted save data.
- **Road rendering:** converts the derived network to continuous ribbon meshes, with chunk-safe world coordinates and stable material settings.

Do not write network topology into Save v8 in this stage. New branches can be reconstructed from the existing settlement mask and stable hut identities; authored starter anchors are read from the existing scene hierarchy. Do not change `ResidentPathGraph` or `ResidentRoster`; they remain the resident walkability/pathfinding service.

## Prototype sequence

### C-2A — anchor and topology review

- [x] Inspect Hut_A and Hut_B door child transforms: both face local +Z, but their Z offsets differ (`0.598` vs `0.65`).
- [x] Identify the authored starter hub/path anchors from `IslandPrototypeBuilder.CreateStarterSettlement`.
- [x] Confirm authored starter hut positions/yaws and runtime visual scale behavior.
- [x] Confirm resident navigation uses neighbour-cell stand points rather than the prefab door.
- [ ] Define a deterministic network input record: authored starter path geometry; hut identity, world transform and door anchor; plus new hut cells and their transforms.
- [ ] Add test cases for the authored two-hut starter, one additional hut, multiple branches, rotated huts, huts on chunk boundaries, and deterministic rebuild.

### C-2B — isolated ribbon prototype

- Build the route graph only in the Cozy Diorama review scene / prototype tooling.
- Preserve the authored starter path as the initial network spine; add door approaches and deterministic branches.
- Generate continuous mesh branches that share junction positions so branches meet without gaps.
- Use smooth, gently curved centre lines while keeping the route clear of hut footprints.
- Render at close / medium / overview camera scales.
- Compare against Stage C shots; preserve the V2 grove, hut positions, island silhouette and water.

### C-2C — visual approval gate

Pass criteria:
- Both starter huts have an obvious approach from the existing path network to their doors.
- The hearth remains visually connected to the starter network.
- Additional huts join the nearest existing network branch, not arbitrary hut-to-hut chords.
- No floating sections, water crossings, hut intersections, disconnected endpoints or visible mesh gaps at junctions.
- The path reads at medium and overview scale without dominating the miniature island.
- Rebuilding twice from the same input produces identical topology and geometry.
- No production gameplay/save files are modified.

Only after these criteria are approved should a separate production-integration proposal be written for `HomeSettlementVisualizer`.

## Explicitly untouched

- `MainIsland`, `HomeSettlementVisualizer` production behavior, `SettlementSimulation`, `ResidentPathGraph`, `ResidentRoster` and Resident AI.
- Save v8, migrations, offline growth, world simulation and `WorldGrowthConfig`.
- Hut placement, hut caps, settlement candidate scoring, water layout and chunk growth timing.
- Plant / animal IDs, scatter density, approved V2 tree ratios, fishing and collection.
- The existing Stage C review scene and Stage B-2 / Stage C screenshots.

## Verification status

This document is based on static source inspection of the Stage C branch. No Unity Editor, Play Mode, render, or Android build was run as part of this design step. The next step requires a Unity-side isolated prototype and visual review before any production code changes.
