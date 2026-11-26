# Stage C-2 — settlement path network

**Branch:** `feature/cozy-diorama-stage-c2-settlement-path-network`  
**Base:** Stage C verified snapshot `4c4b38187a1b3ce6e0f312ef743063f5a8eba787`

## C-2A — anchor audit

- `Hut_A` and `Hut_B` both expose a child named `Door` facing local +Z; their door offsets differ, so the prototype reads the child transform instead of hard-coding a shared offset.
- The authored starter settlement in `IslandPrototypeBuilder` has a shared path and hearth; the B-2 review scene contains a starter path ribbon and two huts.
- Runtime hut placement applies a per-cell yaw and `HutVisualUniformScale`; the prototype resolves door positions after the complete transform.
- Resident navigation remains separate: `ResidentRoster` uses neighbouring walkable cells and `ResidentPathGraph` remains unchanged.

## C-2B — isolated prototype builder added

File: `Assets/_Project/Scripts/Editor/CozyDioramaStageC2Builder.cs`

Unity menu entries:
- `AFK Island → Cozy Diorama → Stage C-2 → Build Path Network Review Scene`
- `AFK Island → Cozy Diorama → Stage C-2 → Render Review Shots`

The builder:
1. Opens the existing B-2 review scene and saves a separate copy as `CozyDioramaStageC2.unity`; the B-2 source scene is not modified.
2. Removes only the copied scene's old single ribbon.
3. Adds two review-only huts using the existing Hut_A / Hut_B prefabs.
4. Reads each hut's actual child Door world position after rotation and scale.
5. Seeds a network from the existing B-2 path centreline.
6. Sorts hut IDs ordinally, attaches each door to its nearest point on the current network, and adds a deterministic gently curved branch.
7. Saves separate path mesh assets under `Assets/_Project/Art/CozyDiorama/StageC2/`.
8. Can render close / medium / overview shots to `Builds/cozy-diorama/stage-c2-shots/`.

This is a topology / visual prototype, not production integration. The route builder currently uses the review scene's known flat clearing and does **not** yet perform terrain sampling, obstacle avoidance, water-crossing tests, or chunk-boundary routing. The two added huts are test fixtures, not save data. These limitations are deliberate and must be resolved before production use.

## C-2C — review gates

- [ ] Unity compile succeeds with no new errors.
- [ ] Build menu creates the C-2 scene while B-2 remains unchanged.
- [ ] Both starter hut doors visibly meet the path network.
- [ ] Both test huts join the nearest existing network point.
- [ ] No visible gaps or z-fighting at junctions.
- [ ] Close / medium / overview shots are readable and the path does not dominate the island.
- [ ] Rebuilding twice produces the same topology and appearance.
- [ ] Confirm that test huts and mesh assets exist only in the review scene / StageC2 folder.

## Explicitly untouched

No changes to production `HomeSettlementVisualizer`, `SettlementSimulation`, `ResidentPathGraph`, `ResidentRoster`, Save v8, migrations, offline growth, `WorldGrowthConfig`, hut placement rules, resident AI, water layout, plant / animal IDs, scatter density or approved V2 tree ratios.

## Verification status

The prototype source was added by static repository editing. Unity Editor, compilation, scene generation, rendering, Play Mode and Android build have **not** been run here. The next required step is to run the two menu items in Unity and commit the generated C-2 scene, mesh assets and screenshots only after inspecting the result.
