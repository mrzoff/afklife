using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Recomputes living-ecosystem metrics from plants + world growth masks.
    /// Pure data; no scene objects.
    /// </summary>
    public static class EcosystemSimulation
    {
        public static void Recalculate(GameSaveData save, bool raining)
        {
            if (save == null)
            {
                return;
            }

            SaveMigrationService.EnsureEcosystemWeatherAnimals(save);
            CountPlants(save, out var adultPlants, out var matureTrees, out var plantKinds);
            CountLand(save, out var landCells, out var mossCells, out var vegCells, out var shoreNearWater);

            var eco = save.ecosystem;
            var humidityBase = landCells <= 0
                ? 0.4f
                : Mathf.Clamp01(0.3f + (mossCells + vegCells) / (float)Mathf.Max(1, landCells) * 0.45f);
            if (shoreNearWater)
            {
                humidityBase = Mathf.Min(1f, humidityBase + 0.12f);
            }

            if (raining)
            {
                humidityBase = Mathf.Min(1f, humidityBase + 0.15f);
            }

            var expanded = save.worldGrowth != null ? save.worldGrowth.totalExpandedArea : 0f;
            var fertility = Mathf.Clamp01(0.25f + expanded / 900f + adultPlants * 0.07f);
            var habitatBonus = save.habitats != null ? save.habitats.Count * 0.03f : 0f;
            var biodiversity = Mathf.Clamp01(
                0.08f +
                adultPlants * 0.1f +
                matureTrees * 0.08f +
                plantKinds * 0.06f +
                vegCells / 900f +
                (save.animals != null ? save.animals.Count * 0.035f : 0f) +
                habitatBonus);
            var balance = Mathf.Clamp01(1f - Mathf.Abs(humidityBase - fertility) * 0.55f);

            eco.humidity = humidityBase;
            eco.fertility = fertility;
            eco.biodiversity = biodiversity;
            eco.balance = balance;
        }

        public static void CountPlants(
            GameSaveData save,
            out int adultPlants,
            out int matureTrees,
            out int distinctKinds)
        {
            var adults = 0;
            var mature = 0;
            var kinds = new HashSet<string>();
            EnumeratePlants(save, plant =>
            {
                if (plant.stage >= 2)
                {
                    adults++;
                }

                if (plant.stage >= 3)
                {
                    mature++;
                }

                if (!string.IsNullOrWhiteSpace(plant.plantId))
                {
                    kinds.Add(plant.plantId);
                }
            });
            adultPlants = adults;
            matureTrees = mature;
            distinctKinds = kinds.Count;
        }

        public static void CountLand(
            GameSaveData save,
            out int landCells,
            out int mossCells,
            out int vegCells,
            out bool shoreNearWater)
        {
            landCells = 0;
            mossCells = 0;
            vegCells = 0;
            shoreNearWater = false;
            if (save?.worldGrowth?.chunks == null)
            {
                // Authored home island always has a pond — treat as water adjacency for MVP.
                shoreNearWater = true;
                return;
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    var stage = stages[i];
                    if (stage == (byte)CellStage.Void)
                    {
                        continue;
                    }

                    landCells++;
                    if (stage == (byte)CellStage.Moss)
                    {
                        mossCells++;
                    }

                    if (stage >= (byte)CellStage.Vegetation)
                    {
                        vegCells++;
                    }

                    if (!shoreNearWater && stage >= (byte)CellStage.Moss)
                    {
                        var x = i % ChunkMaskCodec.GridSize;
                        var z = i / ChunkMaskCodec.GridSize;
                        if (HasVoidNeighbor(stages, x, z))
                        {
                            shoreNearWater = true;
                        }
                    }
                }

                if (growth.isLegacyBase)
                {
                    shoreNearWater = true;
                }
            }
        }

        private static bool HasVoidNeighbor(byte[] stages, int x, int z)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    var nx = x + dx;
                    var nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= ChunkMaskCodec.GridSize || nz >= ChunkMaskCodec.GridSize)
                    {
                        return true;
                    }

                    if (stages[ChunkMaskCodec.CellIndex(nx, nz)] == (byte)CellStage.Void)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void EnumeratePlants(GameSaveData save, System.Action<PlantSaveData> visit)
        {
            if (save?.chunks != null)
            {
                foreach (var chunk in save.chunks)
                {
                    if (chunk?.plants == null)
                    {
                        continue;
                    }

                    foreach (var plant in chunk.plants)
                    {
                        if (plant != null)
                        {
                            visit(plant);
                        }
                    }
                }
            }

            if (save?.plants == null)
            {
                return;
            }

            // Avoid double-counting when root mirror mirrors chunk plants.
            if (save.chunks != null && save.chunks.Count > 0)
            {
                return;
            }

            foreach (var plant in save.plants)
            {
                if (plant != null)
                {
                    visit(plant);
                }
            }
        }
    }
}
