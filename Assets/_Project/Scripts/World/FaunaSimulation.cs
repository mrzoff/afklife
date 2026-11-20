using System;
using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    public static class FaunaIds
    {
        public const string Butterfly = "butterfly";
        public const string Bird = "bird";
        public const string Frog = "frog";
    }

    public sealed class FaunaSimulationResult
    {
        public readonly List<AnimalSaveData> spawned = new();
        public int butterflies;
        public int birds;
        public int frogs;
        public int habitatsCreated;
        public int migrated;
        public bool hitSessionCap;
        public bool hitIslandCap;
        public int maxAnimalsCap;
        public int maxNewPerSessionCap;

        public int TotalSpawned => spawned.Count;

        public string SaturationLine()
        {
            if (!hitSessionCap && !hitIslandCap)
            {
                return string.Empty;
            }

            if (hitIslandCap)
            {
                return "Остров насыщен животными — часть популяций догонится, когда остров вырастет.";
            }

            return "За эту сессию появилось много животных — остальное догонится при следующем визите.";
        }

        public string ToReportLine()
        {
            if (TotalSpawned <= 0 && habitatsCreated <= 0)
            {
                return SaturationLine();
            }

            var parts = new List<string>();
            if (butterflies > 0)
            {
                parts.Add($"{butterflies} бабочк{(butterflies == 1 ? "а" : "и")}");
            }

            if (birds > 0)
            {
                parts.Add($"{birds} птиц{(birds == 1 ? "а" : "ы")}");
            }

            if (frogs > 0)
            {
                parts.Add($"{frogs} лягушк{(frogs == 1 ? "а" : "и")}");
            }

            var line = parts.Count > 0 ? "Появились: " + string.Join(", ", parts) : string.Empty;
            if (habitatsCreated > 0)
            {
                line = string.IsNullOrEmpty(line)
                    ? $"+{habitatsCreated} мест обитания"
                    : line + $"; +{habitatsCreated} мест обитания";
            }

            var sat = SaturationLine();
            if (!string.IsNullOrEmpty(sat))
            {
                line = string.IsNullOrEmpty(line) ? sat : line + ". " + sat;
            }

            return line;
        }
    }

    /// <summary>
    /// Offline fauna as habitats + populations (migrate / breed / colonize). Hourly buckets.
    /// </summary>
    public static class FaunaSimulation
    {
        public const int BaseMaxAnimals = 24;
        public const int AbsoluteMaxAnimals = 48;
        public const int MaxPerHabitat = 4;
        public const int BaseMaxNewPerSession = 10;
        public const int AbsoluteMaxNewPerSession = 20;

        // Legacy aliases for callers/tests that still reference fixed caps.
        public const int MaxAnimals = AbsoluteMaxAnimals;
        public const int MaxNewPerSession = AbsoluteMaxNewPerSession;

        public static int ResolveMaxAnimals(GameSaveData save)
        {
            var unlocked = CountUnlockedChunks(save);
            return Mathf.Min(AbsoluteMaxAnimals, 16 + unlocked * 2);
        }

        public static int ResolveMaxNewPerSession(long appliedSeconds)
        {
            var hours = appliedSeconds / 3600.0;
            if (hours >= 7 * 24)
            {
                return AbsoluteMaxNewPerSession;
            }

            if (hours >= 3 * 24)
            {
                return 18;
            }

            if (hours >= 24)
            {
                return 14;
            }

            if (hours >= 8)
            {
                return 12;
            }

            return BaseMaxNewPerSession;
        }

        public static FaunaSimulationResult Simulate(
            GameSaveData save,
            long appliedSeconds,
            DeterministicRng rng,
            WeatherSimulation.WeatherWindowStats weatherStats = null)
        {
            var result = new FaunaSimulationResult();
            if (save == null || appliedSeconds <= 0 || rng == null)
            {
                return result;
            }

            SaveMigrationService.EnsureEcosystemWeatherAnimals(save);
            save.animals ??= new List<AnimalSaveData>();
            save.habitats ??= new List<HabitatSaveData>();

            var maxAnimals = ResolveMaxAnimals(save);
            var maxNew = ResolveMaxNewPerSession(appliedSeconds);
            result.maxAnimalsCap = maxAnimals;
            result.maxNewPerSessionCap = maxNew;

            EcosystemSimulation.CountPlants(save, out var adultPlants, out var matureTrees, out _);
            EcosystemSimulation.CountLand(save, out _, out _, out _, out var shoreNearWater);
            var eco = save.ecosystem;
            // CIF-A2: no free fauna cycle for sub-hour AFK.
            var hoursExact = appliedSeconds / 3600.0;
            var hours = Mathf.Max(0, (int)hoursExact);
            var hourDenom = (float)Math.Max(hoursExact, 1e-6);
            var rainShare = weatherStats != null
                ? Mathf.Clamp01(weatherStats.rainHours / hourDenom)
                : 0f;
            var fogShare = weatherStats != null
                ? Mathf.Clamp01(weatherStats.fogHours / hourDenom)
                : 0f;
            var windBoost = weatherStats != null
                ? Mathf.Clamp01(weatherStats.windEvents / hourDenom) * 0.12
                : 0d;
            var raining = rainShare > 0.08f || WeatherSimulation.IsRaining(save.weather);
            var foggy = fogShare > 0.08f || WeatherSimulation.IsFoggy(save.weather);
            var daytime = WeatherSimulation.IsDaytime(save.weather);

            // Diminishing returns after 7d: fewer colonization rolls, more land/events elsewhere.
            var buckets = hours <= 0
                ? 0
                : hours <= 168
                    ? Mathf.Clamp(hours, 1, 48)
                    : 48 + Mathf.Min(24, (hours - 168) / 12);

            var spawnedUtc = save.worldGrowth != null
                ? save.worldGrowth.lastSimulationUtc
                : save.lastSaveUtc;

            for (var b = 0; b < buckets; b++)
            {
                if (result.TotalSpawned >= maxNew || save.animals.Count >= maxAnimals)
                {
                    MarkCaps(result, save.animals.Count, maxAnimals, maxNew);
                    break;
                }

                // Colonize / reinforce — weather shares raise chances, never reduce below base.
                TryColonize(
                    save, result, rng, FaunaIds.Butterfly, maxAnimals, maxNew,
                    adultPlants >= 2 && (daytime || foggy || fogShare > 0.05f),
                    eco.biodiversity >= 0.12f ? 0.4 : 0.18,
                    fogShare * 0.18 + (foggy ? 0.05 : 0),
                    spawnedUtc);

                TryColonize(
                    save, result, rng, FaunaIds.Bird, maxAnimals, maxNew,
                    matureTrees >= 1 && eco.biodiversity >= 0.2f && daytime,
                    0.32 + windBoost,
                    0,
                    spawnedUtc);

                TryColonize(
                    save, result, rng, FaunaIds.Frog, maxAnimals, maxNew,
                    eco.humidity >= 0.35f && shoreNearWater && (raining || eco.humidity >= 0.5f || rainShare > 0.1f),
                    0.22 + rainShare * 0.28,
                    raining ? 0.08 : 0,
                    spawnedUtc);

                TryBreed(save, result, rng, spawnedUtc, maxAnimals, maxNew);
                TryMigrate(save, result, rng, rainShare, fogShare);
            }

            MarkCaps(result, save.animals.Count, maxAnimals, maxNew);
            SyncHabitatCounts(save);
            EnsureAnimalsOnLand(save, rng);
            return result;
        }

        private static void MarkCaps(
            FaunaSimulationResult result,
            int animalCount,
            int maxAnimals,
            int maxNew)
        {
            if (animalCount >= maxAnimals)
            {
                result.hitIslandCap = true;
            }

            if (result.TotalSpawned >= maxNew)
            {
                result.hitSessionCap = true;
            }
        }

        private static int CountUnlockedChunks(GameSaveData save)
        {
            if (save?.chunks == null)
            {
                return 1;
            }

            var n = 0;
            foreach (var c in save.chunks)
            {
                if (c != null && c.isUnlocked)
                {
                    n++;
                }
            }

            return Mathf.Max(1, n);
        }

        private static void TryColonize(
            GameSaveData save,
            FaunaSimulationResult result,
            DeterministicRng rng,
            string speciesId,
            int maxAnimals,
            int maxNew,
            bool conditionsMet,
            double baseChance,
            double bonusChance,
            long spawnedUtc)
        {
            if (!conditionsMet || save.animals.Count >= maxAnimals || result.TotalSpawned >= maxNew)
            {
                return;
            }

            if (rng.NextDouble() > baseChance + bonusChance)
            {
                return;
            }

            if (!TryPickHabitatChunk(save, rng, speciesId, out var cx, out var cz, out var pos))
            {
                return;
            }

            var habitat = SaveMigrationService.FindHabitat(save.habitats, cx, cz, speciesId);
            var created = false;
            if (habitat == null)
            {
                habitat = new HabitatSaveData
                {
                    chunkX = cx,
                    chunkZ = cz,
                    speciesId = speciesId,
                    biomeAffinity = SaveMigrationService.AffinityForSpecies(speciesId),
                    capacity = MaxPerHabitat,
                    count = 0,
                    populationId = $"p_{speciesId}_{cx}_{cz}_{rng.NextUInt():x4}"
                };
                save.habitats.Add(habitat);
                created = true;
            }

            if (habitat.count >= habitat.capacity)
            {
                return;
            }

            var animal = new AnimalSaveData
            {
                instanceId = $"a_{speciesId}_{rng.NextUInt():x8}",
                animalId = speciesId,
                posX = pos.x,
                posY = pos.y,
                posZ = pos.z,
                state = (byte)AnimalMoveState.Idle,
                spawnedUtc = spawnedUtc,
                habitatChunkX = cx,
                habitatChunkZ = cz,
                generation = 0,
                populationId = habitat.populationId
            };
            save.animals.Add(animal);
            habitat.count++;
            result.spawned.Add(animal);
            if (created)
            {
                result.habitatsCreated++;
            }

            Tally(result, speciesId);
        }

        private static void TryBreed(
            GameSaveData save,
            FaunaSimulationResult result,
            DeterministicRng rng,
            long spawnedUtc,
            int maxAnimals,
            int maxNew)
        {
            if (save.habitats == null || save.animals.Count >= maxAnimals || result.TotalSpawned >= maxNew)
            {
                return;
            }

            foreach (var habitat in save.habitats)
            {
                if (habitat == null || habitat.count < 2 || habitat.count >= habitat.capacity)
                {
                    continue;
                }

                if (rng.NextDouble() > 0.28)
                {
                    continue;
                }

                if (!TryPickPositionInChunk(save, rng, habitat.speciesId, habitat.chunkX, habitat.chunkZ, out var pos))
                {
                    continue;
                }

                var child = new AnimalSaveData
                {
                    instanceId = $"a_{habitat.speciesId}_{rng.NextUInt():x8}",
                    animalId = habitat.speciesId,
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    state = (byte)AnimalMoveState.Idle,
                    spawnedUtc = spawnedUtc,
                    habitatChunkX = habitat.chunkX,
                    habitatChunkZ = habitat.chunkZ,
                    generation = 1,
                    populationId = habitat.populationId
                };
                save.animals.Add(child);
                habitat.count++;
                result.spawned.Add(child);
                Tally(result, habitat.speciesId);
                if (result.TotalSpawned >= maxNew || save.animals.Count >= maxAnimals)
                {
                    return;
                }
            }
        }

        private static void TryMigrate(
            GameSaveData save,
            FaunaSimulationResult result,
            DeterministicRng rng,
            float rainShare,
            float fogShare)
        {
            if (save.animals == null || save.animals.Count == 0 || save.habitats == null || save.habitats.Count < 2)
            {
                return;
            }

            // Weather gently raises migration odds (never punishes dry/sunny AFK).
            var migrateChance = 0.35 + rainShare * 0.12 + fogShare * 0.08;
            if (rng.NextDouble() > migrateChance)
            {
                return;
            }

            var animal = save.animals[(int)(rng.NextUInt() % (uint)save.animals.Count)];
            if (animal == null)
            {
                return;
            }

            HabitatSaveData best = null;
            foreach (var h in save.habitats)
            {
                if (h == null || h.speciesId != animal.animalId || h.count >= h.capacity)
                {
                    continue;
                }

                if (h.chunkX == animal.habitatChunkX && h.chunkZ == animal.habitatChunkZ)
                {
                    continue;
                }

                if (best == null || h.count < best.count)
                {
                    best = h;
                }
            }

            if (best == null)
            {
                return;
            }

            var old = SaveMigrationService.FindHabitat(
                save.habitats, animal.habitatChunkX, animal.habitatChunkZ, animal.animalId);
            if (old != null)
            {
                old.count = Mathf.Max(0, old.count - 1);
            }

            animal.habitatChunkX = best.chunkX;
            animal.habitatChunkZ = best.chunkZ;
            animal.populationId = best.populationId;
            best.count++;
            if (TryPickPositionInChunk(save, rng, animal.animalId, best.chunkX, best.chunkZ, out var pos))
            {
                animal.posX = pos.x;
                animal.posY = pos.y;
                animal.posZ = pos.z;
            }

            result.migrated++;
        }

        private static void SyncHabitatCounts(GameSaveData save)
        {
            if (save.habitats == null)
            {
                return;
            }

            foreach (var h in save.habitats)
            {
                if (h != null)
                {
                    h.count = 0;
                }
            }

            if (save.animals == null)
            {
                return;
            }

            foreach (var a in save.animals)
            {
                if (a == null)
                {
                    continue;
                }

                var h = SaveMigrationService.FindHabitat(
                    save.habitats, a.habitatChunkX, a.habitatChunkZ, a.animalId);
                if (h == null)
                {
                    h = new HabitatSaveData
                    {
                        chunkX = a.habitatChunkX,
                        chunkZ = a.habitatChunkZ,
                        speciesId = a.animalId,
                        biomeAffinity = SaveMigrationService.AffinityForSpecies(a.animalId),
                        capacity = MaxPerHabitat,
                        count = 0,
                        populationId = a.populationId ?? $"p_{a.animalId}_{a.habitatChunkX}_{a.habitatChunkZ}"
                    };
                    save.habitats.Add(h);
                    a.populationId = h.populationId;
                }

                h.count++;
            }

            for (var i = save.habitats.Count - 1; i >= 0; i--)
            {
                if (save.habitats[i] == null || save.habitats[i].count <= 0)
                {
                    save.habitats.RemoveAt(i);
                }
            }
        }

        private static void Tally(FaunaSimulationResult result, string speciesId)
        {
            switch (speciesId)
            {
                case FaunaIds.Butterfly: result.butterflies++; break;
                case FaunaIds.Bird: result.birds++; break;
                case FaunaIds.Frog: result.frogs++; break;
            }
        }

        /// <summary>
        /// Moves animals that were spawned on void/ocean fallbacks onto real land cells.
        /// Safe to call on load; does not change animalId / population counts.
        /// </summary>
        public static int EnsureAnimalsOnLand(GameSaveData save, DeterministicRng rng = null)
        {
            if (save?.animals == null || save.animals.Count == 0)
            {
                return 0;
            }

            rng ??= new DeterministicRng(
                unchecked((uint)((save.worldGrowth?.growthSeed ?? 1) ^ 0xA71D ^ save.animals.Count)));
            var moved = 0;
            foreach (var animal in save.animals)
            {
                if (animal == null || string.IsNullOrEmpty(animal.animalId))
                {
                    continue;
                }

                if (IsNearLandCell(save, animal.posX, animal.posZ, animal.animalId))
                {
                    continue;
                }

                var cx = animal.habitatChunkX;
                var cz = animal.habitatChunkZ;
                if (!TryPickPositionInChunk(save, rng, animal.animalId, cx, cz, out var pos) &&
                    !TryPickHabitatChunk(save, rng, animal.animalId, out cx, out cz, out pos))
                {
                    continue;
                }

                animal.habitatChunkX = cx;
                animal.habitatChunkZ = cz;
                animal.posX = pos.x;
                animal.posY = pos.y;
                animal.posZ = pos.z;
                moved++;
            }

            return moved;
        }

        private static bool IsNearLandCell(
            GameSaveData save,
            float x,
            float z,
            string speciesId)
        {
            const float chunkSize = 16f;
            const float cell = chunkSize / ChunkMaskCodec.GridSize;
            if (save.worldGrowth?.chunks == null)
            {
                return true;
            }

            var minStage = speciesId == FaunaIds.Frog
                ? (byte)CellStage.Soil
                : (byte)CellStage.Moss;
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (growth.isLegacyBase)
                {
                    if (x * x + z * z < 64f)
                    {
                        return true;
                    }

                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] < minStage)
                    {
                        continue;
                    }

                    var lx = i % ChunkMaskCodec.GridSize;
                    var lz = i / ChunkMaskCodec.GridSize;
                    var wx = growth.chunkX * chunkSize + (lx + 0.5f) * cell - chunkSize * 0.5f;
                    var wz = growth.chunkZ * chunkSize + (lz + 0.5f) * cell - chunkSize * 0.5f;
                    var dx = x - wx;
                    var dz = z - wz;
                    if (dx * dx + dz * dz < 2.25f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryPickHabitatChunk(
            GameSaveData save,
            DeterministicRng rng,
            string speciesId,
            out int cx,
            out int cz,
            out Vector3 pos)
        {
            cx = 0;
            cz = 0;
            pos = Vector3.zero;
            var chunks = new List<(int x, int z)>();
            if (save.worldGrowth?.chunks != null)
            {
                foreach (var g in save.worldGrowth.chunks)
                {
                    if (g != null)
                    {
                        chunks.Add((g.chunkX, g.chunkZ));
                    }
                }
            }

            if (chunks.Count == 0)
            {
                chunks.Add((0, 0));
            }

            if (speciesId == FaunaIds.Frog)
            {
                var shore = new List<(int x, int z)>();
                foreach (var c in chunks)
                {
                    if (c.x != 0 || c.z != 0 || chunks.Count == 1)
                    {
                        shore.Add(c);
                    }
                }

                if (shore.Count > 0)
                {
                    chunks = shore;
                }
            }

            // Try several chunks — never accept a water/void fallback in the ocean.
            var attempts = Mathf.Min(chunks.Count, 8);
            for (var a = 0; a < attempts; a++)
            {
                var pick = chunks[(int)(rng.NextUInt() % (uint)chunks.Count)];
                if (TryPickPositionInChunk(save, rng, speciesId, pick.x, pick.z, out pos))
                {
                    cx = pick.x;
                    cz = pick.z;
                    return true;
                }
            }

            // Last resort: home legacy pad (always has land pockets).
            cx = 0;
            cz = 0;
            return TryPickPositionInChunk(save, rng, speciesId, 0, 0, out pos);
        }

        private static bool TryPickPositionInChunk(
            GameSaveData save,
            DeterministicRng rng,
            string speciesId,
            int chunkX,
            int chunkZ,
            out Vector3 pos)
        {
            pos = Vector3.zero;
            var candidates = new List<Vector3>();

            ChunkGrowthSaveData growth = null;
            if (save.worldGrowth?.chunks != null)
            {
                foreach (var g in save.worldGrowth.chunks)
                {
                    if (g != null && g.chunkX == chunkX && g.chunkZ == chunkZ)
                    {
                        growth = g;
                        break;
                    }
                }
            }

            if (growth != null && growth.isLegacyBase)
            {
                TryAddLandCandidate(candidates, new Vector3(-1.2f, 1.9f, 1.0f));
                TryAddLandCandidate(candidates, new Vector3(1.3f, 1.9f, 0.7f));
                TryAddLandCandidate(candidates, new Vector3(0.2f, 1.9f, -1.3f));
                TryAddLandCandidate(candidates, new Vector3(-2.0f, 1.9f, -0.5f));
                TryAddLandCandidate(candidates, new Vector3(2.4f, 1.9f, 1.4f));
            }
            else if (growth != null &&
                     ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
            {
                // Frogs may use Soil+ near growth edge; others need Moss+.
                var minStage = speciesId == FaunaIds.Frog
                    ? (byte)CellStage.Soil
                    : (byte)CellStage.Moss;
                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] < minStage)
                    {
                        continue;
                    }

                    // Stay inland so fauna does not hover over visual water.
                    if (GrowthWorldPlacement.IsShoreCell(save, chunkX, chunkZ, i, radius: 1))
                    {
                        continue;
                    }

                    var cellPos = GrowthWorldPlacement.CellWorldPosition(
                        chunkX, chunkZ, i, stages[i], isLegacyBase: false);
                    TryAddLandCandidate(candidates, cellPos);
                }
            }

            // No land cells in this chunk → fail (do NOT spawn at chunk origin in the ocean).
            if (candidates.Count == 0)
            {
                return false;
            }

            var basePos = candidates[(int)(rng.NextUInt() % (uint)candidates.Count)];
            var boost = speciesId switch
            {
                FaunaIds.Bird => 0.35f,
                FaunaIds.Butterfly => 0.2f,
                _ => 0.05f
            };
            pos = new Vector3(
                basePos.x + (float)(rng.NextDouble() * 0.6 - 0.3),
                basePos.y + boost,
                basePos.z + (float)(rng.NextDouble() * 0.6 - 0.3));
            if (WaterSourceLayout.IsInsideReserveXZ(pos))
            {
                pos.x = basePos.x;
                pos.z = basePos.z;
            }

            return true;
        }

        private static void TryAddLandCandidate(List<Vector3> candidates, Vector3 pos)
        {
            if (WaterSourceLayout.IsInsideReserveXZ(pos))
            {
                return;
            }

            candidates.Add(pos);
        }
    }
}
