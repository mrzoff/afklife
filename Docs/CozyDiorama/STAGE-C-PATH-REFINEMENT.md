# Stage C — path composition refinement (prototype gate)

**Status:** implementation prepared; Unity Editor verification required before production integration.  
**Branch:** `feature/cozy-diorama-stage-c-path-refinement`  
**Base:** Stage B-2 verified snapshot `a63bde1025188c839733db5ea6a6bb44ffb164f4`  
**Scope:** isolated `CozyDioramaStageB2` review scene only.

## Why this is the first Stage C task

The Stage B-2 report identifies the path as the main composition defect: the existing `Path_Tile` prefab reads as separate dark raised pads rather than one continuous trail. The prefab itself contains a square pad (scale 1.12 × 0.08 × 1.12) and a rim (1.22 × 0.03 × 1.22). The review scene placed four copies along a broken polyline, which reinforces the disconnected stepping-stone appearance.

## Change prepared

- Replaced the four isolated prototype tile instances with one continuous narrow ribbon following the same four control points.
- Ribbon width is 0.68 m; it is a flat mesh placed just above the review ground.
- The ribbon reuses the existing `Path_Tile` pad material when available; a warm earth fallback is used only if that material cannot be loaded.
- Mesh is persisted as `Assets/_Project/Art/CozyDiorama/CozyDioramaStageC_PathRibbon.asset` when the review scene is rebuilt.
- The original `Path_Tile.prefab` is not modified.

## Explicitly untouched

- Production `HomeSettlementVisualizer` and all settlement-mask placement.
- `Path_Tile.prefab` and its materials.
- `MainIsland`, Save v8, migrations, world simulation, offline growth, `WorldGrowthConfig`, scatter density, plant/animal IDs, Resident AI, water, fishing and collection.
- The approved V2 tree-to-hut ratios and all tree prefab scales.

## Unity verification required

1. Switch to `feature/cozy-diorama-stage-c-path-refinement` and wait for compilation.
2. Run **AFK Island → Cozy Diorama → Stage B-2 → Build Composition Review Scene**.
3. Confirm the scene rebuild completes and the path mesh asset is created.
4. Inspect close (7.5), medium (12.8) and overview (17) camera sizes.
5. Confirm the path reads as one continuous earthen trail, does not float above the island, does not cut through hut footprints, and remains legible at medium distance.
6. Confirm both huts remain readable and the V2 grove is unchanged.
7. Run **Render Review Shots** and commit the resulting three PNGs under `Docs/CozyDiorama/StageB2-Shots/` if the visual result is acceptable.

Do not run Play Mode or Android build for this static composition check. Report any compile errors or visual defects before proceeding.

## Gate to the next task

Only after the path is visually approved should the next Stage C task begin: a separate production-integration design for applying the approved V2 per-species scale calibration to runtime tree scatter. That work must preserve current density, candidate spacing, LOD policy, save compatibility and world-growth timing, and must be reviewed before production code changes.
