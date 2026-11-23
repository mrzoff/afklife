#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Measures WorldScatterField.Generate on a real save and on a synthetic worst case.
    /// Editor-only: never referenced from runtime code and excluded from player builds.
    /// It reproduces the exact delegates IslandMeshVisualizer passes to the scatter, but does
    /// not create GameObjects, meshes or materials.
    /// </summary>
    public static class WorldScatterBenchmark
    {
        private const int MeasuredRuns = 5;
        private const int FallbackScatterSeed = unchecked((int)0x11B0A11);

        [MenuItem("AFK Island/Benchmark World Scatter (11G-B)")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("World scatter benchmark failed with code " + code);
            }
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private sealed class Dataset
        {
            public string Name;
            public string Kind;
            public string Notes;
            public bool SyntheticDensity;
            public int Seed;
            public readonly List<(int x, int z)> Chunks = new();
            public readonly Dictionary<long, byte[]> Stage = new();
            public readonly Dictionary<long, byte[]> Biome = new();
            public readonly List<Vector2> HutCenters = new();
            public readonly List<Vector2> Blockers = new();
            public int ForestVegetationCells;

            public int DensityCalls;
            public int ValidCalls;
            public int EmittedPoints;
        }

        private sealed class RunResult
        {
            public double TotalMs;
            public double PerChunkSumMs;
            public double[] ChunkMs;
            public int Points;
            public int DensityCalls;
            public int ValidCalls;
            public long AllocBytes;
            public long MonoBefore;
            public long MonoAfter;
        }

        private static int RunInternal()
        {
            var report = new StringBuilder();
            void Line(string s)
            {
                report.AppendLine(s);
                Debug.Log("[Bench] " + s);
            }

            try
            {
                Line("=== WorldScatterField benchmark (Stage 11G-B) ===");
                Line($"Unity: {Application.unityVersion} | OS: {SystemInfo.operatingSystem}");
                Line($"CPU: {SystemInfo.processorType} | logical cores: {SystemInfo.processorCount} | " +
                     $"reported {SystemInfo.processorFrequency} MHz");
                Line($"RAM: {SystemInfo.systemMemorySize} MB | GPU: {SystemInfo.graphicsDeviceName}");
                Line($"Batchmode: {Application.isBatchMode} | graphics: {SystemInfo.graphicsDeviceType}");
                Line($"Chunk size: {GrowthWorldPlacement.ChunkSizeMeters} m | grid {ChunkMaskCodec.GridSize}x{ChunkMaskCodec.GridSize}");
                Line($"Spacing: min {WorldScatterField.DefaultTreeMinimumSpacing} m | " +
                     $"candidates {WorldScatterField.DefaultCandidateSpacing} m");
                Line("");

                WaterSourceLayout.ClearCenterCache();
                var pond = GameObject.Find("WaterSource");
                Line($"WaterSource object found in batchmode: {pond != null}; " +
                     $"reserve center used: {WaterSourceLayout.GetWorldCenter()}");
                if (pond == null)
                {
                    Line("NOTE: batchmode has no scene, so the pond reserve uses the static fallback pose. " +
                         "A Play Mode run may resolve a different centre.");
                }

                Line("");

                var savePath = Path.Combine(
                    Directory.GetParent(Application.dataPath)!.FullName, "Builds", "save056.json");
                if (!File.Exists(savePath))
                {
                    Line("ERROR: reference save not found at " + savePath);
                    return 2;
                }

                var save = JsonUtility.FromJson<GameSaveData>(File.ReadAllText(savePath));
                if (save == null)
                {
                    Line("ERROR: reference save could not be deserialized.");
                    return 3;
                }

                var real = BuildRealDataset(save);
                var stress = BuildStressDataset(real);

                Line($"D1 '{real.Name}' ({real.Kind})");
                Line($"  chunks in save: {save.worldGrowth.chunks.Count}; scatter chunks: {real.Chunks.Count} " +
                     "(legacy authored home chunk is not meshed)");
                Line($"  vegetation+forest cells: {real.ForestVegetationCells}");
                Line($"  seed: {real.Seed} (0x{real.Seed:X8})");
                Line($"  blockers: {real.Blockers.Count} | hut centres: {real.HutCenters.Count}");
                Line($"  {real.Notes}");
                Line("");
                Line($"D2 '{stress.Name}' ({stress.Kind})");
                Line($"  chunks: {stress.Chunks.Count} | vegetation+forest cells: {stress.ForestVegetationCells}");
                Line($"  seed: {stress.Seed} (0x{stress.Seed:X8}) | blockers: {stress.Blockers.Count}");
                Line($"  {stress.Notes}");
                Line("");

                if (!RunDataset(real, Line, out var realRuns))
                {
                    return 4;
                }

                if (!RunDataset(stress, Line, out var stressRuns))
                {
                    return 5;
                }

                if (!CheckPartition(real, Line))
                {
                    return 6;
                }

                if (!CheckPartition(stress, Line))
                {
                    return 6;
                }

                if (!CheckDeterminism(real, Line))
                {
                    return 7;
                }

                if (!CheckDeterminism(stress, Line))
                {
                    return 7;
                }

                WriteSummary(Line, "D1 realistic profile", real, realRuns);
                WriteSummary(Line, "D2 synthetic stress test", stress, stressRuns);

                var outPath = Path.Combine(
                    Directory.GetParent(Application.dataPath)!.FullName, "Builds",
                    "scatter-benchmark-report.txt");
                File.WriteAllText(outPath, report.ToString());
                Line("Report written to " + outPath);
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return 1;
            }
        }

        // ---------------------------------------------------------------- datasets

        private static Dataset BuildRealDataset(GameSaveData save)
        {
            var data = new Dataset
            {
                Name = "save056.json",
                Kind = "realistic production profile",
                Notes = "Real masks, real plant blockers, real hut cells from the reference save."
            };

            data.Seed = ResolveScatterSeed(save);
            data.Blockers.Add(new Vector2(-3.1f, -1.4f)); // authored yard cedar
            AddPlantBlockers(save.plants, data.Blockers);

            foreach (var chunk in save.worldGrowth.chunks)
            {
                if (chunk == null)
                {
                    continue;
                }

                var key = Pack(chunk.chunkX, chunk.chunkZ);
                byte[] stages;
                if (chunk.isLegacyBase)
                {
                    stages = new byte[ChunkMaskCodec.CellCount];
                    for (var i = 0; i < stages.Length; i++)
                    {
                        stages[i] = (byte)CellStage.Moss;
                    }
                }
                else if (!ChunkMaskCodec.TryDecodeBase64(chunk.stageMaskBase64, out stages, out _))
                {
                    stages = Array.Empty<byte>();
                }

                data.Stage[key] = stages;
                data.Biome[key] = BiomeMaskCodec.DecodeOrEmpty(chunk.biomeMaskBase64);

                if (ChunkMaskCodec.TryDecodeBase64(chunk.settlementMaskBase64, out var settle, out _))
                {
                    for (var i = 0; i < settle.Length; i++)
                    {
                        if (settle[i] == (byte)SettlementStage.Hut)
                        {
                            var center = GrowthWorldPlacement.CellCenterWorld(chunk.chunkX, chunk.chunkZ, i);
                            data.HutCenters.Add(new Vector2(center.x, center.z));
                        }
                    }
                }

                if (!chunk.isLegacyBase)
                {
                    data.Chunks.Add((chunk.chunkX, chunk.chunkZ));
                }
            }

            if (save.chunks != null)
            {
                for (var i = 0; i < save.chunks.Count; i++)
                {
                    AddPlantBlockers(save.chunks[i]?.plants, data.Blockers);
                }
            }

            data.Chunks.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));

            for (var i = 0; i < data.Chunks.Count; i++)
            {
                var (x, z) = data.Chunks[i];
                var key = Pack(x, z);
                if (!data.Stage.TryGetValue(key, out var stages) ||
                    !data.Biome.TryGetValue(key, out var biomes))
                {
                    continue;
                }

                for (var c = 0; c < ChunkMaskCodec.CellCount && c < stages.Length && c < biomes.Length; c++)
                {
                    if (stages[c] >= (byte)CellStage.Vegetation && biomes[c] == (byte)BiomeType.Forest)
                    {
                        data.ForestVegetationCells++;
                    }
                }
            }

            return data;
        }

        private static Dataset BuildStressDataset(Dataset real)
        {
            var data = new Dataset
            {
                Name = "all-forest",
                Kind = "synthetic stress test (not a production profile)",
                SyntheticDensity = true,
                Seed = real.Seed,
                Notes = "Same 19 chunk coordinates, every cell Vegetation+Forest, no huts, no plants, " +
                        "density forced to 1.0. This bounds the worst case and is NOT a production profile."
            };

            var stages = new byte[ChunkMaskCodec.CellCount];
            var biomes = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < stages.Length; i++)
            {
                stages[i] = (byte)CellStage.Vegetation;
                biomes[i] = (byte)BiomeType.Forest;
            }

            for (var i = 0; i < real.Chunks.Count; i++)
            {
                var (x, z) = real.Chunks[i];
                data.Chunks.Add((x, z));
                data.Stage[Pack(x, z)] = stages;
                data.Biome[Pack(x, z)] = biomes;
                data.ForestVegetationCells += ChunkMaskCodec.CellCount;
            }

            return data;
        }

        // ---------------------------------------------------------------- measurement

        private static bool RunDataset(Dataset data, Action<string> line, out List<RunResult> runs)
        {
            runs = new List<RunResult>(MeasuredRuns);
            line($"--- {data.Name}: warmup ---");

            var warm = MeasureOnce(data, syntheticDensity: data.SyntheticDensity);
            line($"warmup (includes JIT): {warm.TotalMs:0.0} ms, points={warm.Points}, " +
                 $"densityCalls={warm.DensityCalls}, validCalls={warm.ValidCalls}");
            line($"--- {data.Name}: {MeasuredRuns} measured runs ---");

            for (var i = 0; i < MeasuredRuns; i++)
            {
                var r = MeasureOnce(data, data.SyntheticDensity);
                runs.Add(r);
                line($"run {i + 1}: total={r.TotalMs:0.0} ms | sum(per chunk)={r.PerChunkSumMs:0.0} ms | " +
                     $"points={r.Points} | densityCalls={r.DensityCalls} | validCalls={r.ValidCalls} | " +
                     $"managedAlloc={r.AllocBytes / 1024.0:0.0} KB");
            }

            line("");
            return runs.Count == MeasuredRuns;
        }

        private static RunResult MeasureOnce(Dataset data, bool syntheticDensity)
        {
            var result = new RunResult { ChunkMs = new double[data.Chunks.Count] };
            var points = new List<WorldScatterField.ScatterPoint>(16);
            data.DensityCalls = 0;
            data.ValidCalls = 0;
            data.EmittedPoints = 0;

            var size = GrowthWorldPlacement.ChunkSizeMeters;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var monoBefore = Profiler.GetMonoUsedSizeLong();
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();

            var total = Stopwatch.StartNew();
            for (var i = 0; i < data.Chunks.Count; i++)
            {
                var (cx, cz) = data.Chunks[i];
                var origin = GrowthWorldPlacement.ChunkOriginXZ(cx, cz, size);
                var bounds = new Rect(origin.x, origin.y, size, size);

                var chunkWatch = Stopwatch.StartNew();
                WorldScatterField.Generate(
                    bounds,
                    data.Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    p => DensityAt(data, p, syntheticDensity),
                    p => IsValid(data, p),
                    data.Blockers,
                    points);
                chunkWatch.Stop();

                result.ChunkMs[i] = chunkWatch.Elapsed.TotalMilliseconds;
                result.PerChunkSumMs += result.ChunkMs[i];
                result.Points += points.Count;
            }

            total.Stop();

            var allocAfter = GC.GetAllocatedBytesForCurrentThread();
            var monoAfter = Profiler.GetMonoUsedSizeLong();

            result.TotalMs = total.Elapsed.TotalMilliseconds;
            result.DensityCalls = data.DensityCalls;
            result.ValidCalls = data.ValidCalls;
            result.AllocBytes = allocAfter - allocBefore;
            result.MonoBefore = monoBefore;
            result.MonoAfter = monoAfter;
            return result;
        }

        // ---------------------------------------------------------------- predicates

        private static float DensityAt(Dataset data, Vector2 point, bool synthetic)
        {
            data.DensityCalls++;

            if (!TryGetWorldCell(data, point.x, point.y, out _, out _, out var stage, out var biome))
            {
                return 0f;
            }

            if (stage < (byte)CellStage.Vegetation || biome != BiomeType.Forest)
            {
                return 0f;
            }

            if (synthetic)
            {
                return 1f;
            }

            var broad = WorldScatterField.SampleValueNoise(point.x, point.y, 18f, data.Seed, 113u);
            var detail = WorldScatterField.SampleValueNoise(point.x, point.y, 7f, data.Seed, 127u);
            return 0.28f * Mathf.Lerp(0.72f, 1.18f, broad) * Mathf.Lerp(0.82f, 1.12f, detail);
        }

        private static bool IsValid(Dataset data, Vector2 point)
        {
            data.ValidCalls++;

            if (!TryGetWorldCell(data, point.x, point.y, out var worldCellX, out var worldCellZ,
                    out var stage, out var biome))
            {
                return false;
            }

            if (stage < (byte)CellStage.Vegetation || biome != BiomeType.Forest)
            {
                return false;
            }

            if (IsNearShore(data, worldCellX, worldCellZ, 2))
            {
                return false;
            }

            if (IsNearHut(data, point.x, point.y, SettlementCellQuery.DefaultTreeHutClearance))
            {
                return false;
            }

            if (WaterSourceLayout.BlocksTrunk(point.x, point.y, WaterSourceLayout.TrunkRadius(0.9f)))
            {
                return false;
            }

            return true;
        }

        private static bool TryGetWorldCell(
            Dataset data,
            float worldX,
            float worldZ,
            out int worldCellX,
            out int worldCellZ,
            out byte stage,
            out BiomeType biome)
        {
            const float cellSize = 1f;
            const float half = 8f;
            worldCellX = Mathf.FloorToInt((worldX + half) / cellSize);
            worldCellZ = Mathf.FloorToInt((worldZ + half) / cellSize);
            stage = SampleWorldStage(data, worldCellX, worldCellZ);
            biome = SampleWorldBiome(data, worldCellX, worldCellZ);
            return stage != (byte)CellStage.Void;
        }

        private static byte SampleWorldStage(Dataset data, int worldCellX, int worldCellZ)
        {
            const int grid = 16;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            if (!data.Stage.TryGetValue(Pack(chunkX, chunkZ), out var stages))
            {
                return (byte)CellStage.Void;
            }

            var index = ChunkMaskCodec.CellIndex(lx, lz);
            return index >= 0 && index < stages.Length ? stages[index] : (byte)CellStage.Void;
        }

        private static BiomeType SampleWorldBiome(Dataset data, int worldCellX, int worldCellZ)
        {
            const int grid = 16;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            if (!data.Biome.TryGetValue(Pack(chunkX, chunkZ), out var biomes))
            {
                return BiomeType.None;
            }

            var index = ChunkMaskCodec.CellIndex(lx, lz);
            return index >= 0 && index < biomes.Length ? (BiomeType)biomes[index] : BiomeType.None;
        }

        private static bool IsNearShore(Dataset data, int worldCellX, int worldCellZ, int radius)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (SampleWorldStage(data, worldCellX + dx, worldCellZ + dz) == (byte)CellStage.Void)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNearHut(Dataset data, float worldX, float worldZ, float minimumDistance)
        {
            var minSq = minimumDistance * minimumDistance;
            for (var i = 0; i < data.HutCenters.Count; i++)
            {
                var dx = data.HutCenters[i].x - worldX;
                var dz = data.HutCenters[i].y - worldZ;
                if (dx * dx + dz * dz < minSq)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddPlantBlockers(List<PlantSaveData> plants, List<Vector2> into)
        {
            if (plants == null)
            {
                return;
            }

            for (var i = 0; i < plants.Count; i++)
            {
                var plant = plants[i];
                if (plant != null)
                {
                    into.Add(new Vector2(plant.posX, plant.posZ));
                }
            }
        }

        private static int ResolveScatterSeed(GameSaveData save)
        {
            if (save?.chunks != null)
            {
                for (var i = 0; i < save.chunks.Count; i++)
                {
                    var chunk = save.chunks[i];
                    if (chunk != null && chunk.chunkX == IslandChunk.HomeChunkX &&
                        chunk.chunkZ == IslandChunk.HomeChunkZ && chunk.randomSeed != 0)
                    {
                        return chunk.randomSeed;
                    }
                }
            }

            return FallbackScatterSeed;
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && (r > 0) != (b > 0))
            {
                q--;
            }

            return q;
        }

        private static long Pack(int x, int z) => ((long)x << 32) ^ (uint)z;

        // ---------------------------------------------------------------- checks

        private static bool CheckPartition(Dataset data, Action<string> line)
        {
            var size = GrowthWorldPlacement.ChunkSizeMeters;
            var perChunk = new List<WorldScatterField.ScatterPoint>(16);
            var all = new List<WorldScatterField.ScatterPoint>(512);
            var buffer = new List<WorldScatterField.ScatterPoint>(16);

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            for (var i = 0; i < data.Chunks.Count; i++)
            {
                var (cx, cz) = data.Chunks[i];
                var origin = GrowthWorldPlacement.ChunkOriginXZ(cx, cz, size);
                minX = Mathf.Min(minX, origin.x);
                minZ = Mathf.Min(minZ, origin.y);
                maxX = Mathf.Max(maxX, origin.x + size);
                maxZ = Mathf.Max(maxZ, origin.y + size);

                var bounds = new Rect(origin.x, origin.y, size, size);
                WorldScatterField.Generate(bounds, data.Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    p => DensityAt(data, p, data.SyntheticDensity),
                    p => IsValid(data, p), data.Blockers, buffer);
                all.AddRange(buffer);
            }

            var whole = new List<WorldScatterField.ScatterPoint>(512);
            var worldBounds = new Rect(minX, minZ, maxX - minX, maxZ - minZ);
            WorldScatterField.Generate(worldBounds, data.Seed,
                WorldScatterField.DefaultTreeMinimumSpacing,
                WorldScatterField.DefaultCandidateSpacing,
                p => DensityAt(data, p, data.SyntheticDensity),
                p => IsValid(data, p), data.Blockers, whole);

            var ok = ComparePoints(all, whole, out var detail);
            line($"[partition] {data.Name}: per-chunk points={all.Count}, whole-bounds points={whole.Count} " +
                 $"(bbox {worldBounds.width:0}x{worldBounds.height:0} m) -> {(ok ? "IDENTICAL" : "MISMATCH " + detail)}");

            // No duplicate world positions inside the partition.
            var seen = new HashSet<string>();
            for (var i = 0; i < all.Count; i++)
            {
                var key = $"{all[i].Position.x:R}:{all[i].Position.y:R}";
                if (!seen.Add(key))
                {
                    line($"[partition] {data.Name}: duplicate world position {key}");
                    return false;
                }
            }

            // Every point must sit inside exactly one requested chunk half-open interval.
            // Chunk (0,0) is centred on the origin, so the chunk index is floor((p + size/2) / size).
            for (var i = 0; i < all.Count; i++)
            {
                var p = all[i].Position;
                var cx = Mathf.FloorToInt((p.x + size * 0.5f) / size);
                var cz = Mathf.FloorToInt((p.y + size * 0.5f) / size);
                var owned = false;
                for (var c = 0; c < data.Chunks.Count; c++)
                {
                    if (data.Chunks[c].x == cx && data.Chunks[c].z == cz)
                    {
                        owned = true;
                        break;
                    }
                }

                if (!owned)
                {
                    line($"[partition] {data.Name}: point {p} is not owned by any requested chunk");
                    return false;
                }
            }

            return ok;
        }

        private static bool CheckDeterminism(Dataset data, Action<string> line)
        {
            var size = GrowthWorldPlacement.ChunkSizeMeters;
            var first = new List<WorldScatterField.ScatterPoint>(512);
            var second = new List<WorldScatterField.ScatterPoint>(512);
            var buffer = new List<WorldScatterField.ScatterPoint>(16);

            void GenerateInto(List<WorldScatterField.ScatterPoint> into)
            {
                for (var i = 0; i < data.Chunks.Count; i++)
                {
                    var (cx, cz) = data.Chunks[i];
                    var origin = GrowthWorldPlacement.ChunkOriginXZ(cx, cz, size);
                    var bounds = new Rect(origin.x, origin.y, size, size);
                    WorldScatterField.Generate(bounds, data.Seed,
                        WorldScatterField.DefaultTreeMinimumSpacing,
                        WorldScatterField.DefaultCandidateSpacing,
                        p => DensityAt(data, p, data.SyntheticDensity),
                        p => IsValid(data, p), data.Blockers, buffer);
                    into.AddRange(buffer);
                }
            }

            GenerateInto(first);
            GenerateInto(second);

            var ok = ComparePoints(first, second, out var detail);
            line($"[determinism] {data.Name}: seed={data.Seed} chunks={data.Chunks.Count} " +
                 $"points={first.Count} -> {(ok ? "IDENTICAL" : "MISMATCH " + detail)}");
            return ok;
        }

        private static bool ComparePoints(
            List<WorldScatterField.ScatterPoint> a,
            List<WorldScatterField.ScatterPoint> b,
            out string detail)
        {
            detail = null;
            if (a.Count != b.Count)
            {
                detail = $"count {a.Count} vs {b.Count}";
                return false;
            }

            var keyed = new Dictionary<string, int>(a.Count);
            for (var i = 0; i < a.Count; i++)
            {
                var key = $"{a[i].GridX}:{a[i].GridZ}:{a[i].Position.x:R}:{a[i].Position.y:R}";
                keyed.TryGetValue(key, out var n);
                keyed[key] = n + 1;
            }

            for (var i = 0; i < b.Count; i++)
            {
                var key = $"{b[i].GridX}:{b[i].GridZ}:{b[i].Position.x:R}:{b[i].Position.y:R}";
                if (!keyed.TryGetValue(key, out var n) || n == 0)
                {
                    detail = $"missing {key}";
                    return false;
                }

                keyed[key] = n - 1;
            }

            return true;
        }

        // ---------------------------------------------------------------- summary

        private static void WriteSummary(
            Action<string> line,
            string title,
            Dataset data,
            List<RunResult> runs)
        {
            var totals = new List<double>(runs.Count);
            var sums = new List<double>(runs.Count);
            for (var i = 0; i < runs.Count; i++)
            {
                totals.Add(runs[i].TotalMs);
                sums.Add(runs[i].PerChunkSumMs);
            }

            totals.Sort();
            sums.Sort();

            double Mean(List<double> v)
            {
                double s = 0;
                for (var i = 0; i < v.Count; i++)
                {
                    s += v[i];
                }

                return s / v.Count;
            }

            double Median(List<double> v) => v[v.Count / 2];

            var r0 = runs[0];
            line($"[summary] {title} ({data.Name})");
            line($"  wall total   min={totals[0]:0.0} median={Median(totals):0.0} " +
                 $"mean={Mean(totals):0.0} max={totals[totals.Count - 1]:0.0} ms");
            line($"  per-chunk sum min={sums[0]:0.0} median={Median(sums):0.0} " +
                 $"mean={Mean(sums):0.0} max={sums[sums.Count - 1]:0.0} ms");
            line($"  points={r0.Points} | densityCalls={r0.DensityCalls} | validCalls={r0.ValidCalls} | " +
                 $"chunks={data.Chunks.Count}");
            line($"  per chunk mean={Mean(totals) / data.Chunks.Count:0.00} ms | " +
                 $"calls per point={(r0.Points > 0 ? (double)r0.DensityCalls / r0.Points : 0):0.0}");
            line($"  managed alloc per run: {r0.AllocBytes / 1024.0:0.0} KB " +
                 "(GC.GetAllocatedBytesForCurrentThread)");
            line($"  Mono heap before/after: {r0.MonoBefore / 1024.0 / 1024.0:0.0} MB -> " +
                 $"{r0.MonoAfter / 1024.0 / 1024.0:0.0} MB " +
                 "(snapshot only; the delta is not the allocation of this call)");

            var chunkMin = double.MaxValue;
            var chunkMax = 0.0;
            for (var i = 0; i < r0.ChunkMs.Length; i++)
            {
                chunkMin = Math.Min(chunkMin, r0.ChunkMs[i]);
                chunkMax = Math.Max(chunkMax, r0.ChunkMs[i]);
            }

            line($"  slowest chunk spread: {chunkMin:0.00} .. {chunkMax:0.00} ms");
            line("");
        }
    }
}
#endif
