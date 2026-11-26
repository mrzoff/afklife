# Stage C-3 — production path network integration plan

**Status:** design proposal only; no production code changed  
**Branch:** `feature/cozy-diorama-stage-c2-settlement-path-network`  
**Design input:** Stage C-2A anchor audit and Stage C-2B isolated verification  
**C-2B report:** [STAGE-C2B-VERIFICATION-REPORT.md](STAGE-C2B-VERIFICATION-REPORT.md)

## 1. Goal and non-goals

Turn the C-2B visual path prototype into a production-ready settlement path system: a readable, continuous network that connects the authored starter paths, hearth and hut doors, and grows by branches as new huts appear.

The network is a **visual/topology layer**. It must remain separate from resident navigation and must not redefine which settlement cells are walkable.

This stage is a plan, not an implementation. It does not authorize changes to production scripts, prefabs, scenes, save data or simulation.

### Explicitly out of scope

- Changes to `ResidentPathGraph`, `ResidentRoster` or resident movement rules.
- Changes to `SettlementSimulation` placement/stage progression.
- Changes to Save v8, migrations, offline growth, persistent IDs or world-growth timing.
- Changes to `WorldGrowthConfig`, plant/animal logic, fishing or collection.
- Tree scale/density calibration and narrow conifer work.
- Merging this feature branch into `main`.

## 2. What C-2B establishes — and what it does not

C-2B demonstrates on a flat review clearing that:
- Four huts can be connected from their actual child `Door` transforms.
- New branches can join either an existing hut branch or the starter cross path.
- The route selection is deterministic for the tested layout.
- The tested routes had no reported hut-footprint, rock or island-outline violations.
- The ribbon winding was corrected so mesh normals face upward.

The report also identifies an important placement constraint: a hut whose door faces away from the network can have no clean route. The tested best candidate was a 5.26 m loop; Hut_A required a 3.66 m wrap.

C-2B does **not** validate terrain following, water crossings, chunk boundaries, runtime rebuilds, Play Mode, Android performance or device rendering. Its numerical and visual results must not be treated as production guarantees.

## 3. Proposed architecture

Keep three responsibilities distinct:

1. **Settlement source data** — existing authored starter paths and the current settlement state (Path/Stone/Hut cells, hut transforms and door transforms).
2. **Visual path topology** — a derived graph describing the centreline network and its junctions.
3. **Path rendering** — meshes/materials generated from that graph and sampled island surface.

No new persistent navigation graph is proposed. The visual topology must not become a second authority for resident reachability.

### 3.1 Source of truth

- Existing authored starter path anchors remain the source for the initial network, using the established `IslandPrototypeBuilder` layout.
- Runtime settlement cells remain the source for newly developed Path/Stone segments and Hut locations.
- Each hut connection uses the actual transformed `Door` child, not a shared hard-coded door offset.
- The derived visual graph is rebuildable output. Do not serialize generated mesh data or add a new save version for this feature.

If the existing settlement data cannot uniquely describe a required visual connection, document that gap before implementation; do not silently infer or mutate simulation state.

### 3.2 Topology model

Represent the visual network with stable, deterministic identifiers:
- **Node:** starter anchor, path-cell centre, branch junction or hut-door connection.
- **Edge:** ordered connection between two nodes, with a stable source key and semantic type (starter, settlement path, hut branch).
- **Junction:** a node shared by three or more edges; geometry is generated once for the junction rather than letting independent ribbons overlap arbitrarily.

Build topology in a deterministic order (stable cell/node key, then stable hut ID). Avoid duplicate edges, zero-length segments, accidental loops and duplicate junctions. A branch should join the nearest *valid* existing network location, not simply the nearest point in Euclidean distance.

The visual graph is not a replacement for `ResidentPathGraph`: no resident path queries should be routed through it.

### 3.3 Hut placement and door orientation

Treat door orientation as a placement constraint, not something to repair with increasingly long visual detours.

For a candidate hut:
1. Resolve the actual door position and forward direction after final placement rotation and scale.
2. Find nearby candidate network attachment points.
3. Reject candidates that require crossing the hut footprint, water, island boundary or protected obstacles.
4. Score remaining candidates for route length, turn severity, door approach alignment and junction quality.
5. If no candidate passes hard constraints, reject/defer that hut placement or choose another valid hut orientation/placement before committing the settlement change.

Do not force a path through a hut, across water, outside the island or through a narrow obstacle gap just to connect every door. Establish maximum route length, minimum clearance and minimum door-alignment thresholds during implementation tests; C-2B values are observations, not approved universal thresholds.

The placement system must retain its existing simulation contract. Any proposed orientation constraint must be reviewed against current placement rules before implementation.

## 4. Terrain, water and chunk handling

This is the largest gap between the review prototype and production.

### 4.1 Surface sampling

Generate centreline points in island/world space, then sample the authoritative terrain surface for each point. The path must:
- Follow the visible ground without floating or z-fighting.
- Avoid abrupt vertical changes and excessive slopes.
- Use consistent vertical offset and surface normals.
- Handle transitions between terrain chunks without visible height or tangent discontinuities.

Do not assume a flat Y value or use the review-scene plane as a production fallback.

### 4.2 Water and shore

Water is a hard routing obstacle unless a deliberate bridge/causeway feature is designed and approved separately. The first production version should route around water.

Use the same authoritative water/shore data as the world, rather than a visual-only guess. Test both the visible shoreline and the underlying terrain/water boundary so the ribbon fringe does not bleed into the pond.

### 4.3 Chunk boundaries

A route may cross chunk boundaries. Its topology must not depend on which chunk happened to rebuild first.

