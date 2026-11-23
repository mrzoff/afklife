#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Builds Builds/save-forest-fixture.json from the reference save056.
    /// Stage 11G-B visual acceptance needs Forest inland cells; save056 yields 0 trees
    /// because its 120 plants block the whole island at 3.5 m spacing.
    /// This fixture paints inland Forest and keeps only authored plot plants as blockers.
    /// It is NOT a plant-coordinate regression save — use save056 for that.
    /// Settlement masks and save version 8 stay unchanged. Editor-only.
    /// </summary>
    public static class ForestFixtureBuilder
    {
        private const string SourceRelative = "Builds/save056.json";
        private const string FixtureRelative = "Builds/save-forest-fixture.json";
        private const string ReportRelative = "Builds/save-forest-fixture-report.txt";
        private const int ShoreExclusionCells = 2;
        private const int MinExpectedTrees = 12;

        [MenuItem("AFK Island/Build Forest Fixture (11G-B)")]
        public static void RunMenu()
        {
            var code = RunInternal(installToPersistent: false);
            if (code != 0)
            {
                throw new Exception("Forest fixture build failed with code " + code);
            }

            Debug.Log("[AFK Island] Forest fixture OK → " + FixtureRelative);
        }

        [MenuItem("AFK Island/Install Forest Fixture Save (Play Mode)")]
        public static void InstallMenu()
        {
            var code = RunInternal(installToPersistent: true);
            if (code != 0)
            {
                throw new Exception("Forest fixture install failed with code " + code);
            }

            Debug.Log("[AFK Island] Forest fixture installed to persistentDataPath for next Play Mode.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal(installToPersistent: false));
        }

        public static void RunBatchInstall()
        {
            EditorApplication.Exit(RunInternal(installToPersistent: true));
        }

        private static int RunInternal(bool installToPersistent)
        {
            try
            {
                var root = Directory.GetParent(Application.dataPath)!.FullName;
                var sourcePath = Path.Combine(root, SourceRelative);
                var fixturePath = Path.Combine(root, FixtureRelative);
                var reportPath = Path.Combine(root, ReportRelative);

                if (!File.Exists(sourcePath))
                {
                    Debug.LogError("Reference save missing: " + sourcePath);
                    return 2;
                }

                var sourceJson = File.ReadAllText(sourcePath);
                var save = JsonUtility.FromJson<GameSaveData>(sourceJson);
                if (save?.worldGrowth?.chunks == null || save.plants == null)
                {
                    Debug.LogError("Reference save could not be deserialized.");
                    return 3;
                }

                var plantsBefore = save.plants.Count;
                var settlementSnapshot = SnapshotSettlement(save);

                // Wild plants sit on a ~3.61 m lattice and block scatter at 3.5 m — leave only
                // authored plot plants so decorative trees can appear for visual acceptance.
                TrimToAuthoredPlotPlants(save);

                var stagesByChunk = new Dictionary<long, byte[]>();
                var biomesByChunk = new Dictionary<long, byte[]>();
                for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
                {
                    var growth = save.worldGrowth.chunks[i];
                    if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out var stageErr))
                    {
                        Debug.LogError($"Stage decode failed for ({growth.chunkX},{growth.chunkZ}): {stageErr}");
                        return 4;
                    }

                    if (!BiomeMaskCodec.TryDecodeBase64(growth.biomeMaskBase64, out var biomes, out var biomeErr))
                    {
                        Debug.LogError($"Biome decode failed for ({growth.chunkX},{growth.chunkZ}): {biomeErr}");
                        return 5;
                    }

                    var key = Pack(growth.chunkX, growth.chunkZ);
                    stagesByChunk[key] = stages;
                    biomesByChunk[key] = biomes;
                }

                var paintedBiome = 0;
                var promotedStage = 0;
                var forestVegetationBefore = CountForestVegetation(stagesByChunk, biomesByChunk);
                for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
                {
                    var growth = save.worldGrowth.chunks[i];
                    if (growth.isLegacyBase)
                    {
                        continue;
                    }

                    var key = Pack(growth.chunkX, growth.chunkZ);
                    var stages = stagesByChunk[key];
                    var biomes = biomesByChunk[key];
                    if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                    {
                        settle = new byte[ChunkMaskCodec.CellCount];
                    }

                    var stageChanged = false;
                    var biomeChanged = false;

                    for (var cell = 0; cell < ChunkMaskCodec.CellCount; cell++)
                    {
                        if (stages[cell] == (byte)CellStage.Void)
                        {
                            continue;
                        }

                        if (settle[cell] == (byte)SettlementStage.Hut)
                        {
                            continue;
                        }

                        var localX = cell % ChunkMaskCodec.GridSize;
                        var localZ = cell / ChunkMaskCodec.GridSize;
                        var worldCellX = growth.chunkX * ChunkMaskCodec.GridSize + localX;
                        var worldCellZ = growth.chunkZ * ChunkMaskCodec.GridSize + localZ;
                        if (IsNearVoid(worldCellX, worldCellZ, ShoreExclusionCells, stagesByChunk))
                        {
                            continue;
                        }

                        // Promote Moss/Soil inland so scatter has a contiguous Forest canopy.
                        if (stages[cell] < (byte)CellStage.Vegetation)
                        {
                            stages[cell] = (byte)CellStage.Vegetation;
                            promotedStage++;
                            stageChanged = true;
                        }

                        if (biomes[cell] != (byte)BiomeType.Forest)
                        {
                            biomes[cell] = (byte)BiomeType.Forest;
                            paintedBiome++;
                            biomeChanged = true;
                        }
                    }

                    if (stageChanged)
                    {
                        growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(stages);
                    }

                    if (biomeChanged)
                    {
                        growth.biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biomes);
                    }
                }

                var forestVegetationAfter = CountForestVegetation(stagesByChunk, biomesByChunk);

                if (!SettlementUnchanged(settlementSnapshot, save))
                {
                    Debug.LogError("Settlement masks changed while building the forest fixture.");
                    return 7;
                }

                if (save.version != 8)
                {
                    Debug.LogError("Fixture must remain save version 8.");
                    return 8;
                }

                var treeCount = EstimateTreeCount(save, stagesByChunk, biomesByChunk);
                if (treeCount < MinExpectedTrees)
                {
                    Debug.LogError(
                        $"Forest fixture expected at least {MinExpectedTrees} scatter trees, got {treeCount}. " +
                        $"Painted biome {paintedBiome}, promoted stage {promotedStage}; " +
                        $"Forest+Vegetation {forestVegetationBefore} → {forestVegetationAfter}.");
                    return 9;
                }

                var fixtureJson = JsonUtility.ToJson(save, true);
                Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
                File.WriteAllText(fixturePath, fixtureJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var report = new StringBuilder();
                report.AppendLine("=== Forest fixture (Stage 11G-B) ===");
                report.AppendLine($"source: {SourceRelative}");
                report.AppendLine($"output: {FixtureRelative}");
                report.AppendLine($"save.version: {save.version}");
                report.AppendLine($"plants: {plantsBefore} → {save.plants.Count} (authored plot_* only; wild cleared for tree visuals)");
                report.AppendLine("NOTE: plant coordinate regression stays on save056.json — this fixture is for trees.");
                report.AppendLine($"settlement masks: unchanged");
                report.AppendLine($"biome cells painted to Forest: {paintedBiome}");
                report.AppendLine($"stage cells promoted to Vegetation: {promotedStage}");
                report.AppendLine($"Forest+Vegetation cells: {forestVegetationBefore} → {forestVegetationAfter}");
                report.AppendLine($"estimated scatter trees (no scene WaterSource): {treeCount}");
                report.AppendLine($"shore exclusion: {ShoreExclusionCells} cells");
                report.AppendLine("legacy home chunk: untouched");
                report.AppendLine("Play Mode: AFK Island/Install Forest Fixture Save (Play Mode), then Continue.");
                File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
                Debug.Log(report.ToString());

                if (installToPersistent)
                {
                    InstallPersistent(fixturePath);
                }

                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("Forest fixture builder exception: " + ex);
                return 1;
            }
        }

        private static void InstallPersistent(string fixturePath)
        {
            var dest = Path.Combine(Application.persistentDataPath, "afk_island_save.json");
            var bak = dest + ".bak";
            if (File.Exists(dest))
            {
                File.Copy(dest, dest + ".pre-forest-fixture.bak", overwrite: true);
            }

            if (File.Exists(bak))
            {
                File.Copy(bak, bak + ".pre-forest-fixture.bak", overwrite: true);
            }

            File.Copy(fixturePath, dest, overwrite: true);
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }

            Debug.Log("[AFK Island] Wrote " + dest);
        }

        private static int EstimateTreeCount(
            GameSaveData save,
            Dictionary<long, byte[]> stagesByChunk,
            Dictionary<long, byte[]> biomesByChunk)
        {
            WaterSourceLayout.ClearCenterCache();
            var worldSeed = ResolveScatterSeed(save);
            var blockers = new List<Vector2>(160) { new Vector2(-3.1f, -1.4f) };
            CollectPlantBlockers(save, blockers);

            var hutCenters = CollectHutCenters(save);
            var total = 0;
            var size = GrowthWorldPlacement.ChunkSizeMeters;

            for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
            {
                var growth = save.worldGrowth.chunks[i];
                if (growth.isLegacyBase)
                {
                    continue;
                }

                var origin = GrowthWorldPlacement.ChunkOriginXZ(growth.chunkX, growth.chunkZ, size);
                var bounds = new Rect(origin.x, origin.y, size, size);
                var points = new List<WorldScatterField.ScatterPoint>(32);

                float DensityAt(Vector2 point)
                {
                    if (!TrySample(point.x, point.y, stagesByChunk, biomesByChunk, out _, out _, out var stage, out var biome))
                    {
                        return 0f;
                    }

                    if (stage < (byte)CellStage.Vegetation || biome != BiomeType.Forest)
                    {
                        return 0f;
                    }

                    var broad = WorldScatterField.SampleValueNoise(point.x, point.y, 18f, worldSeed, 113u);
                    var detail = WorldScatterField.SampleValueNoise(point.x, point.y, 7f, worldSeed, 127u);
                    return 0.28f * Mathf.Lerp(0.72f, 1.18f, broad) * Mathf.Lerp(0.82f, 1.12f, detail);
                }

                bool IsValid(Vector2 point)
                {
                    if (!TrySample(point.x, point.y, stagesByChunk, biomesByChunk,
                            out var worldCellX, out var worldCellZ, out var stage, out var biome))
                    {
                        return false;
                    }

                    if (stage < (byte)CellStage.Vegetation || biome != BiomeType.Forest)
                    {
                        return false;
                    }

                    if (IsNearVoid(worldCellX, worldCellZ, ShoreExclusionCells, stagesByChunk))
                    {
                        return false;
                    }

                    if (IsNearHut(point.x, point.y, hutCenters, SettlementCellQuery.DefaultTreeHutClearance))
                    {
                        return false;
                    }

                    if (WaterSourceLayout.BlocksTrunk(point.x, point.y, WaterSourceLayout.TrunkRadius(0.9f)))
                    {
                        return false;
                    }

                    return true;
                }

                WorldScatterField.Generate(
                    bounds,
                    worldSeed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    DensityAt,
                    IsValid,
                    blockers,
                    points);
                total += points.Count;
            }

            return total;
        }

        private static int ResolveScatterSeed(GameSaveData save)
        {
            if (save.worldGrowth?.chunks == null)
            {
                return unchecked((int)0x11B0A11);
            }

            for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
            {
                var growth = save.worldGrowth.chunks[i];
                if (growth.chunkX != 0 || growth.chunkZ != 0)
                {
                    continue;
                }

                // Mirror IslandMeshVisualizer: home chunk randomSeed from ChunkSaveData list.
                break;
            }

            if (save.chunks != null)
            {
                for (var i = 0; i < save.chunks.Count; i++)
                {
                    var chunk = save.chunks[i];
                    if (chunk.chunkX == 0 && chunk.chunkZ == 0 && chunk.randomSeed != 0)
                    {
                        return chunk.randomSeed;
                    }
                }
            }

            return unchecked((int)0x11B0A11);
        }

        private static void CollectPlantBlockers(GameSaveData save, List<Vector2> blockers)
        {
            void Add(PlantSaveData plant)
            {
                if (plant == null)
                {
                    return;
                }

                blockers.Add(new Vector2(plant.posX, plant.posZ));
            }

            if (save.plants != null)
            {
                for (var i = 0; i < save.plants.Count; i++)
                {
                    Add(save.plants[i]);
                }
            }

            if (save.chunks == null)
            {
                return;
            }

            for (var c = 0; c < save.chunks.Count; c++)
            {
                var chunk = save.chunks[c];
                if (chunk?.plants == null)
                {
                    continue;
                }

                for (var p = 0; p < chunk.plants.Count; p++)
                {
                    Add(chunk.plants[p]);
                }
            }
        }

        private static List<Vector2> CollectHutCenters(GameSaveData save)
        {
            var centers = new List<Vector2>(64);
            if (save.worldGrowth?.chunks == null)
            {
                return centers;
            }

            for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
            {
                var growth = save.worldGrowth.chunks[i];
                if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var cell = 0; cell < settle.Length; cell++)
                {
                    if (settle[cell] != (byte)SettlementStage.Hut)
                    {
                        continue;
                    }

                    var world = GrowthWorldPlacement.CellCenterWorld(
                        growth.chunkX, growth.chunkZ, cell, GrowthWorldPlacement.ChunkSizeMeters);
                    centers.Add(new Vector2(world.x, world.z));
                }
            }

            return centers;
        }

        private static bool IsNearHut(float worldX, float worldZ, List<Vector2> hutCenters, float clearance)
        {
            var clearanceSq = clearance * clearance;
            for (var i = 0; i < hutCenters.Count; i++)
            {
                var d = hutCenters[i] - new Vector2(worldX, worldZ);
                if (d.sqrMagnitude <= clearanceSq)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TrySample(
            float worldX,
            float worldZ,
            Dictionary<long, byte[]> stagesByChunk,
            Dictionary<long, byte[]> biomesByChunk,
            out int worldCellX,
            out int worldCellZ,
            out byte stage,
            out BiomeType biome)
        {
            stage = 0;
            biome = BiomeType.None;
            var cellSize = GrowthWorldPlacement.CellSizeMeters;
            var originShift = GrowthWorldPlacement.ChunkSizeMeters * 0.5f;
            worldCellX = Mathf.FloorToInt((worldX + originShift) / cellSize);
            worldCellZ = Mathf.FloorToInt((worldZ + originShift) / cellSize);
            var chunkX = (int)Math.Floor(worldCellX / (double)ChunkMaskCodec.GridSize);
            var chunkZ = (int)Math.Floor(worldCellZ / (double)ChunkMaskCodec.GridSize);

            var localX = worldCellX - chunkX * ChunkMaskCodec.GridSize;
            var localZ = worldCellZ - chunkZ * ChunkMaskCodec.GridSize;
            var key = Pack(chunkX, chunkZ);
            if (!stagesByChunk.TryGetValue(key, out var stages) ||
                !biomesByChunk.TryGetValue(key, out var biomes))
            {
                return false;
            }

            var index = ChunkMaskCodec.CellIndex(localX, localZ);
            stage = stages[index];
            biome = (BiomeType)biomes[index];
            return true;
        }

        private static bool IsNearVoid(
            int worldCellX,
            int worldCellZ,
            int radius,
            Dictionary<long, byte[]> stagesByChunk)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var wx = worldCellX + dx;
                    var wz = worldCellZ + dz;
                    var chunkX = (int)Math.Floor(wx / (double)ChunkMaskCodec.GridSize);
                    var chunkZ = (int)Math.Floor(wz / (double)ChunkMaskCodec.GridSize);
                    var localX = wx - chunkX * ChunkMaskCodec.GridSize;
                    var localZ = wz - chunkZ * ChunkMaskCodec.GridSize;
                    var key = Pack(chunkX, chunkZ);
                    if (!stagesByChunk.TryGetValue(key, out var stages))
                    {
                        return true;
                    }

                    if (stages[ChunkMaskCodec.CellIndex(localX, localZ)] == (byte)CellStage.Void)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int CountForestVegetation(
            Dictionary<long, byte[]> stagesByChunk,
            Dictionary<long, byte[]> biomesByChunk)
        {
            var count = 0;
            foreach (var pair in stagesByChunk)
            {
                if (!biomesByChunk.TryGetValue(pair.Key, out var biomes))
                {
                    continue;
                }

                var stages = pair.Value;
                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] >= (byte)CellStage.Vegetation && biomes[i] == (byte)BiomeType.Forest)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void TrimToAuthoredPlotPlants(GameSaveData save)
        {
            static bool Keep(PlantSaveData plant)
            {
                if (plant == null || string.IsNullOrEmpty(plant.plotId))
                {
                    return false;
                }

                return plant.plotId.StartsWith("plot_", StringComparison.Ordinal);
            }

            if (save.plants != null)
            {
                save.plants.RemoveAll(p => !Keep(p));
            }

            if (save.chunks == null)
            {
                return;
            }

            for (var i = 0; i < save.chunks.Count; i++)
            {
                var chunk = save.chunks[i];
                if (chunk?.plants == null)
                {
                    continue;
                }

                chunk.plants.RemoveAll(p => !Keep(p));
            }
        }

        private static Dictionary<string, string> SnapshotSettlement(GameSaveData save)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
            {
                var g = save.worldGrowth.chunks[i];
                map[$"{g.chunkX},{g.chunkZ}"] = g.settlementMaskBase64 ?? string.Empty;
            }

            return map;
        }

        private static bool SettlementUnchanged(Dictionary<string, string> before, GameSaveData save)
        {
            var after = SnapshotSettlement(save);
            if (before.Count != after.Count)
            {
                return false;
            }

            foreach (var pair in before)
            {
                if (!after.TryGetValue(pair.Key, out var value) || value != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static long Pack(int chunkX, int chunkZ) => ((long)chunkX << 32) ^ (uint)chunkZ;
    }
}
#endif
