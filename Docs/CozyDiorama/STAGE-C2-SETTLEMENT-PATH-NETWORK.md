# Stage C-2 — settlement path network (design and prototype gate)

**Status:** design prepared; isolated visual prototype is the next implementation step.  
**Branch:** `feature/cozy-diorama-stage-c2-settlement-path-network`  
**Base:** Stage C verified snapshot `4c4b38187a1b3ce6e0f312ef743063f5a8eba787`  
**Scope:** path-network presentation and connection rules. No production integration in this stage.

## Goal

Replace the impression of one decorative strip with a settlement that grows a readable, connected footpath network as new huts appear. The first hut should connect to the settlement's shared centre; every later hut should join the existing network at its nearest suitable point. Roads should not be generated as unrelated hut-to-hut chords.

## Repository facts checked

- `SettlementSimulation` advances settlement cells through `None → Clearing → Path → Hut`, with Stone as an alternate stage. It scores candidates using neighboring settlement stages and checks hut spacing and blocked cells.
- `HomeSettlementVisualizer` is currently a read-only renderer of the saved settlement mask. A Path cell is rendered as an instance of `Path_Tile`; a Hut cell renders a hut and also calls `PlacePath`. It does not currently build a continuous road mesh from the mask.
- `ResidentPathGraph` already performs a bounded BFS over the settlement mask. It treats Path and Stone as walkable and huts as destination-only. It is resident navigation, not a road mesh generator; it must not be repurposed as a visual path renderer without a separate design.
- `Hut_A.prefab` has a child named `Door` at local position approximately `(0, 0.42, 0.598)`. The visualizer applies a per-cell yaw and `HutVisualUniformScale`. Therefore the door anchor must be transformed through the exact hut transform; assuming a fixed world-facing direction would be wrong.
- `HomeSettlementVisualizer` is intentionally read-only. Settlement progression and mask commits belong to `SettlementSimulation` / the world-growth commit path, not the visualizer.

## Proposed network rules

1. **Shared origin:** define one stable settlement hub anchor (existing authored central clearing / firepit area for the starter settlement). Do not invent a new gameplay building or move the authored hub.
2. **First hut:** connect the hub to the hut's transformed door anchor.
3. **Subsequent huts:** find the closest point on the already-built network that can be reached over buildable land, then add one branch from that point to the new hut's door. Prefer a nearby branch junction over a direct line to another hut.
4. **One connected graph:** each accepted hut connection joins the same network. Avoid duplicate/overlapping segments and unnecessary loops. A loop is allowed only when a later explicit design rule calls for a secondary route.
5. **Stable ordering:** derive connections deterministically from stable hut cell identity, not scene instance order or random frame timing. Rebuilding the visualizer from the same save must produce the same graph.
6. **Door-aware endpoint:** use the actual child Door transform (or a documented anchor equivalent) after the hut's position, yaw and visual scale have been applied. The route must visibly meet the threshold, not stop at the hut centre.
7. **Terrain-aware route:** stay on valid land, avoid the water-source exclusion and hut footprints, and respect chunk boundaries. Do not route through a hut or across water.
8. **Visual style:** continuous low-profile ribbon; warmer/lighter earth tone than the current dark tile material; gently irregular edges and subtle edge blending into grass. Width variation should be restrained. Small edge stones/tufts are optional secondary dressing, not a substitute for continuity.
9. **Growth presentation:** a newly completed hut may receive its branch in the same visual rebuild after the save mask changes. The route must not alter the simulation's growth rate or hut spawn schedule.

## Architecture boundary

Keep three concerns separate:

- **Settlement progression:** owns which cells become Clearing, Path, Stone or Hut. No changes to Save v8, migration, offline growth, `WorldGrowthConfig`, candidate scoring, or hut caps.
- **Network topology:** a deterministic, derived representation of hub / door anchors / branch connections. Initially this is a prototype-only data structure, not persisted save data.
- **Road rendering:** converts the derived network to one or more continuous ribbon meshes, with chunk-safe world coordinates and stable material settings.

Do not write network topology into Save v8 in this stage. It can be reconstructed from the existing settlement mask and stable hut identities. Do not change `ResidentPathGraph`; it remains the resident walkability/pathfinding service.

## Prototype sequence

### C-2A — anchor and topology review

- Inspect both Hut_A and Hut_B door child transforms and verify whether their orientation conventions match.
- Identify the authored starter hub anchor from the actual starter scene; do not guess its position from a screenshot.
- Confirm how hut yaw and visual scale are assigned by `HomeSettlementVisualizer`.
- Define a deterministic network input record: hut cell identity, world transform, door anchor; plus the authored hub anchor.
- Add test cases for one hut, two huts, three huts, rotated huts, huts on chunk boundaries, and a save rebuild.

### C-2B — isolated ribbon prototype

- Build the route graph only in the Cozy Diorama review scene / prototype tooling.
- Generate a continuous mesh per branch, sharing junction positions so branches meet without gaps.
- Use smooth, gently curved centre lines while keeping the route clear of hut footprints.
- Render at close / medium / overview camera scales.
- Compare against Stage C shots; preserve the V2 grove, hut positions, island silhouette and water.

### C-2C — visual approval gate

Pass criteria:
- Every hut has an obvious walkable-looking approach to its door.
- The first hut connects to the shared hub.
- Additional huts join the nearest existing network branch, not arbitrary hut-to-hut chords.
- No floating sections, water crossings, hut intersections, disconnected endpoints or visible mesh gaps at junctions.
- The path reads at medium and overview scale without dominating the miniature island.
- Rebuilding twice from the same input produces identical topology and geometry.
- No production gameplay/save files are modified.

Only after these criteria are approved should a separate production-integration proposal be written for `HomeSettlementVisualizer`.

## Explicitly untouched

- `MainIsland`, `HomeSettlementVisualizer` production behavior, `SettlementSimulation`, `ResidentPathGraph` and Resident AI.
- Save v8, migrations, offline growth, world simulation and `WorldGrowthConfig`.
- Hut placement, hut caps, settlement candidate scoring, water layout and chunk growth timing.
- Plant / animal IDs, scatter density, approved V2 tree ratios, fishing and collection.
- The existing Stage C review scene and Stage B-2 / Stage C screenshots.

## Verification status

This document is based on static source inspection of the Stage C branch. No Unity Editor, Play Mode, render, or Android build was run as part of this design step. The next step requires a Unity-side isolated prototype and visual review before any production code changes.