Recommended approach:
- Resolve path topology from stable world-space settlement data.
- Clip/render edge geometry per affected chunk, while preserving shared boundary samples.
- Use deterministic sample spacing and shared boundary positions/tangents.
- Rebuild only chunks affected by a topology change and their immediate mesh-neighbour dependencies.
- Ensure unloading/reloading a chunk recreates the same geometry without duplicate seams.

Do not introduce a global expensive rebuild every frame or every growth tick.

## 5. Geometry and visual direction

Preserve the approved cozy low-poly / miniature-diorama direction and the C-2B winding correction.

Target appearance:
- Continuous, gently curved path ribbons.
- Slight, controlled width variation; no sudden pinching at junctions.
- Soft irregular grass transition at the edges.
- Lighter, less contrast-heavy ground material than the Stage C single ribbon.
- Small edge details only where they reinforce the route; avoid clutter and avoid placing decoration inside the walkable visual strip.
- Junctions with a deliberate shared patch/triangulation so no gaps, z-fighting or obvious overlapping caps appear.

Keep the path legible at overview, medium and close camera distances. Material/mesh generation should reuse assets or runtime resources predictably; repeated rebuilds must not leak or proliferate materials.

## 6. Update lifecycle and performance

Rebuild only when relevant source data changes:
- A Path/Stone cell is added, removed or changes state.
- A hut is placed/removed or its final transform changes.
- Starter layout is initialized or migrated into the current scene representation.
- A terrain chunk needed by an affected route becomes available or changes.

The rebuild should be deterministic and idempotent. Re-running it with unchanged inputs must produce the same topology and equivalent geometry, with no duplicate objects, materials or meshes.

Avoid coupling path rebuilds to per-frame resident movement, weather animation, water waves or unrelated decoration scatter.

Before implementation, profile the expected worst case: largest supported settlement, maximum route count, chunk reload and a batch of several simultaneous growth events. Set explicit CPU, memory and draw-call budgets after measuring on target hardware; do not invent budgets from editor-only results.

## 7. Compatibility and safety constraints

The implementation must preserve:
- Save v8 and existing saves without migration.
- Existing settlement cell meanings and progression.
- Offline growth results and timing.
- Resident AI, `ResidentPathGraph` and `ResidentRoster`.
- Stable plant/animal IDs and all unrelated collection/fishing systems.
- Existing starter pond, water source, terrain colliders and authored settlement anchors.
- Approved B-2 tree scale ratios and current scatter density.

Generated visual path topology should be derived from existing state and safely rebuildable after load. If a requirement cannot be met without changing saved data or simulation semantics, stop and present that change as a separate decision.

## 8. Implementation sequence (proposed gates)

### C-3A — source and contract audit
- Re-read current production settlement placement, mask/query APIs, terrain sampling, chunk ownership and water representation.
- Identify exact stable identifiers and lifecycle events available to a derived visual system.
- Document any missing source data and proposed minimal interface.
- No production edits.

**Exit:** a concrete data contract and list of production files/interfaces that would need to change.

### C-3B — offline topology tests
- Extract or implement a pure topology planner in an isolated test harness.
- Cover starter-only layout, current four-hut fixture, branch-to-branch joins, multiple simultaneous huts, reversed door orientation, blocked joins, duplicate input, and deterministic rebuild.
- Assert hard constraints independently of route scoring.

**Exit:** deterministic topology tests pass; impossible placements are rejected rather than connected by invalid detours.

### C-3C — terrain-aware prototype
- Add terrain sampling, slope checks, water/shore rejection and chunk-boundary continuity to a separate review scene.
- Test routes near the pond, rocks, hut footprints, shore edges and at least one chunk boundary.
- Render close/medium/overview shots and compare with C-2B.

**Exit:** no floating, terrain cuts, water crossings, chunk seams, junction gaps or hut/obstacle intersections in the test matrix.

### C-3D — production integration proposal review
- Present the exact file-level change list and risks.
- Confirm no save migration and no Resident navigation changes.
- Only after explicit approval, implement behind a controlled visual feature boundary.

**Exit:** user approval of the implementation scope.

### C-3E — production verification
- Compile in Unity.
- Rebuild the review scene and verify deterministic output.
- Run Play Mode through starter setup, several settlement growth events, save/load and offline catch-up.
- Verify chunk unload/reload and route updates.
- Run Android build and device/performance checks before calling the feature production-ready.

## 9. Acceptance checklist

- [ ] Starter path and hearth connect to the initial settlement network.
- [ ] Every accepted hut has a clean route to a valid network node and a readable door approach.
- [ ] Invalid door orientation/placement is rejected or corrected before placement; no forced loops through huts or shoreline.
- [ ] No route crosses water or leaves the island.
- [ ] Terrain following is stable with no floating, clipping or abrupt slope changes.
- [ ] Chunk boundaries have matching positions/tangents and no visible seams.
- [ ] Junctions are watertight visually: no gaps, z-fighting or overlapping ribbon caps.
- [ ] Rebuild is deterministic, idempotent and incremental.
- [ ] No material/mesh proliferation after repeated rebuilds.
- [ ] Save v8 and old saves load unchanged; no migration.
- [ ] Resident navigation and simulation behavior are unchanged.
- [ ] Play Mode, save/load, offline growth, chunk reload and Android/device checks pass.

## 10. Current decision and next action

**Current decision:** proceed with C-3A source/contract audit only. Keep all production files untouched until the audit identifies exact interfaces and the user approves the implementation scope.

After C-3A, report:
1. exact source files and methods involved;
2. which existing data can drive topology without schema changes;
3. how terrain, water and chunk APIs can be sampled safely;
4. the smallest isolated implementation boundary;
5. any unresolved design choice that genuinely requires user input.

No production integration, save changes, navigation changes, tree calibration or conifer work is authorized by this document.
