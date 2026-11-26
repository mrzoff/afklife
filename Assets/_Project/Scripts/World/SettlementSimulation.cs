using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    public sealed class SettlementSimulationResult
    {
        public int cellsAdvanced;
        public int clearingsAdded;
        public int pathsAdded;
        public int stonesAdded;
        public int hutsAdded;

        public string ToReportLine()
        {
            if (cellsAdvanced <= 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(4);
            if (pathsAdded > 0)
            {
                parts.Add(pathsAdded == 1 ? "1 тропа" : $"{pathsAdded} троп");
            }

            if (stonesAdded > 0)
            {
                parts.Add(stonesAdded == 1 ? "1 камень" : $"{stonesAdded} камней");
            }

            if (hutsAdded > 0)
            {
                parts.Add(hutsAdded == 1 ? "1 хижина" : $"{hutsAdded} хижин");
            }

            if (clearingsAdded > 0 && parts.Count == 0)
            {
                parts.Add(clearingsAdded == 1 ? "1 расчистка" : $"{clearingsAdded} расчисток");
            }

            if (parts.Count == 0)
            {
                return $"Поселение выросло на {cellsAdvanced} клетке(ах)";
            }

            return "Поселение: " + string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// AFK settlement growth on land cells. Separate from CellStage / plant colonization.
    /// Does not change WorldGrowthConfig expansion pace.
    /// Neighbor queries use world cell coords so roads continue across chunk borders.
    /// </summary>
    public static class SettlementSimulation
    {
        public const int AbsoluteMaxAdvancesPerSession = 14;
        public const int AbsoluteMaxHutsPerSession = 6;
        public const int AbsoluteMaxStones = 12;
        public const int AbsoluteMaxStonesPerSession = 2;
        public const int CellsPerHut = 28;
        /// <summary>Inner passes so Clearing→Path→Hut can chain within one AFK budget.</summary>
        public const int MaxInnerPasses = 4;

        public static string ToStatusLine(GameSaveData save)
        {
            CountStages(save, out var clearings, out var paths, out var stones, out var huts);
            if (clearings + paths + stones + huts <= 0)
            {
                return "поселение: пока пусто";
            }

            return $"поселение: расчистки {clearings} · тропы {paths} · камни {stones} · дома {huts}";
        }

        public static void CountStages(
            GameSaveData save,
            out int clearings,
            out int paths,
            out int stones,
            out int huts)
        {
            clearings = 0;
            paths = 0;
            stones = 0;
            huts = 0;
            if (save?.worldGrowth?.chunks == null)
            {
                return;
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    switch ((SettlementStage)settle[i])
                    {
                        case SettlementStage.Clearing:
                            clearings++;
                            break;
                        case SettlementStage.Path:
                            paths++;
                            break;
                        case SettlementStage.Stone:
                            stones++;
                            break;
                        case SettlementStage.Hut:
                            huts++;
                            break;
                    }
                }
            }
        }

        public static SettlementSimulationResult Apply(
            GameSaveData save,
            long appliedSeconds,
            DeterministicRng rng)
        {
            var result = new SettlementSimulationResult();
            if (save == null || appliedSeconds <= 0 || rng == null)
            {
                return result;
            }

            SaveMigrationService.EnsureAllSettlementMasks(save);
            save.worldGrowth ??= SaveMigrationService.CreateDefaultWorldGrowth(0);
            WaterSourceLayout.ScrubSettlementInPond(save);

            RebalanceExcessStones(save);

            if (save.worldGrowth.lastSimulationUtc > 0 &&
                save.worldGrowth.settlementWatermarkUtc == save.worldGrowth.lastSimulationUtc)
            {
                return result;
            }

            var maxAdvances = ResolveMaxAdvances(appliedSeconds);
            var maxHuts = ResolveMaxHuts(appliedSeconds);
            if (maxAdvances <= 0)
            {
                MarkWatermark(save);
                return result;
            }

            CountStages(save, out _, out var pathCount, out var stoneCount, out var hutCount);
            var stoneCap = SoftStoneCap(pathCount);
            var hutCap = CountSoftHutCap(save);

            var advances = 0;
            var hutsThisSession = 0;
            var stonesThisSession = 0;
            var picksPerChunk = new Dictionary<long, int>();
            var touched = new HashSet<long>();

            for (var pass = 0; pass < MaxInnerPasses && advances < maxAdvances; pass++)
            {
                var candidates = new List<UpgradeCandidate>(128);
                var articulationPoints = FindRoadArticulationPoints(save);
                foreach (var growth in save.worldGrowth.chunks)
                {
                    if (growth == null)
                    {
                        continue;
                    }

                    if (!TryGetLandStages(growth, out var land) ||
                        !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                    {
                        continue;
                    }

                    CollectCandidates(
                        save, growth, land, settle, candidates, stoneCount, stoneCap, articulationPoints);
                }

                if (candidates.Count == 0)
                {
                    break;
                }

                var byChunk = GroupByChunk(candidates);
                var passAdvances = 0;
                var passBudget = Mathf.Max(1, (maxAdvances - advances + MaxInnerPasses - pass - 1) /
                                              (MaxInnerPasses - pass));

                while (advances < maxAdvances &&
                       passAdvances < passBudget &&
                       byChunk.Count > 0)
                {
                    if (!TryPickFairCandidate(byChunk, picksPerChunk, save, rng, out var c))
                    {
                        break;
                    }

                    var cellKey = Pack(c.chunkX, c.chunkZ, c.cellIndex);
                    if (!touched.Add(cellKey))
                    {
                        continue;
                    }

                    if (c.toStage == (byte)SettlementStage.Hut)
                    {
                        if (hutsThisSession >= maxHuts || hutCount >= hutCap)
                        {
                            continue;
                        }
                    }

                    if (c.toStage == (byte)SettlementStage.Stone)
                    {
                        if (stonesThisSession >= AbsoluteMaxStonesPerSession ||
                            stoneCount >= stoneCap)
                        {
                            continue;
                        }
                    }

                    if (!TryApplyUpgrade(save, c))
                    {
                        continue;
                    }

                    advances++;
                    passAdvances++;
                    var chunkKey = PackChunk(c.chunkX, c.chunkZ);
                    picksPerChunk.TryGetValue(chunkKey, out var n);
                    picksPerChunk[chunkKey] = n + 1;

                    switch ((SettlementStage)c.toStage)
                    {
                        case SettlementStage.Clearing:
                            result.clearingsAdded++;
                            break;
                        case SettlementStage.Path:
                            result.pathsAdded++;
                            pathCount++;
                            stoneCap = SoftStoneCap(pathCount);
                            break;
                        case SettlementStage.Stone:
                            result.stonesAdded++;
                            stonesThisSession++;
                            stoneCount++;
                            break;
                        case SettlementStage.Hut:
                            result.hutsAdded++;
                            hutsThisSession++;
                            hutCount++;
                            break;
                    }
                }
            }

            result.cellsAdvanced = advances;
            MarkWatermark(save);
            return result;
        }

        private static Dictionary<long, List<UpgradeCandidate>> GroupByChunk(List<UpgradeCandidate> candidates)
        {
            var map = new Dictionary<long, List<UpgradeCandidate>>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var key = PackChunk(c.chunkX, c.chunkZ);
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<UpgradeCandidate>(16);
                    map[key] = list;
                }

                list.Add(c);
            }

            foreach (var kv in map)
            {
                kv.Value.Sort((a, b) => b.score.CompareTo(a.score));
            }

            return map;
        }

        /// <summary>
        /// Round-robin across chunks: prefer least-picked chunks, then grown over home,
        /// then top-scoring candidates within the chosen chunk.
        /// </summary>
        private static bool TryPickFairCandidate(
            Dictionary<long, List<UpgradeCandidate>> byChunk,
            Dictionary<long, int> picksPerChunk,
            GameSaveData save,
            DeterministicRng rng,
            out UpgradeCandidate chosen)
        {
            chosen = default;
            if (byChunk.Count == 0)
            {
                return false;
            }

            long bestKey = 0;
            var bestRank = int.MaxValue;
            var found = false;

            foreach (var kv in byChunk)
            {
                if (kv.Value.Count == 0)
                {
                    continue;
                }

                picksPerChunk.TryGetValue(kv.Key, out var picks);
                var legacy = IsLegacyChunk(save, kv.Value[0].chunkX, kv.Value[0].chunkZ);
                // Rank: fewer picks first; grown chunks beat home at same pick count.
                var rank = picks * 4 + (legacy ? 1 : 0);
                if (!found || rank < bestRank)
                {
                    found = true;
                    bestRank = rank;
                    bestKey = kv.Key;
                }
            }

            if (!found || !byChunk.TryGetValue(bestKey, out var list) || list.Count == 0)
            {
                return false;
            }

            var window = Mathf.Min(list.Count, 4);
            var pick = (int)(rng.NextUInt() % (uint)window);
            chosen = list[pick];
            list.RemoveAt(pick);
            if (list.Count == 0)
            {
                byChunk.Remove(bestKey);
            }

            return true;
        }

        private static bool IsLegacyChunk(GameSaveData save, int chunkX, int chunkZ)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return false;
            }

            foreach (var g in save.worldGrowth.chunks)
            {
                if (g != null && g.chunkX == chunkX && g.chunkZ == chunkZ)
                {
                    return g.isLegacyBase;
                }
            }

            return false;
        }

        private static int ResolveMaxAdvances(long appliedSeconds)
        {
            var hours = appliedSeconds / 3600.0;
            if (hours >= 7 * 24)
            {
                return AbsoluteMaxAdvancesPerSession;
            }

            if (hours >= 3 * 24)
            {
                return 9;
            }

            if (hours >= 24)
            {
                return 6;
            }

            if (hours >= 8)
            {
                return 4;
            }

            return 2;
        }

        private static int ResolveMaxHuts(long appliedSeconds)
        {
            var hours = appliedSeconds / 3600.0;
            if (hours >= 7 * 24)
            {
                return AbsoluteMaxHutsPerSession;
            }

            if (hours >= 3 * 24)
            {
                return 4;
            }

            if (hours >= 24)
            {
                return 3;
            }

            return hours >= 8 ? 2 : 1;
        }

        private static int SoftStoneCap(int pathCount) =>
            Mathf.Clamp(3 + pathCount / 4, 4, AbsoluteMaxStones);

        private static int CountSoftHutCap(GameSaveData save)
        {
            var land = CountMossLand(save);
            var byArea = Mathf.Max(4, land / CellsPerHut);
            return Mathf.Clamp(byArea, 6, 64);
        }

        private static int CountMossLand(GameSaveData save)
        {
            var land = 0;
            if (save?.worldGrowth?.chunks == null)
            {
                return 0;
            }

            foreach (var g in save.worldGrowth.chunks)
            {
                if (g == null)
                {
                    continue;
                }

                if (g.isLegacyBase)
                {
                    land += 48;
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(g.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] >= (byte)CellStage.Moss)
                    {
                        land++;
                    }
                }
            }

            return land;
        }

        public static void RebalanceExcessStones(GameSaveData save)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return;
            }

            CountStages(save, out _, out var paths, out var stones, out _);
            var cap = SoftStoneCap(Mathf.Max(paths, 4));
            if (stones <= cap)
            {
                return;
            }

            var surplus = stones - cap;
            var stoneCells = new List<(ChunkGrowthSaveData growth, int index, int score)>(stones);
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] != (byte)SettlementStage.Stone)
                    {
                        continue;
                    }

                    var score = NeighborSettlementScore(save, growth.chunkX, growth.chunkZ, i);
                    stoneCells.Add((growth, i, score));
                }
            }

            stoneCells.Sort((a, b) => a.score.CompareTo(b.score));
            var demoted = 0;
            var dirty = new HashSet<ChunkGrowthSaveData>();
            var decoded = new Dictionary<ChunkGrowthSaveData, byte[]>();

            for (var i = 0; i < stoneCells.Count && demoted < surplus; i++)
            {
                var (growth, idx, _) = stoneCells[i];
                if (!decoded.TryGetValue(growth, out var settle))
                {
                    if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out settle, out _))
                    {
                        continue;
                    }

                    decoded[growth] = settle;
                }

                if (settle[idx] != (byte)SettlementStage.Stone)
                {
                    continue;
                }

                settle[idx] = (byte)SettlementStage.Path;
                dirty.Add(growth);
                demoted++;
            }

            foreach (var growth in dirty)
            {
                if (decoded.TryGetValue(growth, out var settle))
                {
                    growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);
                }
            }
        }

        private static void CollectCandidates(
            GameSaveData save,
            ChunkGrowthSaveData growth,
            byte[] land,
            byte[] settle,
            List<UpgradeCandidate> list,
            int stoneCount,
            int stoneCap,
            HashSet<long> articulationPoints)
        {
            for (var i = 0; i < settle.Length; i++)
            {
                if (!IsBuildableLand(growth, land, i))
                {
                    continue;
                }

                if (!growth.isLegacyBase &&
                    GrowthWorldPlacement.IsShoreCell(save, growth.chunkX, growth.chunkZ, i, radius: 1))
                {
                    continue;
                }

                if (WaterSourceLayout.BlocksSettlementCell(growth.chunkX, growth.chunkZ, i))
                {
                    continue;
                }

                var cur = settle[i];
                byte next;
                if (cur == (byte)SettlementStage.None)
                {
                    next = (byte)SettlementStage.Clearing;
                }
                else if (cur == (byte)SettlementStage.Clearing)
                {
                    next = (byte)SettlementStage.Path;
                }
                else if (cur == (byte)SettlementStage.Path)
                {
                    // World-aware road continuity (cross-chunk).
                    var alongRoad =
                        HasNeighborAtLeast(save, growth.chunkX, growth.chunkZ, i, SettlementStage.Path) ||
                        HasNeighborAtLeast(save, growth.chunkX, growth.chunkZ, i, SettlementStage.Hut);
                    var grownInland = !growth.isLegacyBase &&
                                      land != null &&
                                      i < land.Length &&
                                      land[i] >= (byte)CellStage.Vegetation;

                    // Preserve cardinal road junctions and bridge/articulation cells:
                    // the visual road planner connects Path/Stone cells with four cardinal
                    // edges, not diagonal neighbours. Removing a junction (degree >= 3)
                    // or cut-vertex splits the road network into disconnected components.
                    ChunkMaskCodec.IndexToLocal(i, out var pathLx, out var pathLz);
                    var pathWorldKey = PackWorldCell(
                        growth.chunkX * ChunkMaskCodec.GridSize + pathLx,
                        growth.chunkZ * ChunkMaskCodec.GridSize + pathLz);
                    var isProtectedRoad =
                        IsRoadJunctionCell(save, growth.chunkX, growth.chunkZ, i) ||
                        (articulationPoints != null
                            ? articulationPoints.Contains(pathWorldKey)
                            : WouldDisconnectRoadNetwork(save, growth.chunkX, growth.chunkZ, i));

                    // Keep junctions and bridges as roads; huts only at non-critical grown road cells.
                    if (!isProtectedRoad && alongRoad && (grownInland ||
                                      HasNeighborAtLeast(save, growth.chunkX, growth.chunkZ, i,
                                          SettlementStage.Hut)))
                    {
                        next = (byte)SettlementStage.Hut;
                    }
                    else if (stoneCount < stoneCap && !alongRoad)
                    {
                        next = (byte)SettlementStage.Stone;
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (cur == (byte)SettlementStage.Stone &&
                         (HasNeighborAtLeast(save, growth.chunkX, growth.chunkZ, i, SettlementStage.Path) ||
                          HasNeighborAtLeast(save, growth.chunkX, growth.chunkZ, i, SettlementStage.Hut)))
                {
                    next = (byte)SettlementStage.Hut;
                }
                else
                {
                    continue;
                }

                if (next == (byte)SettlementStage.Hut)
                {
                    var hutCenter = GrowthWorldPlacement.CellCenterWorld(growth.chunkX, growth.chunkZ, i);
                    if (SettlementCellQuery.IsHutFootprintBlocked(
                            save, hutCenter.x, hutCenter.z, SettlementCellQuery.DefaultHutCenterSpacing))
                    {
                        continue;
                    }
                }

                // A Path cell is part of the continuous road graph, not a decorative
                // isolated mark. Only promote a Clearing when it touches an existing
                // cardinal Path/Stone neighbour (including across chunk boundaries).
                // Revalidated again in TryApplyUpgrade because earlier upgrades can change
                // the topology after this candidate list was collected.
                if (next == (byte)SettlementStage.Path &&
                    !HasCardinalRoadNeighbor(save, growth.chunkX, growth.chunkZ, i))
                {
                    continue;
                }

                var score = NeighborSettlementScore(save, growth.chunkX, growth.chunkZ, i);

                if (next == (byte)SettlementStage.Clearing)
                {
                    score += HasNeighborAtLeast(save, growth.chunkX, growth.chunkZ, i, SettlementStage.Path)
                        ? 10
                        : 2;
                    if (!growth.isLegacyBase && land[i] >= (byte)CellStage.Vegetation)
                    {
                        score += 5;
                    }
                }
                else if (next == (byte)SettlementStage.Path)
                {
                    score += 12;
                }
                else if (next == (byte)SettlementStage.Stone)
                {
                    score -= 8;
                }
                else if (next == (byte)SettlementStage.Hut)
                {
                    score += 9;
                    if (!growth.isLegacyBase)
                    {
                        score += 8;
                        if (land[i] >= (byte)CellStage.Vegetation)
                        {
                            score += 3;
                        }
                    }
                    else
                    {
                        score += CenterBias(i) / 2;
                    }
                }

                if (growth.isLegacyBase && next <= (byte)SettlementStage.Path)
                {
                    score += CenterBias(i) / 3;
                }

                list.Add(new UpgradeCandidate
                {
                    chunkX = growth.chunkX,
                    chunkZ = growth.chunkZ,
                    cellIndex = i,
                    toStage = next,
                    score = score
                });
            }
        }

        private static bool TryApplyUpgrade(GameSaveData save, UpgradeCandidate c)
        {
            ChunkGrowthSaveData growth = null;
            foreach (var g in save.worldGrowth.chunks)
            {
                if (g != null && g.chunkX == c.chunkX && g.chunkZ == c.chunkZ)
                {
                    growth = g;
                    break;
                }
            }

            if (growth == null ||
                !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
            {
                return false;
            }

            if (c.cellIndex < 0 || c.cellIndex >= settle.Length)
            {
                return false;
            }

            if (WaterSourceLayout.BlocksSettlementCell(c.chunkX, c.chunkZ, c.cellIndex))
            {
                return false;
            }

            var cur = settle[c.cellIndex];
            var allowed =
                (cur + 1 == c.toStage) ||
                (cur == (byte)SettlementStage.Path && c.toStage == (byte)SettlementStage.Hut) ||
                (cur == (byte)SettlementStage.Stone && c.toStage == (byte)SettlementStage.Hut);

            if (!allowed)
            {
                return false;
            }

            if (c.toStage == (byte)SettlementStage.Path &&
                !HasCardinalRoadNeighbor(save, c.chunkX, c.chunkZ, c.cellIndex))
            {
                return false;
            }

            if (c.toStage == (byte)SettlementStage.Hut)
            {
                // Revalidate at apply time because earlier upgrades in this simulation pass
                // may have changed neighbouring road cells or opened a road loop after
                // candidates were collected.
                if (cur == (byte)SettlementStage.Path &&
                    IsProtectedRoadCell(save, c.chunkX, c.chunkZ, c.cellIndex))
                {
                    return false;
                }

                var hutCenter = GrowthWorldPlacement.CellCenterWorld(c.chunkX, c.chunkZ, c.cellIndex);
                if (SettlementCellQuery.IsHutFootprintBlocked(
                        save, hutCenter.x, hutCenter.z, SettlementCellQuery.DefaultHutCenterSpacing))
                {
                    return false;
                }
            }

            settle[c.cellIndex] = c.toStage;
            growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);
            return true;
        }

        private static bool IsBuildableLand(ChunkGrowthSaveData growth, byte[] land, int cellIndex)
        {
            if (growth.isLegacyBase)
            {
                return true;
            }

            return land != null &&
                   cellIndex >= 0 &&
                   cellIndex < land.Length &&
                   land[cellIndex] >= (byte)CellStage.Moss;
        }

        private static bool TryGetLandStages(ChunkGrowthSaveData growth, out byte[] land)
        {
            if (growth.isLegacyBase)
            {
                land = new byte[ChunkMaskCodec.CellCount];
                for (var i = 0; i < land.Length; i++)
                {
                    land[i] = (byte)CellStage.Moss;
                }

                return true;
            }

            return ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out land, out _);
        }

        /// <summary>Settlement stage at world cell; crosses chunk boundaries.</summary>
        public static byte SampleSettlementStage(GameSaveData save, int worldCellX, int worldCellZ)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return (byte)SettlementStage.None;
            }

            var grid = ChunkMaskCodec.GridSize;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            if (lx < 0 || lz < 0 || lx >= grid || lz >= grid)
            {
                return (byte)SettlementStage.None;
            }

            foreach (var g in save.worldGrowth.chunks)
            {
                if (g == null || g.chunkX != chunkX || g.chunkZ != chunkZ)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var settle, out _))
                {
                    return (byte)SettlementStage.None;
                }

                var idx = ChunkMaskCodec.CellIndex(lx, lz);
                if (idx < 0 || idx >= settle.Length)
                {
                    return (byte)SettlementStage.None;
                }

                return settle[idx];
            }

            return (byte)SettlementStage.None;
        }

        private static int NeighborSettlementScore(GameSaveData save, int chunkX, int chunkZ, int cellIndex)
        {
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            var wx = chunkX * ChunkMaskCodec.GridSize + lx;
            var wz = chunkZ * ChunkMaskCodec.GridSize + lz;
            var score = 0;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    var n = SampleSettlementStage(save, wx + dx, wz + dz);
                    if (n > (byte)SettlementStage.None)
                    {
                        score += n;
                    }
                }
            }

            return score;
        }

        private static bool HasNeighborAtLeast(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex,
            SettlementStage min)
        {
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            var wx = chunkX * ChunkMaskCodec.GridSize + lx;
            var wz = chunkZ * ChunkMaskCodec.GridSize + lz;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (SampleSettlementStage(save, wx + dx, wz + dz) >= (byte)min)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Counts cardinal neighbours that participate in the continuous road graph.
        /// Matches SettlementPathTopology.BuildMaskRoadGraph: Path and Stone cells are
        /// road cells; Hut cells connect by a separate door branch and are not mask edges.
        /// World-coordinate sampling keeps the result identical across chunk boundaries.
        /// </summary>
        public static int CountCardinalRoadNeighbors(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= ChunkMaskCodec.CellCount)
            {
                return 0;
            }

            ChunkMaskCodec.IndexToLocal(cellIndex, out var localX, out var localZ);
            var worldX = chunkX * ChunkMaskCodec.GridSize + localX;
            var worldZ = chunkZ * ChunkMaskCodec.GridSize + localZ;
            var count = 0;

            if (IsRoadStage(SampleSettlementStage(save, worldX - 1, worldZ))) count++;
            if (IsRoadStage(SampleSettlementStage(save, worldX + 1, worldZ))) count++;
            if (IsRoadStage(SampleSettlementStage(save, worldX, worldZ - 1))) count++;
            if (IsRoadStage(SampleSettlementStage(save, worldX, worldZ + 1))) count++;
            return count;
        }

        /// <summary>
        /// True when a prospective Path cell touches the existing road graph by one of its
        /// four cardinal sides. Hut cells are deliberately excluded: they are door destinations,
        /// not edges in the Path/Stone mask graph.
        /// </summary>
        public static bool HasCardinalRoadNeighbor(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex) =>
            CountCardinalRoadNeighbors(save, chunkX, chunkZ, cellIndex) > 0;

        /// <summary>True for a branch or crossing cell in the cardinal road graph.</summary>
        public static bool IsRoadJunctionCell(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex) =>
            CountCardinalRoadNeighbors(save, chunkX, chunkZ, cellIndex) >= 3;

        /// <summary>
        /// Computes all articulation points (cut-vertices) of the navigable cardinal road graph
        /// in a single O(V + E) iterative Tarjan DFS pass. Keys are PackWorldCell(worldX, worldZ).
        /// Mathematically equivalent to calling WouldDisconnectRoadNetwork on every road cell,
        /// avoiding O(N * chunks) repeated Base64 mask decoding during CollectCandidates.
        /// </summary>
        public static HashSet<long> FindRoadArticulationPoints(GameSaveData save)
        {
            var articulationPoints = new HashSet<long>();
            var roadCells = BuildNavigableRoadCells(save, excludeKey: long.MinValue, out var cellCoords);
            if (roadCells.Count < 3)
            {
                return articulationPoints;
            }

            var disc = new Dictionary<long, int>(roadCells.Count);
            var low = new Dictionary<long, int>(roadCells.Count);
            var parent = new Dictionary<long, long>(roadCells.Count);
            var rootChildCount = new Dictionary<long, int>();
            var stack = new Stack<TarjanFrame>(Mathf.Min(roadCells.Count, 256));
            var timer = 0;

            for (var r = 0; r < cellCoords.Count; r++)
            {
                var rootCoord = cellCoords[r];
                var rootKey = PackWorldCell(rootCoord.x, rootCoord.y);
                if (disc.ContainsKey(rootKey))
                {
                    continue;
                }

                timer++;
                disc[rootKey] = timer;
                low[rootKey] = timer;
                rootChildCount[rootKey] = 0;
                stack.Push(new TarjanFrame { key = rootKey, wx = rootCoord.x, wz = rootCoord.y, dir = 0 });

                while (stack.Count > 0)
                {
                    var frame = stack.Pop();
                    var u = frame.key;
                    var pushedChild = false;

                    while (frame.dir < 4)
                    {
                        var dir = frame.dir++;
                        var nwx = frame.wx + (dir == 0 ? -1 : dir == 1 ? 1 : 0);
                        var nwz = frame.wz + (dir == 2 ? -1 : dir == 3 ? 1 : 0);
                        var v = PackWorldCell(nwx, nwz);
                        if (!roadCells.Contains(v))
                        {
                            continue;
                        }

                        if (!disc.TryGetValue(v, out var discV))
                        {
                            parent[v] = u;
                            if (!parent.ContainsKey(u))
                            {
                                rootChildCount[u] = rootChildCount[u] + 1;
                            }

                            timer++;
                            disc[v] = timer;
                            low[v] = timer;
                            stack.Push(frame);
                            stack.Push(new TarjanFrame { key = v, wx = nwx, wz = nwz, dir = 0 });
                            pushedChild = true;
                            break;
                        }

                        if (!parent.TryGetValue(u, out var parentU) || v != parentU)
                        {
                            if (discV < low[u])
                            {
                                low[u] = discV;
                            }
                        }
                    }

                    if (pushedChild)
                    {
                        continue;
                    }

                    // Finished vertex u: propagate low[u] to parent[u] or check root child count.
                    if (parent.TryGetValue(u, out var p))
                    {
                        if (low[u] < low[p])
                        {
                            low[p] = low[u];
                        }

                        if (parent.ContainsKey(p) && low[u] >= disc[p])
                        {
                            articulationPoints.Add(p);
                        }
                    }
                    else if (rootChildCount[u] >= 2)
                    {
                        articulationPoints.Add(u);
                    }
                }
            }

            return articulationPoints;
        }

        private static HashSet<long> BuildNavigableRoadCells(
            GameSaveData save,
            long excludeKey,
            out List<Vector2Int> coords)
        {
            var roadCells = new HashSet<long>();
            coords = new List<Vector2Int>(64);
            if (save?.worldGrowth?.chunks == null)
            {
                return roadCells;
            }

            var grid = ChunkMaskCodec.GridSize;
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                byte[] land = null;
                if (!growth.isLegacyBase && !string.IsNullOrEmpty(growth.stageMaskBase64))
                {
                    ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out land, out _);
                }

                var baseX = growth.chunkX * grid;
                var baseZ = growth.chunkZ * grid;
                for (var i = 0; i < settle.Length; i++)
                {
                    if (!IsRoadStage(settle[i]))
                    {
                        continue;
                    }

                    if (land != null && i < land.Length && land[i] < (byte)CellStage.Moss)
                    {
                        continue;
                    }

                    if (growth.isLegacyBase)
                    {
                        if (WaterSourceLayout.BlocksSettlementCell(growth.chunkX, growth.chunkZ, i))
                        {
                            continue;
                        }

                        var center = GrowthWorldPlacement.CellCenterWorld(growth.chunkX, growth.chunkZ, i);
                        if (SettlementCellQuery.IsNearAuthoredStarterHut(center.x, center.z, 0.85f))
                        {
                            continue;
                        }
                    }

                    ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                    var wx = baseX + lx;
                    var wz = baseZ + lz;
                    var key = PackWorldCell(wx, wz);
                    if (key != excludeKey && roadCells.Add(key))
                    {
                        coords.Add(new Vector2Int(wx, wz));
                    }
                }
            }

            return roadCells;
        }

        private struct TarjanFrame
        {
            public long key;
            public int wx;
            public int wz;
            public int dir;
        }

        /// <summary>
        /// True when removing this cell from the cardinal road graph (Path/Stone cells across
        /// all chunks) leaves at least two of its cardinal road neighbours unreachable from
        /// each other (i.e. the cell is an articulation vertex / bridge).
        /// Isolated cells and dead-end spurs (degree &lt;= 1) never disconnect the graph.
        /// </summary>
        public static bool WouldDisconnectRoadNetwork(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex)
        {
            if (save?.worldGrowth?.chunks == null ||
                cellIndex < 0 ||
                cellIndex >= ChunkMaskCodec.CellCount)
            {
                return false;
            }

            // Fast O(1) short-circuit: dead-end (deg 1) or isolated (deg 0) cannot split a component.
            if (CountCardinalRoadNeighbors(save, chunkX, chunkZ, cellIndex) < 2)
            {
                return false;
            }

            ChunkMaskCodec.IndexToLocal(cellIndex, out var localX, out var localZ);
            var targetX = chunkX * ChunkMaskCodec.GridSize + localX;
            var targetZ = chunkZ * ChunkMaskCodec.GridSize + localZ;
            var targetKey = PackWorldCell(targetX, targetZ);

            var roadCells = BuildNavigableRoadCells(save, excludeKey: targetKey, out _);

            var neighbors = new List<Vector2Int>(4);
            var cand0 = new Vector2Int(targetX - 1, targetZ);
            var cand1 = new Vector2Int(targetX + 1, targetZ);
            var cand2 = new Vector2Int(targetX, targetZ - 1);
            var cand3 = new Vector2Int(targetX, targetZ + 1);
            if (roadCells.Contains(PackWorldCell(cand0.x, cand0.y))) neighbors.Add(cand0);
            if (roadCells.Contains(PackWorldCell(cand1.x, cand1.y))) neighbors.Add(cand1);
            if (roadCells.Contains(PackWorldCell(cand2.x, cand2.y))) neighbors.Add(cand2);
            if (roadCells.Contains(PackWorldCell(cand3.x, cand3.y))) neighbors.Add(cand3);

            if (neighbors.Count < 2)
            {
                return false;
            }

            var requiredTargets = new HashSet<long>();
            for (var i = 1; i < neighbors.Count; i++)
            {
                requiredTargets.Add(PackWorldCell(neighbors[i].x, neighbors[i].y));
            }

            var visited = new HashSet<long>();
            var queue = new Queue<Vector2Int>();
            var start = neighbors[0];
            visited.Add(PackWorldCell(start.x, start.y));
            queue.Enqueue(start);

            while (queue.Count > 0 && requiredTargets.Count > 0)
            {
                var cur = queue.Dequeue();
                TryEnqueueRoadNeighbor(cur.x - 1, cur.y, roadCells, visited, requiredTargets, queue);
                TryEnqueueRoadNeighbor(cur.x + 1, cur.y, roadCells, visited, requiredTargets, queue);
                TryEnqueueRoadNeighbor(cur.x, cur.y - 1, roadCells, visited, requiredTargets, queue);
                TryEnqueueRoadNeighbor(cur.x, cur.y + 1, roadCells, visited, requiredTargets, queue);
            }

            return requiredTargets.Count > 0;
        }

        /// <summary>
        /// True when a Path cell must remain road and cannot be promoted to Hut:
        /// either it is a junction (degree >= 3) or removing it would split the road network.
        /// </summary>
        public static bool IsProtectedRoadCell(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex) =>
            IsRoadJunctionCell(save, chunkX, chunkZ, cellIndex) ||
            WouldDisconnectRoadNetwork(save, chunkX, chunkZ, cellIndex);

        private static void TryEnqueueRoadNeighbor(
            int wx,
            int wz,
            HashSet<long> roadCells,
            HashSet<long> visited,
            HashSet<long> requiredTargets,
            Queue<Vector2Int> queue)
        {
            var key = PackWorldCell(wx, wz);
            if (!roadCells.Contains(key) || !visited.Add(key))
            {
                return;
            }

            requiredTargets.Remove(key);
            queue.Enqueue(new Vector2Int(wx, wz));
        }

        private static long PackWorldCell(int worldX, int worldZ) =>
            ((long)worldX << 32) ^ (uint)worldZ;

        private static bool IsRoadStage(byte stage) =>
            stage == (byte)SettlementStage.Path ||
            stage == (byte)SettlementStage.Stone;

        private static int CenterBias(int cellIndex)
        {
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            var dx = lx - 7.5f;
            var dz = lz - 7.5f;
            var dist = Mathf.Sqrt(dx * dx + dz * dz);
            return Mathf.RoundToInt(Mathf.Max(0f, 8f - dist));
        }

        private static void MarkWatermark(GameSaveData save)
        {
            if (save?.worldGrowth == null)
            {
                return;
            }

            save.worldGrowth.settlementWatermarkUtc = save.worldGrowth.lastSimulationUtc;
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && ((a < 0) ^ (b < 0)))
            {
                q--;
            }

            return q;
        }

        private static long Pack(int chunkX, int chunkZ, int cellIndex) =>
            ((long)chunkX << 42) ^ ((long)chunkZ << 21) ^ cellIndex;

        private static long PackChunk(int chunkX, int chunkZ) =>
            ((long)chunkX << 32) ^ (uint)chunkZ;

        private struct UpgradeCandidate
        {
            public int chunkX;
            public int chunkZ;
            public int cellIndex;
            public byte toStage;
            public int score;
        }
    }
}
