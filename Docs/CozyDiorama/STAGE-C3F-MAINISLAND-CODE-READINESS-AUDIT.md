# Stage C-3F — MainIsland / Save v8 code-readiness audit

**Status:** source audit + one deterministic invalidation fix; no MainIsland runtime or real-save test performed.
**Base:** Stage C-3E Unity 6 verification at `a602b24`.
**Branch:** `feature/cozy-diorama-stage-c3f-mainisland-readiness`
**Scope:** inspect the production integration path before authorizing a MainIsland Play Mode / Save v8 / offline-growth run.

## 1. Change made in C-3F

### Chunk seed is now part of the C-3E visual invalidation key

`HomeSettlementVisualizer` derives visual hut yaw and other per-cell variation from each `ChunkGrowthSaveData.growthSeed`:

- `PlaceChunkProps` calculates a chunk seed and uses it in `Hash01(seed, cellIndex, ...)`.
- `RebuildContinuousPathNetwork` uses the same chunk seed to derive grown-hut yaw.
- The C-3E `BuildSignature` and per-chunk `chunkSig` previously omitted `growthSeed`.

This meant that a seed-only change could leave previously generated hut orientation/variation in place even though the authoritative save had changed. The continuous-mode global signature and per-chunk signature now include `growthSeed`. Legacy-mode signature behavior is intentionally unchanged.

**Commit:** `0336dd4` — `fix(cozy-diorama): invalidate continuous settlement visuals when chunk seed changes`.

## 2. MainIsland source inspection

### Scene-level facts

- `MainIsland.unity` contains the authored `World` root with `IslandPrototypeBuilder`.
- `WaterSource` is present under the authored world hierarchy at the inspected scene transform.
- The scene YAML does **not** contain a directly serialized `HomeSettlementVisualizer` component by its script/class identifier.
- The C-3E verifier creates `HomeSettlementVisualizer` itself in its isolated scene. Therefore C-3E PASS proves the visualizer pipeline in isolation, but does not by itself prove that the production bootstrap creates/configures it in `MainIsland`.

This is the first production integration point to resolve before a MainIsland Play Mode claim can be made: locate the runtime owner/factory that creates `HomeSettlementVisualizer`, confirm it receives the scene's `SaveService` and `ChunkManager`, and confirm it calls `RebuildFromSave` after the authoritative save is committed.

## 3. Save and offline-growth contract

Source inspection confirms the relevant ordering in `WorldGrowthService.ApplyOfflineSession`:

1. Begin the save load transaction.
2. Read the save and restore logical chunks.
3. Simulate elapsed world growth against the loaded data.
4. Apply living/offline nature and settlement normalization to the working copy.
5. Write the committed save.
6. Apply logical chunk state and call `SaveService.SetAuthoritative(working)`.
7. Call `offlineGrowth.RestoreVisualsFromCommitted(working)`.
8. End the load transaction in `finally`.

This is a suitable point for a derived visual network to consume the committed snapshot. C-3F must verify that the visualizer is included in the visual restore path; it must not independently run simulation or mutate `GameSaveData`.

The Save v8 schema already contains the required source data: per-chunk stage, biome and settlement masks, per-chunk `growthSeed`, legacy-base flag, and world-growth watermarks. No new persisted topology or save migration is justified by the current implementation.

## 4. Risks / checks before calling C-3F complete

| Area | Source-level finding | Required runtime proof |
|---|---|---|
| Production ownership | No direct `HomeSettlementVisualizer` component found in MainIsland scene YAML | Trace runtime factory/bootstrap and prove exactly one configured instance |
| Save/load | `SaveService.AuthoritativeSnapshot` is set after commit | Load an existing Save v8 and compare a read-only snapshot/hash before and after visual rebuild |
| Offline catch-up | `WorldGrowthService` writes the simulated working save before visual restoration | Verify settlement masks and watermarks advance once; visual rebuild must not change them |
| Chunk lifecycle | Renderer is derived from the complete world-growth snapshot | Exercise chunk creation/removal/reload and confirm no stale path slots or duplicate roots |
| Terrain invalidation | Sampler revision changes on cache rebuild | Change stage/biome masks and confirm only expected path chunks rebuild; unchanged input is a no-op |
| Water | Runtime blocker uses `WaterSourceLayout` and the current pond transform signature | Verify moved/rotated pond in MainIsland against the actual pond footprint |
| Seed invalidation | Fixed in commit `0336dd4` for C-3E signatures | Change only a chunk seed in an isolated save fixture and confirm the affected chunk visual rebuilds |
| Save safety | Visualizer should remain read-only | Hash serialized save fields before/after visual-only rebuild |

## 5. Recommended C-3F execution sequence

1. Trace the production creation/configuration path for `HomeSettlementVisualizer`; do not add a second instance if one is already created dynamically.
2. Use the added read-only editor diagnostic: \`AFK Island → Cozy Diorama → Stage C-3F → Report MainIsland Runtime Wiring\`. It reports bootstrap/system counts, visualizer presence, feature flag, authoritative save version, world-growth chunk count and watermarks, topology node/edge/hut counts, connected huts, renderer chunks and owned meshes. It does not write or mutate the save.
3. Run that diagnostic in `MainIsland` with the feature flag OFF first; compare the legacy visuals and ensure the scene starts without new objects.
4. Enable the flag only for a controlled test session; capture a pre/post Save v8 hash and verify no serialized fields changed from visual rendering.
5. Test an existing Save v8 load, then a controlled offline catch-up, then chunk add/remove/reload.
6. Keep the default flag OFF until all of the above and Android/device checks pass.

## 6. Explicit non-goals retained

- No Save v8 schema/migration changes.
- No changes to `SettlementSimulation`, `WorldGrowthConfig`, offline simulation timing, `ResidentPathGraph`, `ResidentRoster`, hut placement, or world expansion speed.
- No edits to `MainIsland.unity`, B-2, C-2, C-3C or C-3D scenes.
- No change to the default feature flag.
- No merge into `main`.

## 7. Validation status

- Source inspection: completed for C-3E integration, terrain cache, renderer, topology, SaveService, WorldGrowthService, GameBootstrap, GrowthWorldPlacement and MainIsland scene YAML.\n- Added read-only runtime diagnostic: \`Assets/_Project/Scripts/Editor/CozyDioramaStageC3FMainIslandDiagnostic.cs\` (menu: \`AFK Island → Cozy Diorama → Stage C-3F → Report MainIsland Runtime Wiring\`).
- Unity compilation / Play Mode: **not run in this environment**.
- Real Save v8 load / offline catch-up: **not run**.
- Android build/device: **not run**.

C-3F is therefore **not yet runtime-accepted**. The next code task is to identify and instrument the production visualizer bootstrap path, not to enable the feature globally.
