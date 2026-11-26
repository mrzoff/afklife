# Cozy Diorama — Stage B-1 decision and Stage B-2 handoff

**Status:** Stage B-1 reviewed; V2 (middle) selected as the approved tree-scale baseline.
**Source review:** five screenshots in `Docs/CozyDiorama/StageB-Shots/`, commit `93f31c8416940eb35433e56d0e3e0de8bf9eb7ba`.
**Scope:** isolated Cozy Diorama prototype only. This decision does not authorize production-scene or runtime changes.

## 1. Stage B-1 decision

Select **V2 (middle)** as the default visual reference for the tree scale study.

Rationale from the screenshots:
- Tree silhouettes are clearly present without dominating the huts and open settlement area.
- The rear tree line reads as a grove while retaining visible gaps.
- The right-side grove gives the island a visual anchor without filling the whole clearing.
- V1 reads as a conservative/smaller alternative; V3 is better treated as an occasional upper-end specimen than as the general baseline.
- The current in-game reference has a noticeably larger scale contrast and is not the preferred new art-direction baseline.

### Approved usage

- **V2:** default target for the general tree population in the Cozy Diorama prototype.
- **V1 range:** allowed for younger/smaller trees and lower-canopy transitions.
- **V3 range:** reserved for rare tall specimens or later growth accents.
- Do not apply one global scale multiplier to every tree prefab. Preserve per-species measured bounds and scale each species against the hut-height reference used by the study.
- Do not increase tree density to compensate for scale. Density and composition are separate decisions.
- This is an art-direction decision for the isolated prototype; it is not yet a production scatter change.

## 2. Stage B-2 objective — composition under the real game camera

Build the next isolated review pass around the approved V2 baseline. The goal is to determine whether the selected tree scale remains readable in a more complete miniature-diorama composition, not merely on the scale-study discs.

### Required review scene

Use a separate prototype scene. Do not edit or replace `MainIsland.unity`, the authored legacy island, or any production scene.

Include:
- the existing Hut_A / Hut_B settlement at the current in-game hut scale;
- the V2 tree scale as the default tree baseline;
- V1 and V3 examples only as clearly labelled comparison specimens, not mixed randomly into the baseline grove;
- the existing low vegetation and rock props at their current study scales;
- a compact irregular island/ground silhouette with a readable open settlement clearing;
- the existing approved daytime lighting and isometric camera contract as the starting point.

The composition should preserve open sightlines to both huts. Tree crowns must not hide both roofs or visually close the central clearing.

### Camera checks

Capture the same scene at:
1. close view — hut, resident-scale reference and nearby trees;
2. medium view — settlement clearing and grove composition;
3. overview — complete island silhouette and separation of land, shore and water.

Use the production camera pose/orthographic settings as the starting reference; do not alter the production camera controller.

### Acceptance criteria

- V2 trees read as part of one miniature scale system with huts, props and resident reference.
- Both huts remain identifiable at medium and overview distances.
- The settlement clearing remains visually open and is not enclosed by a continuous wall of crowns.
- The grove has an irregular edge and visible internal gaps; no evenly spaced row effect.
- Tree bases sit on the ground; no visible floating or sinking.
- No obvious prefab clipping, duplicate silhouettes, or shadow mass that obscures the settlement.
- The prototype remains isolated from bootstrap, SaveService, Save v8, offline growth, WorldGrowthConfig, Resident AI and production scatter.
- The generated review scene and screenshots are reproducible from the Editor menu.

## 3. Explicitly out of scope

- No changes to production `IslandMeshVisualizer`, `WorldScatterField`, `HomeSettlementVisualizer`, `IslandPrototypeBuilder` or `ChunkTerrainMesher`.
- No changes to Save v8, save migration, simulation, offline growth, plant/animal IDs, Resident AI, fishing, weather or water systems.
- No global prefab rescaling or mass material replacement.
- No changes to world-growth speed, density, candidate spacing, LOD thresholds or sway parameters.
- No merge into `main`.

## 4. Unity verification requested from the project owner

After the isolated Stage B-2 builder is added:
- open the generated prototype scene in Unity 6;
- confirm it builds without compile errors or missing references;
- inspect the close, medium and overview camera captures;
- check tree grounding and hut visibility;
- report any visual defects with screenshots.

Unity Editor, Play Mode and Android device checks must be reported as not run until actually executed in the project environment.
