#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 13: residents derived from hut cells, routes across chunk borders,
    /// save version stays 8. Does not change SettlementSimulation or the 14/6/2 budgets.
    /// </summary>
    public static class ResidentSmoke
    {
        private const long WeekSeconds = 7L * 24L * 3600L;

        private const byte None = (byte)SettlementStage.None;
        private const byte Path = (byte)SettlementStage.Path;
        private const byte Stone = (byte)SettlementStage.Stone;
        private const byte Hut = (byte)SettlementStage.Hut;

        [MenuItem("AFK Island/Smoke Residents (Stage 13)")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Resident smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Resident smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            try
            {
                var roster = RunRoster();
                if (roster != 0)
                {
                    return roster;
                }

                var graph = RunGraph();
                if (graph != 0)
                {
                    return graph;
                }

                var director = RunDirector();
                if (director != 0)
                {
                    return director;
                }

                var offline = RunOfflineAndSave();
                if (offline != 0)
                {
                    return offline;
                }

                Debug.Log("[AFK Island] Resident smoke OK.");
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return 1;
            }
        }

        /// <summary>Package A: one resident per hut, stable ids, stand point off the hut cell.</summary>
        private static int RunRoster()
        {
            if (ResidentRoster.ResidentsPerHut != 1)
            {
                Debug.LogError($"ResidentsPerHut is {ResidentRoster.ResidentsPerHut}, expected 1.");
                return 100;
            }

            var empty = World(Growth(0, 0, true, _ => { }));
            if (ResidentRoster.Build(empty).Count != 0 || ResidentRoster.Count(empty) != 0)
            {
                Debug.LogError("An empty settlement mask produced residents.");
                return 101;
            }

            var hutCell = Cell(4, 4);
            var oneHut = World(Growth(0, 0, true, m =>
            {
                m[hutCell] = Hut;
                m[Cell(5, 4)] = Path;
            }));

            var single = ResidentRoster.Build(oneHut);
            if (single.Count != 1)
            {
                Debug.LogError($"One hut gave {single.Count} residents, expected 1.");
                return 102;
            }

            var entry = single[0];
            if (entry.residentId != ResidentRoster.MakeResidentId(0, 0, hutCell, 0))
            {
                Debug.LogError($"Unexpected resident id {entry.residentId}.");
                return 103;
            }

            if (entry.chunkX != 0 || entry.chunkZ != 0 || entry.cellIndex != hutCell || entry.slot != 0)
            {
                Debug.LogError($"Resident {entry.residentId} is bound to the wrong hut cell.");
                return 104;
            }

            if (!SettlementCellQuery.IsHutAtWorldXZ(oneHut, entry.homeWorld.x, entry.homeWorld.z))
            {
                Debug.LogError("homeWorld is not the hut cell centre, so the visual would drift.");
                return 105;
            }

            // The hut is wider than a cell: the stand point must land on the road, not inside the house.
            if (SettlementCellQuery.IsHutAtWorldXZ(oneHut, entry.standWorld.x, entry.standWorld.z))
            {
                Debug.LogError($"Resident {entry.residentId} stands on its own hut cell.");
                return 106;
            }

            if (!SettlementCellQuery.TryWorldToCell(
                    entry.standWorld.x, entry.standWorld.z, out var standChunkX, out var standChunkZ, out var standCell))
            {
                Debug.LogError($"Stand point of {entry.residentId} is off the grid.");
                return 107;
            }

            if (standChunkX != 0 || standChunkZ != 0 || standCell != Cell(5, 4))
            {
                Debug.LogError(
                    $"Resident {entry.residentId} stands on cell ({standChunkX},{standChunkZ},{standCell}) " +
                    $"instead of the road next to the hut.");
                return 108;
            }

            var home = entry.homeWorld;
            if (Mathf.Abs(entry.standWorld.y - (home.y + ResidentRoster.StandHeightOffset)) > 0.001f)
            {
                Debug.LogError($"Stand height offset is not applied on {entry.residentId}.");
                return 109;
            }

            // Two huts, two chunk owners: two residents, ids sorted and unique.
            var twoHuts = World(
                Growth(0, 0, true, m =>
                {
                    m[Cell(4, 4)] = Hut;
                    m[Cell(5, 4)] = Path;
                }),
                Growth(1, 0, false, m =>
                {
                    m[Cell(2, 9)] = Hut;
                    m[Cell(3, 9)] = Path;
                }));

            var pair = ResidentRoster.Build(twoHuts);
            if (pair.Count != 2 || ResidentRoster.Count(twoHuts) != 2)
            {
                Debug.LogError($"Two huts gave {pair.Count} residents, expected 2.");
                return 110;
            }

            if (!IdsAreUnique(pair))
            {
                Debug.LogError("Roster produced duplicate resident ids.");
                return 111;
            }

            if (string.CompareOrdinal(pair[0].residentId, pair[1].residentId) > 0)
            {
                Debug.LogError("Roster is not sorted by id, so the visual order would change between runs.");
                return 112;
            }

            // A hut block has no road at all: the ring fallback must still leave the buildings.
            var block = World(Growth(0, 0, true, m =>
            {
                for (var lz = 3; lz <= 5; lz++)
                {
                    for (var lx = 3; lx <= 5; lx++)
                    {
                        m[Cell(lx, lz)] = Hut;
                    }
                }
            }));

            var blocked = 0;
            var blockRoster = ResidentRoster.Build(block);
            foreach (var resident in blockRoster)
            {
                if (SettlementCellQuery.IsHutAtWorldXZ(block, resident.standWorld.x, resident.standWorld.z))
                {
                    blocked++;
                }
            }

            if (blockRoster.Count != 9 || blocked != 0)
            {
                Debug.LogError($"Hut block gave {blockRoster.Count} residents, {blocked} still stand on a hut.");
                return 113;
            }

            // Same save twice and a deep clone: the roster must not drift.
            var again = ResidentRoster.Build(twoHuts);
            var clone = ResidentRoster.Build(SaveClone.DeepClone(twoHuts));
            if (!SameRoster(pair, again) || !SameRoster(pair, clone))
            {
                Debug.LogError("Roster is not deterministic across rebuilds and save clones.");
                return 114;
            }

            Debug.Log(
                $"[AFK Island] Resident roster: 1 hut -> 1, 2 huts -> 2, empty mask -> 0, " +
                $"{blockRoster.Count} huts in a block all stand off the buildings.");
            return 0;
        }

        /// <summary>Package B: BFS over path/stone cells, cache per pair, refusal without a road.</summary>
        private static int RunGraph()
        {
            // Two huts joined by a road inside one chunk.
            var inside = World(Growth(0, 0, true, m =>
            {
                m[Cell(4, 4)] = Hut;
                m[Cell(5, 4)] = Path;
                m[Cell(6, 4)] = Path;
                m[Cell(7, 4)] = Hut;
            }));

            var insideRoster = ResidentRoster.Build(inside);
            var graph = new ResidentPathGraph();
            graph.Bind(inside);

            var route = new List<Vector3>();
            if (!graph.TryFindPath(At(insideRoster, 0, 0, Cell(4, 4)), At(insideRoster, 0, 0, Cell(7, 4)), route))
            {
                Debug.LogError("No route between two huts joined by a road.");
                return 200;
            }

            if (route.Count < 2)
            {
                Debug.LogError($"Route inside a chunk has {route.Count} points.");
                return 201;
            }

            if (!Near(route[0], At(insideRoster, 0, 0, Cell(4, 4)).standWorld) ||
                !Near(route[route.Count - 1], At(insideRoster, 0, 0, Cell(7, 4)).standWorld))
            {
                Debug.LogError("Route must start and finish on the stand points, not inside the houses.");
                return 202;
            }

            // Cached pair gives the same route on the second call.
            var second = new List<Vector3>();
            if (!graph.TryFindPath(At(insideRoster, 0, 0, Cell(4, 4)), At(insideRoster, 0, 0, Cell(7, 4)), second) ||
                second.Count != route.Count)
            {
                Debug.LogError("Cached route differs from the first search.");
                return 203;
            }

            // A hut is only walkable as the destination: no road at all means no route.
            var noRoad = World(Growth(0, 0, true, m =>
            {
                m[Cell(4, 4)] = Hut;
                m[Cell(9, 9)] = Hut;
            }));

            var noRoadRoster = ResidentRoster.Build(noRoad);
            var dryGraph = new ResidentPathGraph();
            dryGraph.Bind(noRoad);
            var none = new List<Vector3>();
            if (dryGraph.TryFindPath(noRoadRoster[0], noRoadRoster[1], none) || none.Count != 0)
            {
                Debug.LogError("Residents are given a route with no road between the huts.");
                return 204;
            }

            // Same hut cell as both ends is not a trip.
            none.Clear();
            if (dryGraph.TryFindPath(noRoadRoster[0], noRoadRoster[0], none))
            {
                Debug.LogError("A resident got a route to its own hut.");
                return 205;
            }

            // Without a bound save the graph refuses everything.
            var unbound = new ResidentPathGraph();
            unbound.Bind(null);
            if (unbound.TryFindPath(insideRoster[0], insideRoster[1], none))
            {
                Debug.LogError("Unbound graph returned a route.");
                return 206;
            }

            // The road crosses into the neighbour chunk: the route must cross with it.
            var border = World(
                Growth(0, 0, true, m =>
                {
                    m[Cell(14, 8)] = Hut;
                    m[Cell(15, 8)] = Path;
                }),
                Growth(1, 0, false, m =>
                {
                    m[Cell(0, 8)] = Path;
                    m[Cell(1, 8)] = Hut;
                }));

            var borderRoster = ResidentRoster.Build(border);
            var borderGraph = new ResidentPathGraph();
            borderGraph.Bind(border);
            var crossed = new List<Vector3>();
            if (!borderGraph.TryFindPath(At(borderRoster, 0, 0, Cell(14, 8)), At(borderRoster, 1, 0, Cell(1, 8)), crossed))
            {
                Debug.LogError("Route stops at the chunk border although the road continues.");
                return 207;
            }

            var chunks = new HashSet<int>();
            foreach (var point in crossed)
            {
                if (SettlementCellQuery.TryWorldToCell(point.x, point.z, out var cx, out var cz, out _))
                {
                    chunks.Add(cx);
                }
            }

            if (!chunks.Contains(0) || !chunks.Contains(1))
            {
                Debug.LogError($"Route touches chunks [{string.Join(",", chunks)}], expected both 0 and 1.");
                return 208;
            }

            // Stone is walkable too, so a cobbled square joins two roads.
            var stone = World(Growth(0, 0, true, m =>
            {
                m[Cell(4, 4)] = Hut;
                m[Cell(5, 4)] = Path;
                m[Cell(6, 4)] = Stone;
                m[Cell(7, 4)] = Path;
                m[Cell(8, 4)] = Hut;
            }));

            var stoneRoster = ResidentRoster.Build(stone);
            var stoneGraph = new ResidentPathGraph();
            stoneGraph.Bind(stone);
            var borderPoints = crossed.Count;
            var stoneRoute = new List<Vector3>();
            if (!stoneGraph.TryFindPath(At(stoneRoster, 0, 0, Cell(4, 4)), At(stoneRoster, 0, 0, Cell(8, 4)), stoneRoute))
            {
                Debug.LogError("Stone cells are not walkable.");
                return 209;
            }

            // Mask change invalidates the cache: the road is gone, the route must be gone with it.
            // The pair key stays the same hut cells, so a stale cache would still answer from memory.
            var borderChunk = FindGrowth(border, 1, 0);
            var mask = Decode(borderChunk);
            mask[Cell(0, 8)] = None;
            borderChunk.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(mask);
            borderGraph.Bind(border);
            var afterMask = ResidentRoster.Build(border);
            if (borderGraph.TryFindPath(At(afterMask, 0, 0, Cell(14, 8)), At(afterMask, 1, 0, Cell(1, 8)), crossed))
            {
                Debug.LogError("Cached route survived a mask change. Residents would walk on a road that is gone.");
                return 210;
            }

            Debug.Log(
                $"[AFK Island] Resident graph: route inside a chunk {route.Count} points, " +
                $"across the border {borderPoints} points, no road -> no route.");
            return 0;
        }

        /// <summary>Package C: the director rebuilds from the mask without duplicating residents.</summary>
        private static int RunDirector()
        {
            var update = typeof(ResidentInstance).GetMethod(
                "Update", BindingFlagsInstance);
            if (update != null)
            {
                Debug.LogError("ResidentInstance runs its own Update. The spec forbids a per-resident AI loop.");
                return 300;
            }

            var save = World(Growth(0, 0, true, m =>
            {
                m[Cell(4, 4)] = Hut;
                m[Cell(5, 4)] = Path;
                m[Cell(6, 4)] = Path;
                m[Cell(7, 4)] = Hut;
            }));

            var go = new GameObject("ResidentSmokeDirector");
            var director = go.AddComponent<ResidentDirector>();
            try
            {
                var serialized = new SerializedObject(director);
                var tick = serialized.FindProperty("tickSeconds").floatValue;
                var moving = serialized.FindProperty("maxMovingResidents").intValue;
                var speed = serialized.FindProperty("moveSpeed").floatValue;
                if (!Mathf.Approximately(tick, 3f) || moving != 3 || !Mathf.Approximately(speed, 0.7f))
                {
                    Debug.LogError(
                        $"Director load changed: tick={tick} moving={moving} speed={speed}, expected 3 / 3 / 0.7.");
                    return 301;
                }

                director.RebuildFromSave(save);
                var firstCount = director.ResidentCount;
                director.RebuildFromSave(save);
                var secondCount = director.ResidentCount;
                if (firstCount != 2 || secondCount != 2)
                {
                    Debug.LogError(
                        $"RebuildFromSave gave {firstCount} residents and then {secondCount}. Expected 2 both times.");
                    return 302;
                }

                // The tracked list is the director's own truth. Reading it through the field keeps
                // the check independent of edit-mode Destroy, which only removes objects at the
                // end of the frame.
                var tracked = TrackedResidents(director);
                if (tracked.Count != ResidentRoster.Count(save))
                {
                    Debug.LogError(
                        $"The director tracks {tracked.Count} residents while the mask gives " +
                        $"{ResidentRoster.Count(save)}.");
                    return 303;
                }

                var ids = new HashSet<string>();
                foreach (var instance in tracked)
                {
                    // Objects handed to Object.Destroy stay alive until the editor frame ends,
                    // so a destroyed entry here is edit-mode teardown, not a duplicated resident.
                    if (instance == null)
                    {
                        continue;
                    }

                    if (!ids.Add(instance.ResidentId))
                    {
                        Debug.LogError($"Resident {instance.ResidentId} is spawned twice.");
                        return 304;
                    }

                    if (!Near(instance.transform.position, instance.HomeStand))
                    {
                        Debug.LogError($"{instance.ResidentId} does not start on its stand point.");
                        return 305;
                    }
                }

                // A hut that leaves the mask takes its resident with it.
                var chunk = FindGrowth(save, 0, 0);
                var mask = Decode(chunk);
                mask[Cell(7, 4)] = None;
                mask[Cell(6, 4)] = None;
                chunk.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(mask);
                director.RebuildFromSave(save);
                var rest = TrackedResidents(director);
                if (director.ResidentCount != 1 || rest.Count != 1)
                {
                    Debug.LogError(
                        $"After the hut was removed the director keeps {director.ResidentCount} " +
                        $"residents ({rest.Count} tracked).");
                    return 307;
                }

                if (rest[0] == null || rest[0].ResidentId != ResidentRoster.MakeResidentId(0, 0, Cell(4, 4), 0))
                {
                    Debug.LogError($"The surviving resident is wrong after the rebuild: {rest[0]?.ResidentId}.");
                    return 308;
                }

                Debug.Log(
                    $"[AFK Island] Resident director: 2 huts -> {secondCount} residents, " +
                    $"rebuild does not duplicate, hut removed -> {director.ResidentCount}. " +
                    $"tick={tick:0.#} s, moving<={moving}, speed={speed:0.0} m/s.");
                return 0;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>Package D and the save contract: offline growth, version 8, nothing persisted.</summary>
        private static int RunOfflineAndSave()
        {
            if (SaveMigrationService.CurrentVersion != 8)
            {
                Debug.LogError($"Save version is {SaveMigrationService.CurrentVersion}, expected 8.");
                return 400;
            }

            var save = World(Growth(0, 0, true, m =>
            {
                m[Cell(4, 4)] = Hut;
                m[Cell(5, 4)] = Path;
                m[Cell(6, 4)] = Path;
                m[Cell(7, 4)] = Hut;
                m[Cell(9, 2)] = Stone;
            }));

            var before = ResidentRoster.Count(save);
            var result = SettlementSimulation.Apply(save, WeekSeconds, new DeterministicRng(0x513u));
            SettlementSimulation.CountStages(save, out var clearings, out var paths, out var stones, out var huts);
            var after = ResidentRoster.Count(save);

            if (after != huts * ResidentRoster.ResidentsPerHut)
            {
                Debug.LogError($"After a week there are {after} residents and {huts} huts.");
                return 401;
            }

            // Migration on a clone: a resident stage must not push the save out of version 8.
            // MigrateIfNeeded returns the save on success and null when it refuses the data.
            var migrated = SaveMigrationService.MigrateIfNeeded(SaveClone.DeepClone(save));
            if (save.version != 8 || migrated == null || migrated.version != 8)
            {
                Debug.LogError(
                    $"Residents touched the save contract: version={save.version}, " +
                    $"migrated={(migrated == null ? "refused" : migrated.version.ToString())}.");
                return 402;
            }

            // Residents are derived, never stored: the serialized save must not mention them.
            var json = JsonUtility.ToJson(save);
            if (json.Contains("res_") || json.Contains("residentId"))
            {
                Debug.LogError("Residents leaked into the save payload.");
                return 403;
            }

            var clone = SaveClone.DeepClone(save);
            if (!SameRoster(ResidentRoster.Build(save), ResidentRoster.Build(clone)))
            {
                Debug.LogError("Roster differs after a save round trip, so a restart would change the crowd.");
                return 404;
            }

            Debug.Log(
                $"[AFK Island] Residents offline: advances={result.cellsAdvanced} " +
                $"c={clearings} p={paths} s={stones} h={huts}, residents {before} -> {after}, " +
                $"save version stays {save.version} and holds no resident data.");
            return 0;
        }

        private const System.Reflection.BindingFlags BindingFlagsInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        private static List<ResidentInstance> TrackedResidents(ResidentDirector director)
        {
            var field = typeof(ResidentDirector).GetField("_residents", BindingFlagsInstance);
            if (field == null)
            {
                throw new Exception("ResidentDirector no longer keeps a _residents list.");
            }

            return field.GetValue(director) as List<ResidentInstance> ?? new List<ResidentInstance>();
        }

        private static bool Near(Vector3 a, Vector3 b) =>
            Vector3.Distance(a, b) < 0.05f;

        private static bool IdsAreUnique(List<ResidentEntry> entries)
        {
            var seen = new HashSet<string>();
            foreach (var entry in entries)
            {
                if (!seen.Add(entry.residentId))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameRoster(List<ResidentEntry> a, List<ResidentEntry> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (a[i].residentId != b[i].residentId ||
                    !Near(a[i].homeWorld, b[i].homeWorld) ||
                    !Near(a[i].standWorld, b[i].standWorld))
                {
                    return false;
                }
            }

            return true;
        }

        private static ResidentEntry At(List<ResidentEntry> entries, int chunkX, int chunkZ, int cellIndex)
        {
            foreach (var entry in entries)
            {
                if (entry.chunkX == chunkX && entry.chunkZ == chunkZ && entry.cellIndex == cellIndex)
                {
                    return entry;
                }
            }

            Debug.LogError($"No resident derived from hut cell ({chunkX},{chunkZ},{cellIndex}).");
            return null;
        }

        private static int Cell(int localX, int localZ) => ChunkMaskCodec.CellIndex(localX, localZ);

        private static byte[] Decode(ChunkGrowthSaveData growth)
        {
            if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var mask, out var error))
            {
                throw new Exception($"Settlement mask of ({growth.chunkX},{growth.chunkZ}) is broken: {error}");
            }

            return mask;
        }

        private static ChunkGrowthSaveData Growth(int chunkX, int chunkZ, bool legacy, Action<byte[]> fill)
        {
            var growth = SaveMigrationService.CreateGrowthRecord(chunkX, chunkZ, legacy);

            var land = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < land.Length; i++)
            {
                land[i] = (byte)CellStage.Vegetation;
            }

            growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land);

            var settle = new byte[ChunkMaskCodec.CellCount];
            fill?.Invoke(settle);
            growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);
            return growth;
        }

        private static ChunkGrowthSaveData FindGrowth(GameSaveData save, int chunkX, int chunkZ)
        {
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth != null && growth.chunkX == chunkX && growth.chunkZ == chunkZ)
                {
                    return growth;
                }
            }

            throw new Exception($"Growth record ({chunkX},{chunkZ}) is missing from the test save.");
        }

        private static GameSaveData World(params ChunkGrowthSaveData[] growths)
        {
            const long now = 1_700_000_000L;
            var save = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                plants = new List<PlantSaveData>(),
                chunks = new List<ChunkSaveData>
                {
                    new ChunkSaveData
                    {
                        chunkX = 0,
                        chunkZ = 0,
                        isUnlocked = true,
                        biomeId = "starter_island",
                        plants = new List<PlantSaveData>()
                    },
                    new ChunkSaveData
                    {
                        chunkX = 1,
                        chunkZ = 0,
                        isUnlocked = true,
                        biomeId = "grown",
                        plants = new List<PlantSaveData>()
                    }
                },
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now),
                ecosystem = SaveMigrationService.CreateDefaultEcosystem(),
                weather = SaveMigrationService.CreateDefaultWeather(now),
                animals = new List<AnimalSaveData>(),
                habitats = new List<HabitatSaveData>()
            };

            save.worldGrowth.chunks = new List<ChunkGrowthSaveData>();
            save.worldGrowth.lastSimulationUtc = now;
            save.worldGrowth.settlementWatermarkUtc = now;
            foreach (var growth in growths)
            {
                save.worldGrowth.chunks.Add(growth);
            }

            return save;
        }
    }
}
#endif
