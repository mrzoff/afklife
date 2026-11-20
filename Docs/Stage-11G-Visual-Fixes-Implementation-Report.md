# Stage 11G — visual placement fixes

**Repository:** `mrzoff/afklife`  
**Base branch:** `feature/stage-11g-b-organic-trees`  
**Fix branch:** `fix/stage-11g-visual-overlap-and-start-area`  
**Scope:** bird visuals, hut/tree footprint separation, organic starter island silhouette, starter planting markers  
**Status:** IMPLEMENTED IN BRANCH — UNITY COMPILE / EDITOR SMOKE / PLAY MODE / ANDROID DEVICE PASS PENDING

## 1. Bird visuals

- Added `AnimalData.modelForwardYawOffset` so a species can correct its authored model forward axis without changing the world movement vector.
- Bird catalog uses a 180° correction to compensate for the 180° local yaw in `Animal_Bird.prefab`.
- Reduced bird visual scale from 1.0 to 0.25 after checking the source mesh dimensions; with the prefab's 0.34 child scale this brings the bird to roughly half a metre in length.
- Reduced move speed from 0.85 to 0.42 m/s and wander radius from 3.2 to 1.7 m.
- Increased idle interval from 1.5–4 s to 3–6.5 s to make birds less restless.
- Both AnimalData asset copies were updated (Resources and ScriptableObjects).

The save data and fauna population simulation are unchanged. The actual model facing and perceived size still require visual confirmation in Play Mode / Android.

## 2. Hut footprints and vegetation

- Added read-only world-space hut footprint queries to `SettlementCellQuery`.
- New hut candidates are rejected when their centers are closer than 3.2 m to another generated hut or an authored starter hut. The same rule is rechecked immediately before the settlement mask is written.
- Existing overlapping hut entries in old saves are not rewritten. `HomeSettlementVisualizer` deterministically suppresses overlapping visuals, keeping the lowest world-cell Z/X key in a conflict.
- Suppressed hut visuals are excluded from the authored-home fade count.
- `IslandMeshVisualizer` builds a per-rebuild cache of hut centers. Decorative bushes/rocks and tall trees are excluded from the hut footprint without repeatedly decoding every settlement mask during scatter.

This is a visual/placement correction only. Save v8, existing settlement masks and existing plant coordinates are unchanged.

## 3. Organic starter island

- Replaced the rectangular cube meshes for `BaseRock`, `MidPlateau`, `SettlementYard`, `ShoreSand_N` and `ShoreSand_S` with generated 64-segment irregular elliptical platform meshes.
- Platform tops remain flat and retain their original transform positions/scales/heights. Existing settlement, pond and plot coordinates are not moved.
- Sloped side bands replace the vertical square box edges.
- BaseRock, MidPlateau and shore sand use matching MeshColliders; SettlementYard remains without a collider, as before.
- Existing authored scenes are upgraded at runtime as well as newly built islands.
- The authored-home plant footprint query now uses an ellipse rather than a rectangle, keeping new wild planting inside the sculpted silhouette.

The organic geometry is generated at runtime; inspect the silhouette, collision and pond interactions in Unity before acceptance.

## 4. Planting markers

- Green PlotMarker renderers are hidden by default.
- The PlantPlot BoxCollider remains enabled, so the existing tap-to-plant interaction remains available.
- A serialized `showAvailableMarker` option can re-enable markers later when a deliberate tutorial/selection affordance is designed.

## 5. Regression smoke

Added menu:

`AFK Island/Smoke Stage 11G Visual Fixes`

Checks:
- hut footprint exclusion across a chunk boundary;
- deterministic visible winner for overlapping saved hut visuals;
- authored starter hut exclusion;
- bird scale, motion tuning and model-forward correction;
- planting marker hidden while plot collider remains active;
- organic platform mesh vertex/index counts and irregular perimeter.

## 6. Explicitly unchanged

- Save schema/version remains v8.
- No save migration or SaveService changes.
- Existing saved plant coordinates are not modified.
- Fauna population counts and offline fauna simulation are unchanged.
- AFK growth pacing and settlement budgets are unchanged.
- `main` is not modified; this branch is based on the open Stage 11G-B feature branch.

## 7. Verification status

| Check | Status |
|---|---|
| Source review | DONE — code changes committed to fix branch |
| Unity C# compilation | NOT RUN |
| Stage 11G visual fix Editor Smoke | ADDED — NOT EXECUTED |
| Existing Stage 11G-B scatter smoke | NOT EXECUTED |
| Existing Stage 12 settlement smoke | NOT EXECUTED |
| Play Mode visual inspection | NOT RUN |
| Android APK / BlueStacks pass | NOT RUN |
| Save v8 reference comparison | NOT RUN |

Do not merge this branch until Unity compile, all three Editor Smoke menus, Play Mode and Android visual checks pass.
