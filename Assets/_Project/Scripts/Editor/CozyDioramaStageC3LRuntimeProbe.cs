using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AfkIsland.CameraControl;
using AfkIsland.Core;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3L - MainIsland runtime validation of the continuous path network renderer (flag stays false by default;
    /// the probe only flips it at runtime on the live HomeSettlementVisualizer).
    /// Scenario is passed with "-l3Scenario real|grownreal|g101|g104|g106".
    ///   real      : copy of the real Save v8 as the player has it (offline growth runs on load), then 10 incremental
    ///               in-memory growth sessions with the renderer ON.
    ///   grownreal : the real save grown by 12 sessions (C-3J rules) before play.
    ///   gNNN      : fresh starter seed NNN grown by 12 sessions before play.
    /// Flow: A (OFF) -> B (ON, timed, close-ups of hut door / sharp junction / sharp turn / steepest slope / narrowest) ->
    ///       3x OFF/ON transitions (mesh hash determinism, leak counters) -> incremental growth -> fresh-vs-incremental hash -> OFF.
    /// The probe never edits the real save by hand: the driver script backs the file up before and restores it byte-for-byte after.
    /// Report: Docs/CozyDiorama/StageC3L-RuntimeValidation/c3l-{scenario}-report.txt
    /// </summary>
    [InitializeOnLoad]
    public static class CozyDioramaStageC3LRuntimeProbe
    {
        private const string MainIslandScenePath = "Assets/_Project/Scenes/MainIsland.unity";
        private const string OutDir = "Docs/CozyDiorama/StageC3L-RuntimeValidation";
        private const string RealBackupPath = @"D:\afk\c3i-backup\afk_island_save.REAL-BACKUP-C3I.json";
        private const string StepKey = "C3L_PROBE_STEP";
        private const long SevenDaysSeconds = 7L * 24 * 3600;

        private static readonly Stack<IEnumerator> _routines = new Stack<IEnumerator>();
        private static float _waitUntil;
        private static int _waitFrames;
        private static StringBuilder _summaryFailures = new StringBuilder();
        private static int _shotIndex;
        private static readonly List<string> _checks = new List<string>();

        static CozyDioramaStageC3LRuntimeProbe()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3L/Run MainIsland Runtime Validation (real)")]
        public static void RunMenu() => Begin();

        public static void RunBatchEntry() => Begin();

        private static string Scenario
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                for (var i = 0; i < args.Length - 1; i++) if (args[i] == "-l3Scenario") return args[i + 1];
                return "real";
            }
        }

        private static string ReportPath => Path.Combine(OutDir, "c3l-" + Scenario + "-report.txt").Replace('\\', '/');

        private static string TargetSavePath => Path.Combine(Application.persistentDataPath, "afk_island_save.json");

        private static void Begin()
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(ReportPath, "Stage C-3L MainIsland runtime validation, scenario=" + Scenario + "\n", new UTF8Encoding(false));
            EditorSceneManager.OpenScene(MainIslandScenePath, OpenSceneMode.Single);
            PlaceSave();
            SessionState.SetInt(StepKey, 1);
            EditorApplication.EnterPlaymode();
        }

        // ------------------------------------------------------------------ save placement

        private static string StageCounts(GameSaveData d)
        {
            var sb = new StringBuilder();
            foreach (var g in d.worldGrowth.chunks)
            {
                if (g == null || !ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var m, out _)) continue;
                var c = new int[8];
                foreach (var b in m) c[Mathf.Min(b, 7)]++;
                sb.Append("[").Append(g.chunkX).Append(',').Append(g.chunkZ).Append(" legacy=").Append(g.isLegacyBase).Append(" none=").Append(c[0]).Append(" other=");
                for (var i = 1; i < 8; i++) sb.Append(i).Append(':').Append(c[i]).Append(' ');
                sb.Append("] ");
            }
            return sb.ToString();
        }

        private static void PlaceSave()
        {
            var target = TargetSavePath;
            var scen = Scenario;
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);

            if (scen == "real")
            {
                File.Copy(RealBackupPath, target, true);
                Append("placed REAL save copy -> " + target);
                return;
            }

            GameSaveData grown;
            if (scen == "grownreal")
            {
                var real = JsonUtility.FromJson<GameSaveData>(File.ReadAllText(RealBackupPath));
                Append("  real settlement stages before growth: " + StageCounts(real));
                StarterSettlementSeed.SeedIfEmpty(real);
                Append("  real settlement stages after starter seed: " + StageCounts(real));
                grown = CozyDioramaStageC3IStarterSettlementVerifier.GrowSilently(real, 12);
            }
            else if (scen.Length > 1 && scen[0] == 'g' && int.TryParse(scen.Substring(1), out var seed))
            {
                grown = CozyDioramaStageC3IStarterSettlementVerifier.GrowSilently(CozyDioramaStageC3IStarterSettlementVerifier.StarterSeeded(seed), 12);
            }
            else
            {
                throw new InvalidOperationException("unknown scenario " + scen);
            }

            Append("  placed save settlement stages: " + StageCounts(grown));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            grown.lastSaveUtc = now;
            grown.worldGrowth.lastSimulationUtc = now;
            grown.worldGrowth.settlementWatermarkUtc = now;
            File.WriteAllText(target, JsonUtility.ToJson(grown), new UTF8Encoding(false));
            Append($"placed grown save ({scen}, 12 sessions) -> {target} chunks={grown.worldGrowth.chunks.Count}");
        }

        // ------------------------------------------------------------------ tick / coroutine driver

        private static void Tick()
        {
            var step = SessionState.GetInt(StepKey, 0);
            if (step == 0) return;

            try
            {
                switch (step)
                {
                    case 1:
                        if (!EditorApplication.isPlaying) return;
                        _routines.Clear();
                        _routines.Push(Run());
                        _waitUntil = 0f;
                        _waitFrames = 0;
                        SessionState.SetInt(StepKey, 2);
                        break;

                    case 2:
                        if (!EditorApplication.isPlaying) { Fail("play mode stopped early"); return; }
                        if (_waitFrames > 0) { _waitFrames--; return; }
                        if (Time.realtimeSinceStartup < _waitUntil) return;
                        if (_routines.Count == 0) return;
                        var top = _routines.Peek();
                        if (!top.MoveNext())
                        {
                            _routines.Pop();
                            if (_routines.Count == 0)
                            {
                                SessionState.SetInt(StepKey, 3);
                                EditorApplication.isPlaying = false;
                            }

                            return;
                        }

                        if (top.Current is IEnumerator nested) _routines.Push(nested);
                        else if (top.Current is float seconds) _waitUntil = Time.realtimeSinceStartup + seconds;
                        else if (top.Current is int frames) _waitFrames = frames;
                        break;

                    case 3:
                        if (EditorApplication.isPlaying || EditorApplication.isCompiling) return;
                        SessionState.SetInt(StepKey, 0);
                        Append("PROBE FINISHED");
                        EditorApplication.Exit(0);
                        break;
                }
            }
            catch (Exception ex)
            {
                Fail("exception: " + ex);
            }
        }

        private static void Fail(string reason)
        {
            Append("FAILED: " + reason);
            SessionState.SetInt(StepKey, 0);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            Append("PROBE FINISHED WITH FAILURE");
            EditorApplication.Exit(1);
        }

        // ------------------------------------------------------------------ the scenario

        private sealed class Target
        {
            public string Label;
            public Vector3 Pos;
            public string Why;
        }

        private static IEnumerator Run()
        {
            yield return 4f;
            // The editor SaveService races at play entry; place the variant again right before loading it explicitly.
            PlaceSave();
            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            if (bootstrap == null) { Fail("GameBootstrap not found"); yield break; }
            bootstrap.ContinueGame();
            Append("GameBootstrap.ContinueGame() called");
            yield return 12f;

            var vis = UnityEngine.Object.FindAnyObjectByType<HomeSettlementVisualizer>();
            var saveService = SaveService.Instance;
            var cam = Camera.main;
            if (vis == null || saveService == null || saveService.AuthoritativeSnapshot == null || cam == null)
            {
                Fail($"wiring: vis={(vis != null)} save={(saveService != null)} snapshot={(saveService?.AuthoritativeSnapshot != null)} cam={(cam != null)}");
                yield break;
            }

            var snapshot = saveService.AuthoritativeSnapshot;
            Append($"snapshot: version={snapshot.version} chunks={snapshot.worldGrowth?.chunks?.Count ?? -1} defaultFlag={vis.UseContinuousPathNetworkRenderer}");
            Check("save version is 8", snapshot.version == 8);
            Check("flag is OFF by default in MainIsland", !vis.UseContinuousPathNetworkRenderer);

            var camInfo = new StringBuilder();
            camInfo.Append($"camera: ortho={cam.orthographic} size={cam.orthographicSize:F2} pos={cam.transform.position} rot={cam.transform.rotation.eulerAngles} components=");
            foreach (var c in cam.GetComponents<Component>()) camInfo.Append(c.GetType().Name).Append(',');
            Append(camInfo.ToString());

            HideUi();
            var controller = cam.GetComponent<IsometricCameraController>();
            var defaultPos = cam.transform.position;
            var defaultRot = cam.transform.rotation;
            var defaultSize = cam.orthographicSize;

            // ---------------- A: renderer OFF
            var shaA = Sha256(JsonUtility.ToJson(snapshot));
            var baseline = CountObjects();
            Append("== A: renderer OFF (legacy path objects) ==");
            Append($"  snapshot sha256={shaA}");
            DescribeVisualizer(vis);
            Append($"  object counts: meshes={baseline.meshes} gameObjects={baseline.gameObjects}");
            yield return Shot("A-off-player-view");

            var offTimes = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                vis.RebuildFromSave(snapshot);
                sw.Stop();
                offTimes.Add(sw.Elapsed.TotalMilliseconds);
            }

            Append("  RebuildFromSave(OFF, unchanged signature) ms: " + FormatRuns(offTimes));

            // ---------------- B: renderer ON
            Append("== B: renderer ON (first build) ==");
            if (controller != null) controller.enabled = false;
            var onTimes = new List<double>();
            var firstOn = Stopwatch.StartNew();
            vis.UseContinuousPathNetworkRenderer = true;
            vis.RebuildFromSave(snapshot);
            firstOn.Stop();
            onTimes.Add(firstOn.Elapsed.TotalMilliseconds);
            yield return 2;

            var renderer = vis.ContinuousPathRenderer;
            Check("renderer exists after ON", renderer != null);
            if (renderer == null) { Fail("renderer missing"); yield break; }

            var onInfo = DescribeOn(vis, renderer);
            var firstHash = ChunkHashes(renderer);
            Append("  chunk mesh hash: " + firstHash);
            CheckOnQuality("B", vis, renderer);
            Append("  RebuildFromSave(first ON, full) ms: " + onTimes[0].ToString("F1", CultureInfo.InvariantCulture));
            Check("snapshot unchanged by ON", Sha256(JsonUtility.ToJson(snapshot)) == shaA);

            var focus = OverviewFocus(vis);
            yield return Shot("B-on-player-view-default");
            SetCamera(cam, focus.center, 45f, 45f, focus.size, 0);
            yield return Shot("B-on-overview-iso");
            SetCamera(cam, focus.center, 90f, 0f, focus.size, 0);
            yield return Shot("B-on-overview-top");

            CheckHutDoors(vis, renderer, onInfo, "B");
            var targets = PickTargets(vis, renderer, onInfo);
            Append($"  close-up targets: {targets.Count}");
            foreach (var t in targets) Append($"    target {t.Label} at ({t.Pos.x:F2},{t.Pos.y:F2},{t.Pos.z:F2}) - {t.Why}");
            foreach (var t in targets)
            {
                SetCamera(cam, t.Pos, 45f, 45f, 2.0f, 0);
                yield return Shot("closeup-" + t.Label + "-iso");
                SetCamera(cam, t.Pos, 90f, 0f, 1.2f, 0);
                yield return Shot("closeup-" + t.Label + "-top");
            }

            // player-like view at the closest zoom the game allows (ortho 5) around the first target
            if (targets.Count > 0)
            {
                SetCamera(cam, targets[0].Pos, 45f, 45f, 5f, 0);
                yield return Shot("player-mindzoom-" + targets[0].Label);
            }

            // ---------------- OFF/ON transitions
            Append("== transitions OFF -> ON -> OFF (3 cycles) ==");
            var hashesEqual = true;
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                vis.UseContinuousPathNetworkRenderer = false;
                var swOff = Stopwatch.StartNew();
                vis.RebuildFromSave(snapshot);
                swOff.Stop();
                yield return 2;
                var afterOff = CountObjects();
                var clearedStats = vis.LastClearedRendererStats;
                Append($"  cycle {cycle} OFF: {swOff.Elapsed.TotalMilliseconds:F1} ms; renderer null={(vis.ContinuousPathRenderer == null)} lastCleared: {FormatStats(clearedStats)}; counts meshes={afterOff.meshes} (baseline {baseline.meshes}) gameObjects={afterOff.gameObjects} (baseline {baseline.gameObjects})");
                Check($"cycle {cycle}: no live owned meshes after OFF", clearedStats != null && clearedStats.LiveOwnedMeshCount == 0 && clearedStats.TotalMeshesCreated == clearedStats.TotalMeshesDestroyed);
                Check($"cycle {cycle}: Mesh object count back to the baseline after OFF", afterOff.meshes == baseline.meshes);
                Check($"cycle {cycle}: GameObject count back to the baseline after OFF", afterOff.gameObjects == baseline.gameObjects);

                vis.UseContinuousPathNetworkRenderer = true;
                var swOn = Stopwatch.StartNew();
                vis.RebuildFromSave(snapshot);
                swOn.Stop();
                onTimes.Add(swOn.Elapsed.TotalMilliseconds);
                yield return 2;
                renderer = vis.ContinuousPathRenderer;
                var h = renderer != null ? ChunkHashes(renderer) : "null";
                if (h != firstHash) hashesEqual = false;
                Append($"  cycle {cycle} ON : {swOn.Elapsed.TotalMilliseconds:F1} ms; hash equal to first build={h == firstHash}; {FormatStats(renderer?.GetStats())}");
                CheckOnQuality("cycle" + cycle, vis, renderer);
            }

            Check("mesh hash identical across every OFF->ON cycle (deterministic)", hashesEqual);
            Check("snapshot unchanged by transitions", Sha256(JsonUtility.ToJson(snapshot)) == shaA);
            Append("  RebuildFromSave(ON) ms (first + cycles; first includes JIT): " + FormatRuns(onTimes));

            // repeated apply with an unchanged signature must be a no-op
            var noopStats = renderer.GetStats();
            vis.RebuildFromSave(snapshot);
            var noopAfter = renderer.GetStats();
            Check("re-apply of the same state creates no new meshes", noopAfter.TotalMeshesCreated == noopStats.TotalMeshesCreated);

            // ---------------- incremental growth with the renderer ON
            var sessions = Scenario == "real" ? 10 : 4;
            Append($"== incremental growth: {sessions} in-memory sessions (+7d each) with the renderer ON ==");
            var applyMs = new List<double>();
            var incrementalOk = true;
            for (var s = 1; s <= sessions; s++)
            {
                var live = saveService.AuthoritativeSnapshot;
                var before = SettlementRoadCount(live);
                if (s == 1)
                {
                    live.worldGrowth.lastSimulationUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - SevenDaysSeconds * 2;
                    live.worldGrowth.settlementWatermarkUtc = 0;
                }

                var grownSave = CozyDioramaStageC3IStarterSettlementVerifier.GrowthSession(live, TimeSpan.FromSeconds(SevenDaysSeconds), out _);
                saveService.SetAuthoritative(grownSave);
                var sw = Stopwatch.StartNew();
                vis.RebuildFromSave(grownSave);
                sw.Stop();
                applyMs.Add(sw.Elapsed.TotalMilliseconds);
                var st = vis.ContinuousPathRenderer.GetStats();
                var plan = vis.LastContinuousPathPlan;
                var rep = vis.ContinuousPathRenderer.LastBuildReport;
                Append($"  session {s}: roadCells {before}->{SettlementRoadCount(grownSave)} rebuild={sw.Elapsed.TotalMilliseconds:F1} ms nodes={plan?.Nodes.Count} edges={plan?.Edges.Count} components={plan?.ConnectedComponents} rebuiltChunks={st.LastRebuiltChunkCount} reused={st.LastReusedChunkCount} removed={st.LastRemovedChunkCount} live={st.LiveOwnedMeshCount} created={st.TotalMeshesCreated} destroyed={st.TotalMeshesDestroyed} (dirty-chunk build: foldbacks={rep?.FoldbackTriangles} flips={rep?.WindingFlipsRibbon}/{rep?.WindingFlipsCap}/{rep?.WindingFlipsJunction} downward={rep?.DownwardTriangles})");
                if (rep != null && (rep.FoldbackTriangles != 0 || rep.WindingFlipsRibbon != 0 || rep.WindingFlipsCap != 0 || rep.WindingFlipsJunction != 0 || rep.DownwardTriangles != 0)) incrementalOk = false;
                if (plan != null && plan.ConnectedComponents != 1) incrementalOk = false;
                if (st.LiveOwnedMeshCount != st.TotalMeshesCreated - st.TotalMeshesDestroyed) incrementalOk = false;
                yield return 6;
            }

            Check("incremental sessions: no foldbacks/flips/downward, one road component, owned-mesh accounting consistent", incrementalOk);
            Append("  incremental rebuild ms: " + FormatRuns(applyMs));

            renderer = vis.ContinuousPathRenderer;
            var incHash = ChunkHashes(renderer);
            var incStats = renderer.GetStats();
            // fresh full rebuild of the same state (OFF -> ON) must equal the incremental result
            vis.UseContinuousPathNetworkRenderer = false;
            vis.RebuildFromSave(saveService.AuthoritativeSnapshot);
            yield return 2;
            vis.UseContinuousPathNetworkRenderer = true;
            var freshSw = Stopwatch.StartNew();
            vis.RebuildFromSave(saveService.AuthoritativeSnapshot);
            freshSw.Stop();
            yield return 2;
            renderer = vis.ContinuousPathRenderer;
            var freshHash = ChunkHashes(renderer);
            Append($"  fresh full rebuild after incremental: {freshSw.Elapsed.TotalMilliseconds:F1} ms; chunks={renderer.GetStats().ActiveChunkCount} (incremental had {incStats.ActiveChunkCount})");
            Check("incremental result == fresh full rebuild (per-chunk mesh hashes)", incHash == freshHash);
            CheckOnQuality("final-fresh", vis, renderer);

            // ---------------- close-ups of the final grown state (real scenario only; others were done before)
            if (Scenario == "real")
            {
                Append("== final grown state close-ups ==");
                var fInfo = DescribeOn(vis, renderer);
                CheckHutDoors(vis, renderer, fInfo, "G");
                var f = OverviewFocus(vis);
                SetCamera(cam, f.center, 45f, 45f, f.size, 0);
                yield return Shot("G-grown-overview-iso");
                SetCamera(cam, f.center, 90f, 0f, f.size, 0);
                yield return Shot("G-grown-overview-top");
                var gTargets = PickTargets(vis, renderer, fInfo);
                foreach (var t in gTargets) Append($"    target {t.Label} at ({t.Pos.x:F2},{t.Pos.y:F2},{t.Pos.z:F2}) - {t.Why}");
                foreach (var t in gTargets)
                {
                    SetCamera(cam, t.Pos, 45f, 45f, 2.0f, 0);
                    yield return Shot("G-closeup-" + t.Label + "-iso");
                    SetCamera(cam, t.Pos, 90f, 0f, 1.2f, 0);
                    yield return Shot("G-closeup-" + t.Label + "-top");
                }
            }

            // ---------------- final OFF (rollback) and wrap-up
            Append("== final rollback to OFF ==");
            SetCamera(cam, focus.center, 45f, 45f, focus.size, 0);
            vis.UseContinuousPathNetworkRenderer = false;
            vis.RebuildFromSave(saveService.AuthoritativeSnapshot);
            yield return 2;
            var finalOff = CountObjects();
            var finalCleared = vis.LastClearedRendererStats;
            Append($"  lastCleared: {FormatStats(finalCleared)}; counts meshes={finalOff.meshes} gameObjects={finalOff.gameObjects}");
            Check("final OFF: renderer destroyed, created == destroyed, nothing live", vis.ContinuousPathRenderer == null && finalCleared != null && finalCleared.LiveOwnedMeshCount == 0 && finalCleared.TotalMeshesCreated == finalCleared.TotalMeshesDestroyed);
            yield return Shot("Z-off-rollback");
            if (controller != null) controller.enabled = true;
            cam.transform.SetPositionAndRotation(defaultPos, defaultRot);
            cam.orthographicSize = defaultSize;

            var failed = _checks.FindAll(c => c.StartsWith("FAIL", StringComparison.Ordinal));
            Append(string.Empty);
            Append("CHECKS:");
            foreach (var c in _checks) Append("  " + c);
            Append($"RESULT [{Scenario}]: {(failed.Count == 0 ? "PASS" : "FAIL (" + failed.Count + ")")}");
        }

        // ------------------------------------------------------------------ helpers: sampling

        private sealed class OnInfo
        {
            public List<Vector3> CoreVerts = new List<Vector3>();
            public float MaxSlopeDegrees;
            public Vector3 SteepestPos;
            public float MinY = float.MaxValue, MaxY = float.MinValue;
            public float MinUpDot = 1f;
        }

        private static OnInfo DescribeOn(HomeSettlementVisualizer vis, SettlementPathNetworkRenderer renderer)
        {
            var info = new OnInfo();
            var plan = vis.LastContinuousPathPlan;
            var build = renderer.LastBuildReport;
            Append($"  visualizer: flag={vis.UseContinuousPathNetworkRenderer} legacyPathObjects={CountNamedChildren(vis.GrownSettlementRoot, "Path")} legacyHutObjects={CountNamedChildren(vis.GrownSettlementRoot, "Hut")}");
            if (plan != null)
            {
                Append($"  topology: nodes={plan.Nodes.Count} edges={plan.Edges.Count} junctions={plan.Junctions.Count} components={plan.ConnectedComponents} huts={plan.HutReports.Count} starterIssues={plan.StarterIssues.Count}");
                var statuses = new Dictionary<string, int>();
                foreach (var h in plan.HutReports)
                {
                    statuses.TryGetValue(h.Status.ToString(), out var c);
                    statuses[h.Status.ToString()] = c + 1;
                }

                var parts = new List<string>();
                foreach (var kv in statuses) parts.Add(kv.Key + "=" + kv.Value);
                Append("  hut status: " + string.Join(", ", parts));
            }

            if (build != null)
            {
                Append($"  mesh build (last apply): chunks={build.Chunks.Count} foldbacks={build.FoldbackTriangles} flips(ribbon/cap/junction)={build.WindingFlipsRibbon}/{build.WindingFlipsCap}/{build.WindingFlipsJunction} downward={build.DownwardTriangles} degenerate={build.DegenerateSkipped} minUpDot={build.MinUpwardNormalDot:F3} ribbonWidth min/max={build.MinRibbonWidthMeters:F3}/{build.MaxRibbonWidthMeters:F3} widthRatioMin={build.MinWidthRatio:F3} narrower70%={build.NarrowRibbonSamples}/{build.RibbonSamples} slopeMax={build.MaxSlopeDegrees:F1} deg seams(pos/edge/height/attach)={build.MaxBorderPositionDelta:F6}/{build.MaxBorderEdgeDelta:F6}/{build.MaxBorderHeightDelta:F6}/{build.MaxJunctionAttachDelta:F6}");
                foreach (var site in build.DefectSites) Append("    defect: " + site);
                for (var i = 0; i < build.NarrowSites.Count && i < 6; i++) Append("    " + build.NarrowSites[i]);
            }

            Append("  renderer: " + FormatStats(renderer.GetStats()));
            Append($"  pond core vertices inside the pond: {CountCoreVerticesInsidePond(renderer)}");

            // world-space core geometry (terrain relief under the road, steepest slope, normals)
            var filters = renderer.GetComponentsInChildren<MeshFilter>();
            foreach (var mf in filters)
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || mr.sharedMaterial != renderer.CoreMaterial) continue;
                var mesh = mf.sharedMesh;
                var verts = mesh.vertices;
                var tris = mesh.triangles;
                var m = mf.transform.localToWorldMatrix;
                var world = new Vector3[verts.Length];
                for (var i = 0; i < verts.Length; i++)
                {
                    world[i] = m.MultiplyPoint3x4(verts[i]);
                    info.CoreVerts.Add(world[i]);
                    if (world[i].y < info.MinY) info.MinY = world[i].y;
                    if (world[i].y > info.MaxY) info.MaxY = world[i].y;
                }

                for (var t = 0; t + 2 < tris.Length; t += 3)
                {
                    var a = world[tris[t]];
                    var b = world[tris[t + 1]];
                    var c = world[tris[t + 2]];
                    var n = Vector3.Cross(b - a, c - a).normalized;
                    if (n.y < info.MinUpDot) info.MinUpDot = n.y;
                    for (var e = 0; e < 3; e++)
                    {
                        var p = e == 0 ? a : e == 1 ? b : c;
                        var q = e == 0 ? b : e == 1 ? c : a;
                        var horiz = Mathf.Sqrt((p.x - q.x) * (p.x - q.x) + (p.z - q.z) * (p.z - q.z));
                        if (horiz < 0.05f) continue;
                        var slope = Mathf.Atan2(Mathf.Abs(p.y - q.y), horiz) * Mathf.Rad2Deg;
                        if (slope > info.MaxSlopeDegrees)
                        {
                            info.MaxSlopeDegrees = slope;
                            info.SteepestPos = (p + q) * 0.5f;
                        }
                    }
                }
            }

            if (info.CoreVerts.Count > 0)
                Append($"  world core geometry: verts={info.CoreVerts.Count} y range {info.MinY:F2}..{info.MaxY:F2} (relief {info.MaxY - info.MinY:F2} m) steepest triangle edge={info.MaxSlopeDegrees:F1} deg at ({info.SteepestPos.x:F2},{info.SteepestPos.z:F2}) world min upDot={info.MinUpDot:F3}");
            return info;
        }

        private static void CheckOnQuality(string tag, HomeSettlementVisualizer vis, SettlementPathNetworkRenderer renderer)
        {
            if (renderer == null) { Check(tag + ": renderer present", false); return; }
            var plan = vis.LastContinuousPathPlan;
            var build = renderer.LastBuildReport;
            var stats = renderer.GetStats();
            if (plan != null)
            {
                Check(tag + ": one connected road component", plan.ConnectedComponents == 1);
                var okHuts = true;
                foreach (var h in plan.HutReports)
                {
                    if (h.Status != SettlementPathTopology.HutConnectionStatus.Connected &&
                        h.Status != SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection) okHuts = false;
                }

                Check(tag + ": every hut connected to the road", okHuts);
                Check(tag + ": no starter audit issues", plan.StarterIssues.Count == 0);
            }

            if (build != null)
            {
                Check(tag + ": zero foldbacks / winding flips / downward triangles",
                    build.FoldbackTriangles == 0 && build.WindingFlipsRibbon == 0 && build.WindingFlipsCap == 0 && build.WindingFlipsJunction == 0 && build.DownwardTriangles == 0 && build.DegenerateSkipped == 0);
            }

            Check(tag + ": zero core vertices inside the pond", CountCoreVerticesInsidePond(renderer) == 0);
            Check(tag + ": owned-mesh accounting (live == created - destroyed)", stats.LiveOwnedMeshCount == stats.TotalMeshesCreated - stats.TotalMeshesDestroyed);
            Check(tag + ": shared materials only", stats.UsesSharedMaterialsOnly);
        }

        private static (Vector3 center, float size) OverviewFocus(HomeSettlementVisualizer vis)
        {
            var plan = vis.LastContinuousPathPlan;
            if (plan == null || plan.Edges.Count == 0) return (new Vector3(0f, 1.2f, 0f), 6f);
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var e in plan.Edges)
            {
                foreach (var p in e.Points)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
            }

            var c = (min + max) * 0.5f;
            var extent = Mathf.Max(max.x - min.x, max.y - min.y) * 0.5f;
            return (new Vector3(c.x, 1.2f, c.y), Mathf.Clamp(extent * 0.8f + 2.5f, 5f, 16f));
        }

        // ------------------------------------------------------------------ helpers: close-up targets

        private static List<Target> PickTargets(HomeSettlementVisualizer vis, SettlementPathNetworkRenderer renderer, OnInfo info)
        {
            var list = new List<Target>();
            var plan = vis.LastContinuousPathPlan;
            if (plan == null) return list;

            // hut doors (starter / first grown huts)
            for (var i = 0; i < plan.HutReports.Count && i < 2; i++)
            {
                var h = plan.HutReports[i];
                var join = Vector2.Lerp(h.DoorWorld, h.JoinPoint, 0.5f);
                list.Add(new Target { Label = "hutdoor" + i, Pos = GroundPoint(info, join), Why = $"hut {h.HutId} door approach, status={h.Status}, variant={h.Variant}, route={h.RouteLength:F2} m" });
            }

            // sharpest junction angles
            var junctions = new List<(float angle, SettlementPathTopology.PathJunction j)>();
            foreach (var j in plan.Junctions)
            {
                if (j.OutgoingTangents == null || j.OutgoingTangents.Count < 3) continue;
                var angles = new List<float>();
                foreach (var t in j.OutgoingTangents) angles.Add(Mathf.Atan2(t.y, t.x) * Mathf.Rad2Deg);
                angles.Sort();
                var minGap = 360f;
                for (var k = 0; k < angles.Count; k++)
                {
                    var next = angles[(k + 1) % angles.Count] + (k + 1 == angles.Count ? 360f : 0f);
                    minGap = Mathf.Min(minGap, next - angles[k]);
                }

                junctions.Add((minGap, j));
            }

            junctions.Sort((a, b) => a.angle.CompareTo(b.angle));
            for (var i = 0; i < junctions.Count && i < 2; i++)
            {
                var jj = junctions[i];
                list.Add(new Target { Label = "sharpjunction" + i, Pos = GroundPoint(info, jj.j.Position), Why = $"junction {jj.j.NodeId} with {jj.j.OutgoingTangents.Count} spokes, narrowest sector {jj.angle:F0} deg" });
            }

            if (junctions.Count > 2)
            {
                var jj = junctions[junctions.Count - 1];
                list.Add(new Target { Label = "widejunction", Pos = GroundPoint(info, jj.j.Position), Why = $"junction {jj.j.NodeId}, widest-minimum sector {jj.angle:F0} deg (reference)" });
            }

            // sharpest turns
            var turns = new List<(float turn, Vector2 pos, string edge, float radius)>();
            foreach (var e in plan.Edges)
            {
                for (var i = 1; i < e.Points.Count - 1; i++)
                {
                    var a = (e.Points[i] - e.Points[i - 1]);
                    var b = (e.Points[i + 1] - e.Points[i]);
                    if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f) continue;
                    var turn = Vector2.Angle(a, b);
                    var ds = 0.5f * (a.magnitude + b.magnitude);
                    var radius = turn > 0.5f ? ds / (turn * Mathf.Deg2Rad) : 999f;
                    turns.Add((turn, e.Points[i], e.Id, radius));
                }
            }

            turns.Sort((a, b) => b.turn.CompareTo(a.turn));
            var taken = new List<Vector2>();
            for (var i = 0; i < turns.Count && taken.Count < 2; i++)
            {
                var t = turns[i];
                var tooClose = false;
                foreach (var p in taken) if (Vector2.Distance(p, t.pos) < 1.5f) tooClose = true;
                if (tooClose) continue;
                taken.Add(t.pos);
                list.Add(new Target { Label = "sharpturn" + (taken.Count - 1), Pos = GroundPoint(info, t.pos), Why = $"edge {t.edge}: heading change {t.turn:F0} deg in one {0.12f:F2} m step, local radius {t.radius:F2} m" });
            }

            // steepest slope under the road
            if (info.MaxSlopeDegrees > 0.5f)
                list.Add(new Target { Label = "steepest", Pos = info.SteepestPos, Why = $"steepest triangle edge on the road mesh {info.MaxSlopeDegrees:F1} deg" });

            // narrowest ribbon (C-3L width diagnostics)
            var build = renderer.LastBuildReport;
            if (build != null && build.NarrowSites.Count > 0)
            {
                var bestRatio = float.MaxValue;
                string bestSite = null;
                Vector2 bestPos = Vector2.zero;
                foreach (var site in build.NarrowSites)
                {
                    var mr = Regex.Match(site, @"ratio=([0-9.]+) at=\((-?[0-9.]+),(-?[0-9.]+)\)");
                    if (!mr.Success) continue;
                    var ratio = float.Parse(mr.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (ratio >= bestRatio) continue;
                    bestRatio = ratio;
                    bestSite = site;
                    bestPos = new Vector2(float.Parse(mr.Groups[2].Value, CultureInfo.InvariantCulture), float.Parse(mr.Groups[3].Value, CultureInfo.InvariantCulture));
                }

                if (bestSite != null)
                    list.Add(new Target { Label = "narrowest", Pos = GroundPoint(info, bestPos), Why = bestSite });
            }

            return list;
        }

        /// <summary>
        /// Compares the road plan's door with the door of the hut object that is really in the scene (after the
        /// visual door-yaw correction) and checks that road geometry actually reaches the visible door.
        /// </summary>
        private static void CheckHutDoors(HomeSettlementVisualizer vis, SettlementPathNetworkRenderer renderer, OnInfo info, string tag)
        {
            var plan = vis.LastContinuousPathPlan;
            var root = vis.GrownSettlementRoot;
            if (plan == null || root == null) return;

            var huts = new List<Transform>();
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                for (var i = 0; i < cur.childCount; i++)
                {
                    var child = cur.GetChild(i);
                    if (child.name.StartsWith("Hut_", StringComparison.Ordinal)) huts.Add(child);
                    else stack.Push(child);
                }
            }

            Append($"  hut door check [{tag}]: scene huts={huts.Count} plan huts={plan.HutReports.Count}");
            var viewsField = typeof(HomeSettlementVisualizer).GetField("_views", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (viewsField != null && viewsField.GetValue(vis) is System.Collections.IDictionary views)
            {
                foreach (System.Collections.DictionaryEntry de in views)
                {
                    var rootField = de.Value.GetType().GetField("root");
                    var go = rootField?.GetValue(de.Value) as GameObject;
                    var hutNames = new StringBuilder();
                    if (go != null) foreach (Transform c in go.transform) if (c.name.StartsWith("Hut", StringComparison.Ordinal)) hutNames.Append(c.name).Append("(").Append(c.localEulerAngles.y.ToString("F0")).Append(") ");
                    Append($"    view key={de.Key} root={(go != null ? go.name : "null")} active={(go != null && go.activeSelf)} huts: {hutNames}");
                }
            }
            var allOk = true;
            foreach (var rep in plan.HutReports)
            {
                Transform hut = null;
                var best = float.MaxValue;
                foreach (var h in huts)
                {
                    var d = Vector2.Distance(new Vector2(h.position.x, h.position.z), rep.DoorWorld);
                    if (d < best) { best = d; hut = h; }
                }

                if (hut == null) { Append($"    {rep.HutId}: no scene hut"); allOk = false; continue; }

                Transform doorT = null;
                foreach (var t in hut.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0) { doorT = t; break; }
                }

                var names = new StringBuilder();
                foreach (Transform c in hut) names.Append(c.name).Append(',');
                var fwd = new Vector2(hut.forward.x, hut.forward.z).normalized;
                var angle = Vector2.Angle(fwd, rep.DoorForward);
                Vector2 modelDoor;
                string doorSource;
                if (doorT != null)
                {
                    var rends = doorT.GetComponentsInChildren<Renderer>();
                    var c3 = rends.Length > 0 ? rends[0].bounds.center : doorT.position;
                    modelDoor = new Vector2(c3.x, c3.z);
                    doorSource = "model child '" + doorT.name + "'";
                }
                else
                {
                    modelDoor = new Vector2(hut.position.x, hut.position.z) + fwd * (0.6f * hut.lossyScale.x);
                    doorSource = "hut forward * 0.6 (prefab has no Door child; children: " + names + ")";
                }

                var nearest = float.MaxValue;
                foreach (var v in info.CoreVerts)
                {
                    var d = Vector2.Distance(new Vector2(v.x, v.z), modelDoor);
                    if (d < nearest) nearest = d;
                }

                var planDoorGap = Vector2.Distance(modelDoor, rep.DoorWorld);
                Append($"    {rep.HutId}: status={rep.Status} hutPos=({hut.position.x:F2},{hut.position.z:F2}) sceneYaw(world/local)={hut.eulerAngles.y:F0}/{hut.localEulerAngles.y:F0} planYaw(original/effective)={rep.OriginalYawDegrees:F0}/{rep.EffectiveYawDegrees:F0} planDoor=({rep.DoorWorld.x:F2},{rep.DoorWorld.y:F2}) planFwd=({rep.DoorForward.x:F2},{rep.DoorForward.y:F2}) hutFwdVsPlanFwd={angle:F1} deg modelDoor={doorSource} at ({modelDoor.x:F2},{modelDoor.y:F2}) planDoor-vs-model={planDoorGap:F2} m nearestRoadVertexToModelDoor={nearest:F2} m");
                if (angle > 15f || planDoorGap > 0.35f || nearest > 0.45f) allOk = false;
            }

            Check(tag + ": hut doors: plan door matches the visible hut door and the road reaches it", allOk);
        }

        private static Vector3 GroundPoint(OnInfo info, Vector2 xz)
        {
            var best = float.MaxValue;
            var y = 1.0f;
            foreach (var v in info.CoreVerts)
            {
                var d = (v.x - xz.x) * (v.x - xz.x) + (v.z - xz.y) * (v.z - xz.y);
                if (d < best) { best = d; y = v.y; }
            }

            return new Vector3(xz.x, y, xz.y);
        }

        // ------------------------------------------------------------------ helpers: camera / screenshots / UI

        private static void SetCamera(Camera cam, Vector3 focus, float pitch, float yaw, float orthoSize, int unused)
        {
            cam.orthographic = true;
            cam.orthographicSize = orthoSize;
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            cam.transform.rotation = rot;
            cam.transform.position = focus + rot * new Vector3(0f, 0f, -40f);
        }

        private static IEnumerator Shot(string name)
        {
            HideUi(false); // modals (offline return report) may appear after the first hide
            yield return 2;
            _shotIndex++;
            var path = Path.Combine(OutDir, $"c3l-{Scenario}-{_shotIndex:D2}-{name}.png").Replace('\\', '/');
            // Render the main camera into a RenderTexture: the game's OnGUI HUDs (buttons, report) are not part of the camera image.
            var cam = Camera.main;
            const int w = 1280;
            const int h = 960;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var previousTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.aspect = w / (float)h;
            cam.Render();
            cam.targetTexture = previousTarget;
            cam.ResetAspect();
            var previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = previousActive;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            Append("  screenshot: " + path);
            yield return 1;
        }

        private static void HideUi(bool log = true)
        {
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            for (var i = 0; i < canvases.Length; i++) canvases[i].enabled = false;
            var documents = UnityEngine.Object.FindObjectsByType<UnityEngine.UIElements.UIDocument>(FindObjectsSortMode.None);
            for (var i = 0; i < documents.Length; i++)
            {
                if (documents[i].rootVisualElement != null)
                    documents[i].rootVisualElement.style.display = UnityEngine.UIElements.DisplayStyle.None;
            }

            // The offline return report is an OnGUI HUD (not a Canvas): switch its component off so OnGUI stops drawing it.
            var huds = UnityEngine.Object.FindObjectsByType<AfkIsland.UI.ReturnReportHud>(FindObjectsSortMode.None);
            for (var i = 0; i < huds.Length; i++) huds[i].enabled = false;
            if (log) Append($"  hid {canvases.Length} canvases, {documents.Length} UIDocuments and {huds.Length} OnGUI return-report HUDs");
        }

        // ------------------------------------------------------------------ helpers: counters / hashes / report

        private static (int meshes, int gameObjects) CountObjects()
        {
            var meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var gos = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            return (meshes, gos);
        }

        private static string ChunkHashes(SettlementPathNetworkRenderer renderer)
        {
            var sb = new StringBuilder();
            for (var z = -6; z <= 6; z++)
            {
                for (var x = -6; x <= 6; x++)
                {
                    if (!renderer.TryGetChunkMeshes(new Vector2Int(x, z), out var core, out var fringe)) continue;
                    sb.Append('(').Append(x).Append(',').Append(z).Append(")=").Append(MeshHash(core).ToString("X16")).Append('/').Append(MeshHash(fringe).ToString("X16")).Append(';');
                }
            }

            return sb.ToString();
        }

        private static ulong MeshHash(Mesh m)
        {
            if (m == null) return 0;
            unchecked
            {
                ulong h = 1469598103934665603UL;
                var v = m.vertices;
                for (var i = 0; i < v.Length; i++)
                {
                    h = (h ^ (uint)Mathf.RoundToInt(v[i].x * 10000f)) * 1099511628211UL;
                    h = (h ^ (uint)Mathf.RoundToInt(v[i].y * 10000f)) * 1099511628211UL;
                    h = (h ^ (uint)Mathf.RoundToInt(v[i].z * 10000f)) * 1099511628211UL;
                }

                var t = m.triangles;
                for (var i = 0; i < t.Length; i++) h = (h ^ (uint)t[i]) * 1099511628211UL;
                return h;
            }
        }

        private static int SettlementRoadCount(GameSaveData save)
        {
            return CozyDioramaStageC3IStarterSettlementVerifier.RoadSet(save).Count;
        }

        private static void DescribeVisualizer(HomeSettlementVisualizer vis)
        {
            Append($"  visualizer: flag={vis.UseContinuousPathNetworkRenderer} growthRootChildren={(vis.GrownSettlementRoot != null ? vis.GrownSettlementRoot.childCount : -1)} legacyPathObjects={CountNamedChildren(vis.GrownSettlementRoot, "Path")} legacyHutObjects={CountNamedChildren(vis.GrownSettlementRoot, "Hut")} renderer={(vis.ContinuousPathRenderer != null)}");
        }

        private static string FormatStats(SettlementPathNetworkRenderer.RendererStats s)
        {
            if (s == null) return "null";
            return $"activeChunks={s.ActiveChunkCount} activeMeshes={s.ActiveMeshCount} liveOwnedMeshes={s.LiveOwnedMeshCount} created={s.TotalMeshesCreated} destroyed={s.TotalMeshesDestroyed} applyCalls={s.TotalApplyCalls} lastRebuilt={s.LastRebuiltChunkCount} lastReused={s.LastReusedChunkCount} lastRemoved={s.LastRemovedChunkCount} coreVerts={s.TotalCoreVertices} fringeVerts={s.TotalFringeVertices}";
        }

        private static string FormatRuns(List<double> ms)
        {
            var sorted = new List<double>(ms);
            sorted.Sort();
            var median = sorted[sorted.Count / 2];
            return $"[{string.Join(", ", ms.ConvertAll(x => x.ToString("F1", CultureInfo.InvariantCulture)))}] median={median:F1} max={sorted[sorted.Count - 1]:F1}";
        }

        private static int CountNamedChildren(Transform root, string prefix)
        {
            if (root == null) return 0;
            var count = 0;
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                for (var i = 0; i < cur.childCount; i++)
                {
                    var child = cur.GetChild(i);
                    if (child.name.StartsWith(prefix, StringComparison.Ordinal)) count++;
                    else stack.Push(child);
                }
            }

            return count;
        }

        private static int CountCoreVerticesInsidePond(SettlementPathNetworkRenderer renderer)
        {
            if (renderer == null) return 0;
            var center = WaterSourceLayout.GetWorldCenter();
            var rx = WaterSourceLayout.WaterHalfExtentX;
            var rz = WaterSourceLayout.WaterHalfExtentZ;
            var inside = 0;
            foreach (var mf in renderer.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || mr.sharedMaterial != renderer.CoreMaterial) continue;
                var verts = mf.sharedMesh.vertices;
                var m = mf.transform.localToWorldMatrix;
                for (var i = 0; i < verts.Length; i++)
                {
                    var w = m.MultiplyPoint3x4(verts[i]);
                    var dx = (w.x - center.x) / rx;
                    var dz = (w.z - center.z) / rz;
                    if (dx * dx + dz * dz < 0.96f) inside++;
                }
            }

            return inside;
        }

        private static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static void Check(string name, bool ok)
        {
            _checks.Add((ok ? "ok   " : "FAIL ") + name);
            if (!ok) Append("  CHECK FAILED: " + name);
        }

        private static void Append(string line)
        {
            Debug.Log("[C3L] " + line);
            File.AppendAllText(ReportPath, line + "\n", new UTF8Encoding(false));
        }
    }
}
