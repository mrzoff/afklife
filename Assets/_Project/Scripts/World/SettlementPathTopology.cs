using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Stage C-3B: pure, deterministic visual path topology planner.
    /// Has no MonoBehaviour or GameSaveData dependencies and never mutates simulation state.
    /// Visual layer only — separate from ResidentPathGraph and ResidentRoster.
    /// </summary>
    public static class SettlementPathTopology
    {
        public enum NodeKind : byte
        {
            StarterAnchor = 0,
            CellCenter = 1,
            Junction = 2,
            HutDoor = 3
        }

        public enum EdgeKind : byte
        {
            Starter = 0,
            MaskRoad = 1,
            HutBranch = 2
        }

        public enum HutConnectionStatus : byte
        {
            Connected = 0,
            ConnectedWithVisualDoorCorrection = 1,
            UnconnectedBlocked = 2
        }

        public sealed class TopologyConfig
        {
            public float ChunkSizeMeters = 16f;
            public int GridSize = 16;
            public float SampleStepMeters = 0.12f;
            public float StarterMainHalfWidth = 0.30f;
            public float StarterCrossHalfWidth = 0.25f;
            public float MaskRoadHalfWidth = 0.26f;
            public float BranchHalfWidth = 0.22f;
            public float DoorAnchorOffsetMeters = 0.06f;
            public float JunctionMergeRadiusMeters = 0.28f;
            public float MaxBranchLengthMeters = 5.0f;
            public float MinHutClearanceMeters = 0.18f;
            public float MinDoorAlignmentDot = 0.55f;
            public float WaterClearanceMarginMeters = 0.05f;
            public float StarterAttachDistanceMeters = 1.6f;
            public int MaxCandidatesPerHut = 10;
            public bool AllowVisualDoorCorrection = true;
            public bool AvoidFilledBlockLoops = true;

            public float CellSizeMeters => GridSize > 0 ? ChunkSizeMeters / GridSize : 1f;
            public float WorldGridOrigin => -ChunkSizeMeters * 0.5f;
        }

        public sealed class StarterPolylineInput
        {
            public string Id;
            public List<Vector2> ControlPoints = new List<Vector2>();
            public float HalfWidth = 0.28f;
            public bool SmoothCatmullRom = true;
        }

        public sealed class HutInput
        {
            public string Id;
            public Vector2 Center;
            public float YawDegrees;
            public float Scale = 0.62f;
            public Vector2 FootprintHalfExtents = new Vector2(0.47f, 0.44f);
            public float DoorLocalZ = 0.598f;
            public bool HasExplicitDoor;
            public Vector2 ExplicitDoorWorld;
            public Vector2 ExplicitDoorForward;
            public bool AllowVisualDoorCorrection = true;
        }

        public sealed class CircleObstacle
        {
            public string Id;
            public Vector2 Center;
            public float Radius;
        }

        public sealed class MaskCellInput
        {
            public int WorldCellX;
            public int WorldCellZ;
            public byte Stage;
        }

        public sealed class TopologyInput
        {
            public TopologyConfig Config = new TopologyConfig();
            public List<StarterPolylineInput> StarterPaths = new List<StarterPolylineInput>();
            public List<MaskCellInput> RoadCells = new List<MaskCellInput>();
            public List<HutInput> Huts = new List<HutInput>();
            public List<CircleObstacle> Obstacles = new List<CircleObstacle>();
            public Func<float, float, bool> IsInsideLand;
            public Func<float, float, bool> IsBlockedByWater;
        }

        public sealed class PathNode
        {
            public string Id;
            public NodeKind Kind;
            public Vector2 Position;
            public int ChunkX;
            public int ChunkZ;
            public int Degree;
        }

        public sealed class PathEdge
        {
            public string Id;
            public EdgeKind Kind;
            public string FromNodeId;
            public string ToNodeId;
            public float HalfWidth;
            public List<Vector2> Points = new List<Vector2>();
            public List<Vector2Int> TouchedChunks = new List<Vector2Int>();
            public float Length;
        }

        public sealed class PathJunction
        {
            public string NodeId;
            public Vector2 Position;
            public List<string> EdgeIds = new List<string>();
            public List<Vector2> OutgoingTangents = new List<Vector2>();
        }

        public sealed class HutBranchReport
        {
            public string HutId;
            public HutConnectionStatus Status;
            public float OriginalYawDegrees;
            public float EffectiveYawDegrees;
            public bool UsedVisualDoorCorrection;
            public Vector2 DoorWorld;
            public Vector2 DoorForward;
            public Vector2 Anchor;
            public Vector2 JoinPoint;
            public string JoinedEdgeId;
            public string Variant;
            public float RouteLength;
            public float StartAlignmentDot;
            public float MinHutClearance;
            public List<string> HardViolations = new List<string>();
        }

        public sealed class StarterAuditIssue
        {
            public string StarterId;
            public string Issue;
            public Vector2 SamplePoint;
        }

        public sealed class TopologyPlan
        {
            public List<PathNode> Nodes = new List<PathNode>();
            public List<PathEdge> Edges = new List<PathEdge>();
            public List<PathJunction> Junctions = new List<PathJunction>();
            public List<HutBranchReport> HutReports = new List<HutBranchReport>();
            public List<StarterAuditIssue> StarterIssues = new List<StarterAuditIssue>();
            public int ConnectedComponents;
            public int DuplicateHutsIgnored;
            public int DuplicateCellsIgnored;
            public int RejectedMaskEdges;
            public string TopologyHash;
        }

        private sealed class WorkingHut
        {
            public string Id;
            public Vector2 Center;
            public float YawDegrees;
            public float Scale;
            public Vector2 HalfExtents;
            public float Radius;
            public float DoorLocalZ;
            public bool HasExplicitDoor;
            public Vector2 ExplicitDoorWorld;
            public Vector2 ExplicitDoorForward;
            public bool AllowVisualDoorCorrection;
        }

        private struct CandidateJoin
        {
            public Vector2 Point;
            public PathEdge Edge;
            public int SegmentIndex;
            public float Dist;
        }

        private struct RouteEvaluation
        {
            public bool Valid;
            public List<Vector2> Points;
            public float Length;
            public float StartDot;
            public float MinHutClearance;
            public float SoftCost;
            public string Variant;
            public List<string> HardViolations;
        }

        // ------------------------------------------------------------------ coordinate helpers

        public static Vector2 CellCenterXZ(int worldCellX, int worldCellZ, TopologyConfig cfg = null)
        {
            cfg ??= new TopologyConfig();
            var cell = cfg.CellSizeMeters;
            var origin = cfg.WorldGridOrigin;
            return new Vector2(origin + (worldCellX + 0.5f) * cell, origin + (worldCellZ + 0.5f) * cell);
        }

        public static Vector2Int WorldToChunk(Vector2 worldXZ, TopologyConfig cfg = null)
        {
            cfg ??= new TopologyConfig();
            var size = cfg.ChunkSizeMeters;
            var cx = Mathf.FloorToInt((worldXZ.x + size * 0.5f) / size);
            var cz = Mathf.FloorToInt((worldXZ.y + size * 0.5f) / size);
            return new Vector2Int(cx, cz);
        }

        /// <summary>
        /// Returns all chunk coordinates whose visual path geometry may be affected when a cell changes.
        /// Border cells (lx or lz at 0 or GridSize-1) invalidate adjacent neighbor chunks as well.
        /// </summary>
        public static List<Vector2Int> GetAffectedChunksForCellChange(
            int worldCellX,
            int worldCellZ,
            int gridSize = 16,
            int borderMarginCells = 1)
        {
            var cx = FloorDiv(worldCellX, gridSize);
            var cz = FloorDiv(worldCellZ, gridSize);
            var lx = worldCellX - cx * gridSize;
            var lz = worldCellZ - cz * gridSize;

            var minDx = lx < borderMarginCells ? -1 : 0;
            var maxDx = lx >= gridSize - borderMarginCells ? 1 : 0;
            var minDz = lz < borderMarginCells ? -1 : 0;
            var maxDz = lz >= gridSize - borderMarginCells ? 1 : 0;

            var result = new List<Vector2Int>(4);
            for (var dz = minDz; dz <= maxDz; dz++)
            {
                for (var dx = minDx; dx <= maxDx; dx++)
                {
                    result.Add(new Vector2Int(cx + dx, cz + dz));
                }
            }

            result.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            return result;
        }

        public static Vector2 ForwardFromYaw(float yawDegrees)
        {
            var rad = yawDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }

        public static void ResolveDoorTransform(
            Vector2 hutCenter,
            float yawDegrees,
            float scale,
            float doorLocalZ,
            out Vector2 doorWorld,
            out Vector2 doorForward)
        {
            doorForward = ForwardFromYaw(yawDegrees);
            doorWorld = hutCenter + doorForward * (doorLocalZ * scale);
        }

        /// <summary>
        /// Production starter layout from IslandPrototypeBuilder.CreateStarterSettlement.
        /// </summary>
        public static List<StarterPolylineInput> CreateProductionStarterPaths(TopologyConfig cfg = null)
        {
            cfg ??= new TopologyConfig();
            return new List<StarterPolylineInput>
            {
                new StarterPolylineInput
                {
                    Id = "Starter_Main",
                    HalfWidth = cfg.StarterMainHalfWidth,
                    SmoothCatmullRom = true,
                    ControlPoints = new List<Vector2>
                    {
                        new Vector2(0.15f, -1.50f),
                        new Vector2(0.00f, -0.60f),
                        new Vector2(0.00f, 0.20f),
                        new Vector2(0.00f, 1.30f),
                        new Vector2(0.00f, 2.30f)
                    }
                },
                new StarterPolylineInput
                {
                    Id = "Starter_Cross",
                    HalfWidth = cfg.StarterCrossHalfWidth,
                    SmoothCatmullRom = true,
                    ControlPoints = new List<Vector2>
                    {
                        new Vector2(-0.30f, -0.60f),
                        new Vector2(0.90f, -0.60f),
                        new Vector2(2.10f, -0.60f)
                    }
                }
            };
        }

        /// <summary>
        /// Production starter huts from IslandPrototypeBuilder.CreateStarterSettlement (scale 1.0).
        /// </summary>
        public static List<HutInput> CreateProductionStarterHuts()
        {
            return new List<HutInput>
            {
                new HutInput
                {
                    Id = "Starter_Hut_A",
                    Center = new Vector2(-1.60f, 0.90f),
                    YawDegrees = 12f,
                    Scale = 1.0f,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f,
                    AllowVisualDoorCorrection = true
                },
                new HutInput
                {
                    Id = "Starter_Hut_B",
                    Center = new Vector2(1.80f, -0.20f),
                    YawDegrees = -25f,
                    Scale = 1.0f,
                    FootprintHalfExtents = new Vector2(1.55f * 0.5f, 1.25f * 0.5f),
                    DoorLocalZ = 1.25f * 0.52f,
                    AllowVisualDoorCorrection = true
                }
            };
        }

        // ------------------------------------------------------------------ main entry

        public static TopologyPlan Build(TopologyInput input)
        {
            var plan = new TopologyPlan();
            if (input == null)
            {
                plan.TopologyHash = ComputeHash(plan);
                return plan;
            }

            var cfg = input.Config ?? new TopologyConfig();
            var nodesById = new Dictionary<string, PathNode>(StringComparer.Ordinal);
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var nodeCounter = 0;

            string NextNodeId(string prefix)
            {
                nodeCounter++;
                return $"{prefix}_{nodeCounter:0000}";
            }

            PathNode FindOrCreateNode(Vector2 pos, NodeKind kind, string preferredId = null)
            {
                PathNode closest = null;
                var bestD = cfg.JunctionMergeRadiusMeters;
                for (var i = 0; i < plan.Nodes.Count; i++)
                {
                    var n = plan.Nodes[i];
                    // Never merge a road junction onto another hut's doorstep node.
                    if (kind != NodeKind.HutDoor && n.Kind == NodeKind.HutDoor)
                    {
                        continue;
                    }

                    var d = Vector2.Distance(n.Position, pos);
                    if (d <= bestD)
                    {
                        bestD = d;
                        closest = n;
                    }
                }

                if (closest != null)
                {
                    return closest;
                }

                var id = !string.IsNullOrEmpty(preferredId) && !nodesById.ContainsKey(preferredId)
                    ? preferredId
                    : NextNodeId(kind.ToString());
                var chunk = WorldToChunk(pos, cfg);
                var created = new PathNode
                {
                    Id = id,
                    Kind = kind,
                    Position = pos,
                    ChunkX = chunk.x,
                    ChunkZ = chunk.y
                };
                plan.Nodes.Add(created);
                nodesById[id] = created;
                return created;
            }

            // 1. Canonicalize & deduplicate huts
            var huts = CanonicalizeHuts(input.Huts, out var dupHuts);
            plan.DuplicateHutsIgnored = dupHuts;

            // 2. Canonicalize & deduplicate obstacles
            var obstacles = CanonicalizeObstacles(input.Obstacles);

            // 3. Seed starter paths (sorted ordinally by ID)
            var starters = new List<StarterPolylineInput>();
            if (input.StarterPaths != null)
            {
                var seenStarter = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sp in input.StarterPaths)
                {
                    if (sp == null || string.IsNullOrEmpty(sp.Id) || sp.ControlPoints == null || sp.ControlPoints.Count < 2)
                    {
                        continue;
                    }

                    if (!seenStarter.Add(sp.Id))
                    {
                        continue;
                    }

                    starters.Add(sp);
                }

                starters.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            }

            foreach (var sp in starters)
            {
                var pts = sp.SmoothCatmullRom
                    ? SampleCatmullRom(sp.ControlPoints, cfg.SampleStepMeters)
                    : ResamplePolyline(sp.ControlPoints, cfg.SampleStepMeters);
                if (pts.Count < 2)
                {
                    continue;
                }

                AuditStarterAgainstWaterAndLand(sp.Id, pts, sp.HalfWidth, cfg, input.IsInsideLand, input.IsBlockedByWater, plan.StarterIssues);

                var fromNode = FindOrCreateNode(pts[0], NodeKind.StarterAnchor, $"{sp.Id}_Start");
                var toNode = FindOrCreateNode(pts[pts.Count - 1], NodeKind.StarterAnchor, $"{sp.Id}_End");
                AddEdge(plan, edgeIds, sp.Id, EdgeKind.Starter, fromNode.Id, toNode.Id, sp.HalfWidth, pts, cfg);
            }

            // Split intersecting starter edges so T/cross junctions between starters become explicit nodes
            ResolveStarterCrossings(plan, edgeIds, FindOrCreateNode, cfg);

            // 4. Build mask road edges from canonicalized road cells (world space, chunk-agnostic)
            BuildMaskRoadGraph(
                input.RoadCells,
                cfg,
                input.IsInsideLand,
                input.IsBlockedByWater,
                huts,
                plan,
                edgeIds,
                FindOrCreateNode);

            // 5. Connect each hut deterministically (ordinal by Hut.Id)
            for (var h = 0; h < huts.Count; h++)
            {
                var hut = huts[h];
                var report = RouteHut(hut, huts, obstacles, plan, edgeIds, cfg, input.IsInsideLand, input.IsBlockedByWater, FindOrCreateNode);
                plan.HutReports.Add(report);
            }

            // 6. Recompute node degrees, explicit junctions (>= 3 edges), connected components and hash
            FinalizeTopology(plan, nodesById);
            plan.TopologyHash = ComputeHash(plan);
            return plan;
        }

        // ------------------------------------------------------------------ mask road graph

        private static void BuildMaskRoadGraph(
            List<MaskCellInput> rawCells,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater,
            List<WorkingHut> huts,
            TopologyPlan plan,
            HashSet<string> edgeIds,
            Func<Vector2, NodeKind, string, PathNode> findOrCreateNode)
        {
            if (rawCells == null || rawCells.Count == 0)
            {
                return;
            }

            // Deduplicate cells by (worldX, worldZ)
            var roadSet = new HashSet<long>();
            var cells = new List<Vector2Int>(rawCells.Count);
            var dupCells = 0;

            for (var i = 0; i < rawCells.Count; i++)
            {
                var c = rawCells[i];
                if (c == null)
                {
                    continue;
                }

                // Stage 2 (Path) and 3 (Stone) form road network; Hut connects via door branch.
                if (c.Stage != 2 && c.Stage != 3)
                {
                    continue;
                }

                var key = PackCell(c.WorldCellX, c.WorldCellZ);
                if (!roadSet.Add(key))
                {
                    dupCells++;
                    continue;
                }

                var center = CellCenterXZ(c.WorldCellX, c.WorldCellZ, cfg);
                if (IsPointBlocked(center, cfg.MaskRoadHalfWidth, cfg, isInsideLand, isBlockedByWater))
                {
                    roadSet.Remove(key);
                    plan.RejectedMaskEdges++;
                    continue;
                }

                cells.Add(new Vector2Int(c.WorldCellX, c.WorldCellZ));
            }

            plan.DuplicateCellsIgnored = dupCells;
            if (cells.Count == 0)
            {
                return;
            }

            cells.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

            // Build undirected adjacency across world cells (crosses chunk borders seamlessly)
            var adj = new Dictionary<long, List<Vector2Int>>(cells.Count);
            foreach (var c in cells)
            {
                adj[PackCell(c.x, c.y)] = new List<Vector2Int>(4);
            }

            var cellEdges = new HashSet<(long, long)>();
            foreach (var c in cells)
            {
                TryAddMaskEdge(c, new Vector2Int(c.x + 1, c.y));
                TryAddMaskEdge(c, new Vector2Int(c.x, c.y + 1));
            }

            void TryAddMaskEdge(Vector2Int a, Vector2Int b)
            {
                var ka = PackCell(a.x, a.y);
                var kb = PackCell(b.x, b.y);
                if (!roadSet.Contains(kb))
                {
                    return;
                }

                // Avoid 2x2 filled-block micro-loops by dropping the north edge of a complete 2x2 square
                // when both upper corners already connect downward (preserves connectivity, removes square frame).
                if (cfg.AvoidFilledBlockLoops && a.y == b.y)
                {
                    var southA = roadSet.Contains(PackCell(a.x, a.y - 1));
                    var southB = roadSet.Contains(PackCell(b.x, b.y - 1));
                    if (southA && southB)
                    {
                        return;
                    }
                }

                var pa = CellCenterXZ(a.x, a.y, cfg);
                var pb = CellCenterXZ(b.x, b.y, cfg);
                if (IsSegmentBlocked(pa, pb, cfg.MaskRoadHalfWidth, cfg, isInsideLand, isBlockedByWater, huts, null))
                {
                    plan.RejectedMaskEdges++;
                    return;
                }

                var pair = ka < kb ? (ka, kb) : (kb, ka);
                if (!cellEdges.Add(pair))
                {
                    return;
                }

                adj[ka].Add(b);
                adj[kb].Add(a);
            }

            // Contract chains of degree-2 cells into continuous smoothed polylines between degree != 2 nodes
            var visitedEdges = new HashSet<(long, long)>();

            // First emit chains starting at degree != 2 nodes (endpoints and junctions)
            foreach (var start in cells)
            {
                var ks = PackCell(start.x, start.y);
                var neighbors = adj[ks];
                if (neighbors.Count == 2)
                {
                    continue;
                }

                neighbors.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
                foreach (var next in neighbors)
                {
                    var pair = OrderPair(ks, PackCell(next.x, next.y));
                    if (!visitedEdges.Add(pair))
                    {
                        continue;
                    }

                    var chain = TraceChain(start, next, adj, visitedEdges);
                    EmitMaskChain(chain, cfg, plan, edgeIds, findOrCreateNode);
                }
            }

            // Then emit any remaining pure cycles (all cells degree 2)
            foreach (var start in cells)
            {
                var ks = PackCell(start.x, start.y);
                foreach (var next in adj[ks])
                {
                    var pair = OrderPair(ks, PackCell(next.x, next.y));
                    if (!visitedEdges.Add(pair))
                    {
                        continue;
                    }

                    var chain = TraceChain(start, next, adj, visitedEdges);
                    EmitMaskChain(chain, cfg, plan, edgeIds, findOrCreateNode);
                }
            }

            // Connect mask road endpoints/cells near starter paths so the network forms one graph
            if (plan.Edges.Count > 0)
            {
                ConnectMaskGraphToStarters(plan, edgeIds, cfg, isInsideLand, isBlockedByWater, huts, findOrCreateNode);
            }
        }

        private static List<Vector2Int> TraceChain(
            Vector2Int start,
            Vector2Int next,
            Dictionary<long, List<Vector2Int>> adj,
            HashSet<(long, long)> visitedEdges)
        {
            var chain = new List<Vector2Int> { start, next };
            var prev = start;
            var curr = next;

            while (true)
            {
                var kc = PackCell(curr.x, curr.y);
                var neighbors = adj[kc];
                if (neighbors.Count != 2)
                {
                    break;
                }

                var candidate = neighbors[0] == prev ? neighbors[1] : neighbors[0];
                var pair = OrderPair(kc, PackCell(candidate.x, candidate.y));
                if (!visitedEdges.Add(pair))
                {
                    break;
                }

                chain.Add(candidate);
                prev = curr;
                curr = candidate;
                if (curr == start)
                {
                    break;
                }
            }

            return chain;
        }

        private static void EmitMaskChain(
            List<Vector2Int> chain,
            TopologyConfig cfg,
            TopologyPlan plan,
            HashSet<string> edgeIds,
            Func<Vector2, NodeKind, string, PathNode> findOrCreateNode)
        {
            if (chain.Count < 2)
            {
                return;
            }

            var ctrl = new List<Vector2>(chain.Count);
            for (var i = 0; i < chain.Count; i++)
            {
                ctrl.Add(CellCenterXZ(chain[i].x, chain[i].y, cfg));
            }

            var pts = ctrl.Count >= 3
                ? SampleCatmullRom(ctrl, cfg.SampleStepMeters)
                : ResamplePolyline(ctrl, cfg.SampleStepMeters);

            var a = chain[0];
            var b = chain[chain.Count - 1];
            var fromNode = findOrCreateNode(pts[0], NodeKind.CellCenter, $"Cell_{a.x}_{a.y}");
            var toNode = findOrCreateNode(pts[pts.Count - 1], NodeKind.CellCenter, $"Cell_{b.x}_{b.y}");
            var id = $"MaskRoad_{a.x}_{a.y}_to_{b.x}_{b.y}";
            AddEdge(plan, edgeIds, id, EdgeKind.MaskRoad, fromNode.Id, toNode.Id, cfg.MaskRoadHalfWidth, pts, cfg);
        }

        private static void ConnectMaskGraphToStarters(
            TopologyPlan plan,
            HashSet<string> edgeIds,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater,
            List<WorkingHut> huts,
            Func<Vector2, NodeKind, string, PathNode> findOrCreateNode)
        {
            var starterEdges = new List<PathEdge>();
            var maskNodes = new List<PathNode>();
            for (var i = 0; i < plan.Edges.Count; i++)
            {
                if (plan.Edges[i].Kind == EdgeKind.Starter)
                {
                    starterEdges.Add(plan.Edges[i]);
                }
            }

            if (starterEdges.Count == 0)
            {
                return;
            }

            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                if (plan.Nodes[i].Kind == NodeKind.CellCenter)
                {
                    maskNodes.Add(plan.Nodes[i]);
                }
            }

            foreach (var mn in maskNodes)
            {
                PathEdge bestEdge = null;
                var bestPt = Vector2.zero;
                var bestSeg = -1;
                var bestDist = cfg.StarterAttachDistanceMeters;

                for (var e = 0; e < starterEdges.Count; e++)
                {
                    var se = starterEdges[e];
                    for (var p = 0; p < se.Points.Count; p++)
                    {
                        var d = Vector2.Distance(mn.Position, se.Points[p]);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestEdge = se;
                            bestPt = se.Points[p];
                            bestSeg = p;
                        }
                    }
                }

                if (bestEdge == null || bestDist < 0.05f)
                {
                    continue;
                }

                if (IsSegmentBlocked(mn.Position, bestPt, cfg.MaskRoadHalfWidth, cfg, isInsideLand, isBlockedByWater, huts, null))
                {
                    continue;
                }

                var joinNode = SplitEdgeAtPoint(plan, edgeIds, bestEdge, bestSeg, bestPt, findOrCreateNode, cfg);
                var pts = ResamplePolyline(new List<Vector2> { mn.Position, joinNode.Position }, cfg.SampleStepMeters);
                AddEdge(plan, edgeIds, $"MaskLink_{mn.Id}_to_{joinNode.Id}", EdgeKind.MaskRoad, mn.Id, joinNode.Id, cfg.MaskRoadHalfWidth, pts, cfg);
                // Refresh starter edge list after split
                starterEdges.Clear();
                for (var i = 0; i < plan.Edges.Count; i++)
                {
                    if (plan.Edges[i].Kind == EdgeKind.Starter)
                    {
                        starterEdges.Add(plan.Edges[i]);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ starter crossings

        private static void ResolveStarterCrossings(
            TopologyPlan plan,
            HashSet<string> edgeIds,
            Func<Vector2, NodeKind, string, PathNode> findOrCreateNode,
            TopologyConfig cfg)
        {
            // Find closest crossing between pairs of distinct starter edges (e.g. Starter_Main x Starter_Cross)
            for (var i = 0; i < plan.Edges.Count; i++)
            {
                var ea = plan.Edges[i];
                if (ea.Kind != EdgeKind.Starter) continue;

                for (var j = i + 1; j < plan.Edges.Count; j++)
                {
                    var eb = plan.Edges[j];
                    if (eb.Kind != EdgeKind.Starter) continue;
                    if (ea.FromNodeId == eb.FromNodeId || ea.FromNodeId == eb.ToNodeId ||
                        ea.ToNodeId == eb.FromNodeId || ea.ToNodeId == eb.ToNodeId)
                    {
                        continue;
                    }

                    if (FindClosestPolylinePair(ea.Points, eb.Points, out var ia, out var ib, out var dist) &&
                        dist <= cfg.JunctionMergeRadiusMeters)
                    {
                        var mid = (ea.Points[ia] + eb.Points[ib]) * 0.5f;
                        SplitEdgeAtPoint(plan, edgeIds, ea, ia, mid, findOrCreateNode, cfg);
                        SplitEdgeAtPoint(plan, edgeIds, eb, ib, mid, findOrCreateNode, cfg);
                        return;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ hut routing

        private static HutBranchReport RouteHut(
            WorkingHut hut,
            List<WorkingHut> allHuts,
            List<CircleObstacle> obstacles,
            TopologyPlan plan,
            HashSet<string> edgeIds,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater,
            Func<Vector2, NodeKind, string, PathNode> findOrCreateNode)
        {
            var report = new HutBranchReport
            {
                HutId = hut.Id,
                OriginalYawDegrees = hut.YawDegrees,
                EffectiveYawDegrees = hut.YawDegrees
            };

            Vector2 origDoor;
            Vector2 origFwd;
            if (hut.HasExplicitDoor)
            {
                origDoor = hut.ExplicitDoorWorld;
                origFwd = hut.ExplicitDoorForward.sqrMagnitude > 1e-6f
                    ? hut.ExplicitDoorForward.normalized
                    : ForwardFromYaw(hut.YawDegrees);
            }
            else
            {
                ResolveDoorTransform(hut.Center, hut.YawDegrees, hut.Scale, hut.DoorLocalZ, out origDoor, out origFwd);
            }

            report.DoorWorld = origDoor;
            report.DoorForward = origFwd;
            report.Anchor = origDoor + origFwd * cfg.DoorAnchorOffsetMeters;

            if (plan.Edges.Count == 0)
            {
                report.Status = HutConnectionStatus.UnconnectedBlocked;
                report.HardViolations.Add("no_existing_network");
                return report;
            }

            // Try original door orientation first
            var origFound = TryFindBestRouteForDoor(
                hut, origDoor, origFwd, allHuts, obstacles, plan, cfg, isInsideLand, isBlockedByWater,
                out var bestCand, out var bestEval, out var origFailureReasons);

            // If original door found a clean short route facing the network, use it directly.
            // If it needed a long detour (> 2.0 m) while the door faces away from the join, check visual door correction.
            var origFacesAway = origFound &&
                                Vector2.Dot(origFwd, (bestCand.Point - hut.Center).normalized) < 0.25f;
            var needsVisualAlternative = !origFound ||
                                         (cfg.AllowVisualDoorCorrection && hut.AllowVisualDoorCorrection &&
                                          bestEval.Length > 2.0f &&
                                          (origFacesAway || (bestEval.Variant != null && bestEval.Variant.StartsWith("wrap", StringComparison.Ordinal))));

            if (origFound && !needsVisualAlternative)
            {
                CommitHutBranch(hut, origDoor, origFwd, bestCand, bestEval, plan, edgeIds, cfg, findOrCreateNode, report, usedVisualCorrection: false, hut.YawDegrees);
                return report;
            }

            // Visual-only door correction (never moves or rejects hut in simulation!)
            if (cfg.AllowVisualDoorCorrection && hut.AllowVisualDoorCorrection)
            {
                var yawCandidates = BuildVisualYawCandidates(hut, plan);
                var foundCorrected = false;
                var bestYaw = hut.YawDegrees;
                var bestDoor = origDoor;
                var bestFwd = origFwd;
                CandidateJoin corrCand = default;
                RouteEvaluation corrEval = default;
                corrEval.SoftCost = float.MaxValue;

                for (var y = 0; y < yawCandidates.Count; y++)
                {
                    var testYaw = yawCandidates[y];
                    ResolveDoorTransform(hut.Center, testYaw, hut.Scale, hut.DoorLocalZ, out var testDoor, out var testFwd);
                    if (TryFindBestRouteForDoor(
                            hut, testDoor, testFwd, allHuts, obstacles, plan, cfg, isInsideLand, isBlockedByWater,
                            out var c, out var ev, out _))
                    {
                        var yawDeltaPenalty = Mathf.Abs(Mathf.DeltaAngle(hut.YawDegrees, testYaw)) * 0.002f;
                        var total = ev.SoftCost + yawDeltaPenalty;
                        if (!foundCorrected || total < corrEval.SoftCost)
                        {
                            foundCorrected = true;
                            bestYaw = testYaw;
                            bestDoor = testDoor;
                            bestFwd = testFwd;
                            corrCand = c;
                            corrEval = ev;
                            corrEval.SoftCost = total;
                        }
                    }
                }

                // Adopt visual correction if original failed OR if it eliminates an awkward wrap detour (>= 40% shorter)
                if (foundCorrected && (!origFound || corrEval.Length < bestEval.Length * 0.60f))
                {
                    CommitHutBranch(hut, bestDoor, bestFwd, corrCand, corrEval, plan, edgeIds, cfg, findOrCreateNode, report, usedVisualCorrection: true, bestYaw);
                    return report;
                }
            }

            if (origFound)
            {
                CommitHutBranch(hut, origDoor, origFwd, bestCand, bestEval, plan, edgeIds, cfg, findOrCreateNode, report, usedVisualCorrection: false, hut.YawDegrees);
                return report;
            }

            // Hard constraints failed even with visual correction -> leave unconnected rather than forcing invalid detour
            report.Status = HutConnectionStatus.UnconnectedBlocked;
            report.HardViolations.AddRange(origFailureReasons);
            return report;
        }

        private static List<float> BuildVisualYawCandidates(WorkingHut hut, TopologyPlan plan)
        {
            var yaws = new List<float>(12);
            // Aim at nearest point on the current network first
            Vector2 nearest = Vector2.zero;
            var bestD = float.MaxValue;
            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var pts = plan.Edges[e].Points;
                for (var i = 0; i < pts.Count; i++)
                {
                    var d = Vector2.Distance(hut.Center, pts[i]);
                    if (d < bestD)
                    {
                        bestD = d;
                        nearest = pts[i];
                    }
                }
            }

            if (bestD < float.MaxValue)
            {
                var toNet = nearest - hut.Center;
                if (toNet.sqrMagnitude > 1e-6f)
                {
                    var aimYaw = Mathf.Atan2(toNet.x, toNet.y) * Mathf.Rad2Deg;
                    yaws.Add(aimYaw);
                    yaws.Add(aimYaw + 30f);
                    yaws.Add(aimYaw - 30f);
                    yaws.Add(aimYaw + 60f);
                    yaws.Add(aimYaw - 60f);
                }
            }

            for (var deg = 0f; deg < 360f; deg += 45f)
            {
                yaws.Add(deg);
            }

            return yaws;
        }

        private static bool TryFindBestRouteForDoor(
            WorkingHut hut,
            Vector2 doorWorld,
            Vector2 doorForward,
            List<WorkingHut> allHuts,
            List<CircleObstacle> obstacles,
            TopologyPlan plan,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater,
            out CandidateJoin bestCand,
            out RouteEvaluation bestEval,
            out List<string> failureReasons)
        {
            bestCand = default;
            bestEval = default;
            bestEval.SoftCost = float.MaxValue;
            failureReasons = new List<string>();

            var anchor = doorWorld + doorForward * cfg.DoorAnchorOffsetMeters;
            var candidates = FindCandidates(anchor, plan, cfg.MaxCandidatesPerHut);
            if (candidates.Count == 0)
            {
                failureReasons.Add("no_join_candidates");
                return false;
            }

            var foundValid = false;
            var seenReasons = new HashSet<string>(StringComparer.Ordinal);
            var evalStep = Mathf.Max(cfg.SampleStepMeters * 2.5f, 0.25f);
            List<Vector2> bestCtrl = null;

            for (var c = 0; c < candidates.Count; c++)
            {
                var cand = candidates[c];
                // Quick straight-line lower bound prune
                if (cand.Dist > cfg.MaxBranchLengthMeters)
                {
                    if (seenReasons.Add("max_length_exceeded")) failureReasons.Add("max_length_exceeded");
                    continue;
                }

                for (var variant = 0; variant < 6; variant++)
                {
                    var ctrl = BuildRouteControlPoints(hut, anchor, doorForward, cand.Point, variant);
                    var pts = SampleCatmullRom(ctrl, evalStep);
                    var eval = EvaluateRoute(
                        pts, hut, doorForward, cand, variant, allHuts, obstacles, cfg, isInsideLand, isBlockedByWater);

                    if (eval.Valid)
                    {
                        if (!foundValid || eval.SoftCost < bestEval.SoftCost)
                        {
                            foundValid = true;
                            bestCand = cand;
                            bestEval = eval;
                            bestCtrl = ctrl;
                        }
                    }
                    else
                    {
                        for (var r = 0; r < eval.HardViolations.Count; r++)
                        {
                            if (seenReasons.Add(eval.HardViolations[r]))
                            {
                                failureReasons.Add(eval.HardViolations[r]);
                            }
                        }
                    }
                }
            }

            // Resample only the winning route at fine resolution
            if (foundValid && bestCtrl != null)
            {
                bestEval.Points = SampleCatmullRom(bestCtrl, cfg.SampleStepMeters);
                bestEval.Length = ComputePolylineLength(bestEval.Points);
            }

            failureReasons.Sort(StringComparer.Ordinal);
            return foundValid;
        }

        private static void CommitHutBranch(
            WorkingHut hut,
            Vector2 doorWorld,
            Vector2 doorForward,
            CandidateJoin cand,
            RouteEvaluation eval,
            TopologyPlan plan,
            HashSet<string> edgeIds,
            TopologyConfig cfg,
            Func<Vector2, NodeKind, string, PathNode> findOrCreateNode,
            HutBranchReport report,
            bool usedVisualCorrection,
            float effectiveYaw)
        {
            var joinedEdgeId = cand.Edge.Id;
            var doorNode = findOrCreateNode(eval.Points[0], NodeKind.HutDoor, $"Door_{hut.Id}");
            var joinNode = SplitEdgeAtPoint(plan, edgeIds, cand.Edge, cand.SegmentIndex, cand.Point, findOrCreateNode, cfg);

            var pts = new List<Vector2>(eval.Points);
            pts[0] = doorNode.Position;
            pts[pts.Count - 1] = joinNode.Position;

            AddEdge(plan, edgeIds, $"Branch_{hut.Id}", EdgeKind.HutBranch, doorNode.Id, joinNode.Id, cfg.BranchHalfWidth, pts, cfg);

            report.Status = usedVisualCorrection
                ? HutConnectionStatus.ConnectedWithVisualDoorCorrection
                : HutConnectionStatus.Connected;
            report.UsedVisualDoorCorrection = usedVisualCorrection;
            report.EffectiveYawDegrees = effectiveYaw;
            report.DoorWorld = doorWorld;
            report.DoorForward = doorForward;
            report.Anchor = doorWorld + doorForward * cfg.DoorAnchorOffsetMeters;
            report.JoinPoint = joinNode.Position;
            report.JoinedEdgeId = joinedEdgeId;
            report.Variant = eval.Variant;
            report.RouteLength = eval.Length;
            report.StartAlignmentDot = eval.StartDot;
            report.MinHutClearance = eval.MinHutClearance;
        }

        private static List<CandidateJoin> FindCandidates(Vector2 anchor, TopologyPlan plan, int maxCount)
        {
            var all = new List<CandidateJoin>(plan.Edges.Count * 8);
            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var edge = plan.Edges[e];
                if (edge.Points == null || edge.Points.Count < 2) continue;
                var isHutBranch = edge.Kind == EdgeKind.HutBranch;
                var startPt = edge.Points[0];
                var stride = Mathf.Max(1, edge.Points.Count / 16);
                for (var i = 0; i < edge.Points.Count; i += stride)
                {
                    var p = edge.Points[i];
                    // Avoid joining directly on top of another hut's doorstep
                    if (isHutBranch && Vector2.Distance(p, startPt) < 0.35f)
                    {
                        continue;
                    }

                    all.Add(new CandidateJoin
                    {
                        Point = p,
                        Edge = edge,
                        SegmentIndex = i,
                        Dist = Vector2.Distance(anchor, p)
                    });
                }

                var last = edge.Points.Count - 1;
                if (last % stride != 0)
                {
                    var p = edge.Points[last];
                    if (!isHutBranch || Vector2.Distance(p, startPt) >= 0.35f)
                    {
                        all.Add(new CandidateJoin
                        {
                            Point = p,
                            Edge = edge,
                            SegmentIndex = last,
                            Dist = Vector2.Distance(anchor, p)
                        });
                    }
                }
            }

            all.Sort((a, b) =>
            {
                var d = a.Dist.CompareTo(b.Dist);
                if (d != 0) return d;
                var id = string.CompareOrdinal(a.Edge.Id, b.Edge.Id);
                return id != 0 ? id : a.SegmentIndex.CompareTo(b.SegmentIndex);
            });

            var picked = new List<CandidateJoin>(maxCount);
            for (var i = 0; i < all.Count && picked.Count < maxCount; i++)
            {
                var cand = all[i];
                var tooClose = false;
                for (var j = 0; j < picked.Count; j++)
                {
                    if (Vector2.Distance(picked[j].Point, cand.Point) < 0.35f)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    picked.Add(cand);
                }
            }

            return picked;
        }

        private static List<Vector2> BuildRouteControlPoints(
            WorkingHut hut,
            Vector2 anchor,
            Vector2 forward,
            Vector2 target,
            int variant)
        {
            var d = Vector2.Distance(anchor, target);
            var lead = Mathf.Clamp(d * 0.28f, 0.22f, 0.65f);
            var p1 = anchor + forward * lead;

            if (variant == 0)
            {
                var mid = (p1 + target) * 0.5f;
                return new List<Vector2> { anchor, p1, mid, target };
            }

            if (variant == 1)
            {
                var lead2 = Mathf.Clamp(d * 0.18f, 0.16f, 0.35f);
                return new List<Vector2> { anchor, anchor + forward * lead2, target };
            }

            // Variants 2..5: wrap left/right around hut footprint (normal and wide padding)
            var sideSign = variant % 2 == 0 ? 1f : -1f;
            var pad = variant >= 4 ? 0.90f : 0.45f;
            var perp = new Vector2(-forward.y, forward.x) * sideSign;
            var dirToTarget = (target - hut.Center).sqrMagnitude > 1e-6f
                ? (target - hut.Center).normalized
                : forward;
            var wrap1 = hut.Center + perp * (hut.Radius + pad) + forward * 0.25f;
            var wrap2 = hut.Center + perp * (hut.Radius + pad) + dirToTarget * 0.35f;
            return new List<Vector2> { anchor, p1, wrap1, wrap2, target };
        }

        private static string VariantName(int variant)
        {
            switch (variant)
            {
                case 0: return "bezier-soft";
                case 1: return "bezier-tight";
                case 2: return "wrap-left";
                case 3: return "wrap-right";
                case 4: return "wrap-wide-left";
                default: return "wrap-wide-right";
            }
        }

        private static RouteEvaluation EvaluateRoute(
            List<Vector2> pts,
            WorkingHut selfHut,
            Vector2 doorForward,
            CandidateJoin cand,
            int variant,
            List<WorkingHut> allHuts,
            List<CircleObstacle> obstacles,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater)
        {
            var eval = new RouteEvaluation
            {
                Points = pts,
                Variant = VariantName(variant),
                HardViolations = new List<string>(),
                MinHutClearance = float.MaxValue
            };

            if (pts == null || pts.Count < 2)
            {
                eval.HardViolations.Add("degenerate_route");
                return eval;
            }

            var len = ComputePolylineLength(pts);
            eval.Length = len;

            var startDir = (pts[1] - pts[0]).sqrMagnitude > 1e-8f
                ? (pts[1] - pts[0]).normalized
                : Vector2.zero;
            eval.StartDot = Vector2.Dot(startDir, doorForward);

            // Hard constraint 1: door alignment
            if (eval.StartDot < cfg.MinDoorAlignmentDot)
            {
                eval.HardViolations.Add("door_misaligned");
            }

            // Hard constraint 2: maximum route length
            if (len > cfg.MaxBranchLengthMeters)
            {
                eval.HardViolations.Add("max_length_exceeded");
            }

            var hitWater = false;
            var hitOutOfLand = false;
            var hitHut = false;
            var hitObstacle = false;
            var totalTurn = 0f;
            var s = 0f;

            for (var i = 0; i < pts.Count; i++)
            {
                if (i > 0)
                {
                    s += Vector2.Distance(pts[i - 1], pts[i]);
                }

                var p = pts[i];

                // Hard constraint 3: water (check center + lateral edges of ribbon)
                if (!hitWater && IsPointBlockedByWater(p, cfg.BranchHalfWidth + cfg.WaterClearanceMarginMeters, isBlockedByWater))
                {
                    hitWater = true;
                }

                // Hard constraint 4: land boundary
                if (!hitOutOfLand && isInsideLand != null && !isInsideLand(p.x, p.y))
                {
                    hitOutOfLand = true;
                }

                // Hard constraint 5: hut footprints
                for (var h = 0; h < allHuts.Count; h++)
                {
                    var other = allHuts[h];
                    var isSelf = other.Id == selfHut.Id;
                    if (isSelf && s < 0.22f)
                    {
                        continue;
                    }

                    var clear = DistanceToHutFootprint(p, other);
                    if (clear < eval.MinHutClearance)
                    {
                        eval.MinHutClearance = clear;
                    }

                    var required = isSelf ? 0.05f : cfg.MinHutClearanceMeters;
                    if (clear < required)
                    {
                        hitHut = true;
                    }
                }

                // Hard constraint 6: circular obstacles
                if (!hitObstacle && obstacles != null)
                {
                    for (var o = 0; o < obstacles.Count; o++)
                    {
                        var ob = obstacles[o];
                        if (Vector2.Distance(p, ob.Center) < ob.Radius + cfg.BranchHalfWidth * 0.6f)
                        {
                            hitObstacle = true;
                            break;
                        }
                    }
                }

                if (i > 0 && i < pts.Count - 1)
                {
                    var d0 = (pts[i] - pts[i - 1]).normalized;
                    var d1 = (pts[i + 1] - pts[i]).normalized;
                    var dot = Mathf.Clamp(Vector2.Dot(d0, d1), -1f, 1f);
                    totalTurn += Mathf.Acos(dot);
                }
            }

            if (hitWater) eval.HardViolations.Add("water_crossing");
            if (hitOutOfLand) eval.HardViolations.Add("out_of_land");
            if (hitHut) eval.HardViolations.Add("hut_footprint_intersection");
            if (hitObstacle) eval.HardViolations.Add("obstacle_intersection");

            if (eval.MinHutClearance == float.MaxValue)
            {
                eval.MinHutClearance = 99f;
            }

            eval.Valid = eval.HardViolations.Count == 0;
            // Soft cost only distinguishes valid routes: prefer shorter, gentler turns, cleaner door approach
            eval.SoftCost = len + totalTurn * 0.35f + (1f - eval.StartDot) * 1.2f + (variant >= 2 ? 0.25f : 0f);
            return eval;
        }

        // ------------------------------------------------------------------ edge splitting & graph finalization

        private static PathNode SplitEdgeAtPoint(
            TopologyPlan plan,
            HashSet<string> edgeIds,
            PathEdge edge,
            int pointIndex,
            Vector2 splitPos,
            Func<Vector2, NodeKind, string, PathNode> findOrCreateNode,
            TopologyConfig cfg)
        {
            var startPos = edge.Points[0];
            var endPos = edge.Points[edge.Points.Count - 1];
            var fromNode = FindNodeById(plan, edge.FromNodeId);
            var toNode = FindNodeById(plan, edge.ToNodeId);

            if (fromNode != null && fromNode.Kind != NodeKind.HutDoor &&
                Vector2.Distance(splitPos, startPos) <= cfg.JunctionMergeRadiusMeters)
            {
                return fromNode;
            }

            if (toNode != null && toNode.Kind != NodeKind.HutDoor &&
                Vector2.Distance(splitPos, endPos) <= cfg.JunctionMergeRadiusMeters)
            {
                return toNode;
            }

            var splitNode = findOrCreateNode(splitPos, NodeKind.Junction, null);
            var idx = Mathf.Clamp(pointIndex, 1, edge.Points.Count - 2);

            var ptsA = new List<Vector2>(idx + 2);
            for (var i = 0; i <= idx; i++) ptsA.Add(edge.Points[i]);
            if (Vector2.Distance(ptsA[ptsA.Count - 1], splitNode.Position) > 0.01f)
            {
                ptsA.Add(splitNode.Position);
            }

            var ptsB = new List<Vector2>(edge.Points.Count - idx + 1);
            ptsB.Add(splitNode.Position);
            for (var i = idx; i < edge.Points.Count; i++)
            {
                if (Vector2.Distance(ptsB[ptsB.Count - 1], edge.Points[i]) > 0.01f)
                {
                    ptsB.Add(edge.Points[i]);
                }
            }

            var origId = edge.Id;
            var origKind = edge.Kind;
            var origFrom = edge.FromNodeId;
            var origTo = edge.ToNodeId;
            var origWidth = edge.HalfWidth;

            plan.Edges.Remove(edge);
            edgeIds.Remove(origId);

            if (ptsA.Count >= 2)
            {
                AddEdge(plan, edgeIds, $"{origId}_a", origKind, origFrom, splitNode.Id, origWidth, ptsA, cfg);
            }

            if (ptsB.Count >= 2)
            {
                AddEdge(plan, edgeIds, $"{origId}_b", origKind, splitNode.Id, origTo, origWidth, ptsB, cfg);
            }

            return splitNode;
        }

        private static void AddEdge(
            TopologyPlan plan,
            HashSet<string> edgeIds,
            string preferredId,
            EdgeKind kind,
            string fromNodeId,
            string toNodeId,
            float halfWidth,
            List<Vector2> pts,
            TopologyConfig cfg)
        {
            if (pts == null || pts.Count < 2)
            {
                return;
            }

            var len = ComputePolylineLength(pts);
            if (len < 0.02f)
            {
                return;
            }

            var id = preferredId;
            var suffix = 2;
            while (!edgeIds.Add(id))
            {
                id = $"{preferredId}_{suffix}";
                suffix++;
            }

            var edge = new PathEdge
            {
                Id = id,
                Kind = kind,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                HalfWidth = halfWidth,
                Points = pts,
                Length = len,
                TouchedChunks = ComputeTouchedChunks(pts, halfWidth, cfg)
            };
            plan.Edges.Add(edge);
        }

        private static void FinalizeTopology(TopologyPlan plan, Dictionary<string, PathNode> nodesById)
        {
            var incident = new Dictionary<string, List<(PathEdge edge, Vector2 tangent)>>(StringComparer.Ordinal);
            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                plan.Nodes[i].Degree = 0;
                incident[plan.Nodes[i].Id] = new List<(PathEdge, Vector2)>(4);
            }

            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var edge = plan.Edges[e];
                if (edge.Points.Count < 2) continue;

                if (incident.TryGetValue(edge.FromNodeId, out var fromList))
                {
                    var t = (edge.Points[1] - edge.Points[0]).normalized;
                    fromList.Add((edge, t));
                }

                if (incident.TryGetValue(edge.ToNodeId, out var toList))
                {
                    var last = edge.Points.Count - 1;
                    var t = (edge.Points[last - 1] - edge.Points[last]).normalized;
                    toList.Add((edge, t));
                }
            }

            plan.Junctions.Clear();
            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                var node = plan.Nodes[i];
                var list = incident[node.Id];
                node.Degree = list.Count;
                if (node.Degree >= 3)
                {
                    node.Kind = NodeKind.Junction;
                    list.Sort((a, b) =>
                    {
                        var angA = Mathf.Atan2(a.tangent.y, a.tangent.x);
                        var angB = Mathf.Atan2(b.tangent.y, b.tangent.x);
                        return angA.CompareTo(angB);
                    });
                    var j = new PathJunction
                    {
                        NodeId = node.Id,
                        Position = node.Position
                    };
                    for (var k = 0; k < list.Count; k++)
                    {
                        j.EdgeIds.Add(list[k].edge.Id);
                        j.OutgoingTangents.Add(list[k].tangent);
                    }

                    plan.Junctions.Add(j);
                }
            }

            // Connected components over active nodes (degree > 0)
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var components = 0;
            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                var n = plan.Nodes[i];
                if (n.Degree == 0 || !visited.Add(n.Id)) continue;
                components++;
                var q = new Queue<string>();
                q.Enqueue(n.Id);
                while (q.Count > 0)
                {
                    var cur = q.Dequeue();
                    foreach (var (edge, _) in incident[cur])
                    {
                        var next = edge.FromNodeId == cur ? edge.ToNodeId : edge.FromNodeId;
                        if (visited.Add(next))
                        {
                            q.Enqueue(next);
                        }
                    }
                }
            }

            plan.ConnectedComponents = components;
        }

        // ------------------------------------------------------------------ geometry & validation helpers

        private static List<WorkingHut> CanonicalizeHuts(List<HutInput> raw, out int duplicates)
        {
            duplicates = 0;
            var result = new List<WorkingHut>();
            if (raw == null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < raw.Count; i++)
            {
                var h = raw[i];
                if (h == null || string.IsNullOrEmpty(h.Id)) continue;
                if (!seen.Add(h.Id))
                {
                    duplicates++;
                    continue;
                }

                var half = h.FootprintHalfExtents * h.Scale;
                result.Add(new WorkingHut
                {
                    Id = h.Id,
                    Center = h.Center,
                    YawDegrees = h.YawDegrees,
                    Scale = h.Scale,
                    HalfExtents = half,
                    Radius = half.magnitude,
                    DoorLocalZ = h.DoorLocalZ,
                    HasExplicitDoor = h.HasExplicitDoor,
                    ExplicitDoorWorld = h.ExplicitDoorWorld,
                    ExplicitDoorForward = h.ExplicitDoorForward,
                    AllowVisualDoorCorrection = h.AllowVisualDoorCorrection
                });
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return result;
        }

        private static List<CircleObstacle> CanonicalizeObstacles(List<CircleObstacle> raw)
        {
            var result = new List<CircleObstacle>();
            if (raw == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < raw.Count; i++)
            {
                var o = raw[i];
                if (o == null) continue;
                var key = string.IsNullOrEmpty(o.Id)
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F2}:{1:F2}:{2:F2}", o.Center.x, o.Center.y, o.Radius)
                    : o.Id;
                if (seen.Add(key))
                {
                    result.Add(o);
                }
            }

            return result;
        }

        private static void AuditStarterAgainstWaterAndLand(
            string starterId,
            List<Vector2> pts,
            float halfWidth,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater,
            List<StarterAuditIssue> issues)
        {
            var reportedWater = false;
            var reportedLand = false;
            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (!reportedWater && IsPointBlockedByWater(p, halfWidth, isBlockedByWater))
                {
                    reportedWater = true;
                    issues.Add(new StarterAuditIssue
                    {
                        StarterId = starterId,
                        Issue = "starter_touches_water_constraint",
                        SamplePoint = p
                    });
                }

                if (!reportedLand && isInsideLand != null && !isInsideLand(p.x, p.y))
                {
                    reportedLand = true;
                    issues.Add(new StarterAuditIssue
                    {
                        StarterId = starterId,
                        Issue = "starter_outside_land",
                        SamplePoint = p
                    });
                }
            }
        }

        private static bool IsPointBlocked(
            Vector2 p,
            float halfWidth,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater)
        {
            if (isInsideLand != null && !isInsideLand(p.x, p.y))
            {
                return true;
            }

            return IsPointBlockedByWater(p, halfWidth + cfg.WaterClearanceMarginMeters, isBlockedByWater);
        }

        private static bool IsPointBlockedByWater(Vector2 p, float margin, Func<float, float, bool> isBlockedByWater)
        {
            if (isBlockedByWater == null) return false;
            if (isBlockedByWater(p.x, p.y)) return true;
            if (margin <= 0.001f) return false;
            return isBlockedByWater(p.x + margin, p.y)
                   || isBlockedByWater(p.x - margin, p.y)
                   || isBlockedByWater(p.x, p.y + margin)
                   || isBlockedByWater(p.x, p.y - margin);
        }

        private static bool IsSegmentBlocked(
            Vector2 a,
            Vector2 b,
            float halfWidth,
            TopologyConfig cfg,
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater,
            List<WorkingHut> huts,
            string ignoreHutId)
        {
            var d = Vector2.Distance(a, b);
            var steps = Mathf.Max(2, Mathf.CeilToInt(d / Mathf.Max(0.08f, cfg.SampleStepMeters)));
            for (var i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)steps);
                if (IsPointBlocked(p, halfWidth, cfg, isInsideLand, isBlockedByWater))
                {
                    return true;
                }

                if (huts != null)
                {
                    for (var h = 0; h < huts.Count; h++)
                    {
                        if (huts[h].Id == ignoreHutId) continue;
                        if (DistanceToHutFootprint(p, huts[h]) < 0.02f)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static float DistanceToHutFootprint(Vector2 worldXZ, WorkingHut hut)
        {
            var rel = worldXZ - hut.Center;
            var rad = -hut.YawDegrees * Mathf.Deg2Rad;
            var cos = Mathf.Cos(rad);
            var sin = Mathf.Sin(rad);
            var lx = rel.x * cos - rel.y * sin;
            var lz = rel.x * sin + rel.y * cos;
            var dx = Mathf.Max(0f, Mathf.Abs(lx) - hut.HalfExtents.x);
            var dz = Mathf.Max(0f, Mathf.Abs(lz) - hut.HalfExtents.y);
            if (dx > 0f || dz > 0f)
            {
                return Mathf.Sqrt(dx * dx + dz * dz);
            }

            // Inside box -> negative penetration depth
            return -Mathf.Min(hut.HalfExtents.x - Mathf.Abs(lx), hut.HalfExtents.y - Mathf.Abs(lz));
        }

        private static List<Vector2Int> ComputeTouchedChunks(List<Vector2> pts, float halfWidth, TopologyConfig cfg)
        {
            var set = new HashSet<long>();
            var list = new List<Vector2Int>(2);
            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                AddChunk(WorldToChunk(new Vector2(p.x - halfWidth, p.y - halfWidth), cfg));
                AddChunk(WorldToChunk(new Vector2(p.x + halfWidth, p.y - halfWidth), cfg));
                AddChunk(WorldToChunk(new Vector2(p.x - halfWidth, p.y + halfWidth), cfg));
                AddChunk(WorldToChunk(new Vector2(p.x + halfWidth, p.y + halfWidth), cfg));
            }

            void AddChunk(Vector2Int c)
            {
                if (set.Add(PackCell(c.x, c.y)))
                {
                    list.Add(c);
                }
            }

            list.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            return list;
        }

        private static PathNode FindNodeById(TopologyPlan plan, string id)
        {
            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                if (plan.Nodes[i].Id == id) return plan.Nodes[i];
            }

            return null;
        }

        private static bool FindClosestPolylinePair(
            List<Vector2> a,
            List<Vector2> b,
            out int bestIa,
            out int bestIb,
            out float bestDist)
        {
            bestIa = -1;
            bestIb = -1;
            bestDist = float.MaxValue;
            for (var i = 0; i < a.Count; i++)
            {
                for (var j = 0; j < b.Count; j++)
                {
                    var d = Vector2.Distance(a[i], b[j]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestIa = i;
                        bestIb = j;
                    }
                }
            }

            return bestIa >= 0;
        }

        private static List<Vector2> SampleCatmullRom(List<Vector2> ctrl, float step)
        {
            var raw = new List<Vector2>();
            if (ctrl == null || ctrl.Count == 0) return raw;
            if (ctrl.Count == 1)
            {
                raw.Add(ctrl[0]);
                return raw;
            }

            if (ctrl.Count == 2)
            {
                return ResamplePolyline(ctrl, step);
            }

            for (var i = 0; i < ctrl.Count - 1; i++)
            {
                var p0 = i == 0 ? ctrl[0] : ctrl[i - 1];
                var p1 = ctrl[i];
                var p2 = ctrl[i + 1];
                var p3 = i + 2 < ctrl.Count ? ctrl[i + 2] : ctrl[ctrl.Count - 1];

                var segLen = Vector2.Distance(p1, p2);
                var sub = Mathf.Max(4, Mathf.CeilToInt(segLen / Mathf.Max(0.04f, step)));
                for (var k = 0; k < sub; k++)
                {
                    var t = k / (float)sub;
                    raw.Add(Catmull(p0, p1, p2, p3, t));
                }
            }

            raw.Add(ctrl[ctrl.Count - 1]);
            return ResamplePolyline(raw, step);
        }

        private static Vector2 Catmull(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            var t2 = t * t;
            var t3 = t2 * t;
            return 0.5f * ((2f * p1)
                           + (-p0 + p2) * t
                           + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                           + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private static List<Vector2> ResamplePolyline(List<Vector2> raw, float step)
        {
            var res = new List<Vector2>();
            if (raw == null || raw.Count == 0) return res;
            res.Add(raw[0]);
            var spacing = Mathf.Max(0.04f, step);
            var acc = 0f;

            for (var i = 1; i < raw.Count; i++)
            {
                var a = raw[i - 1];
                var b = raw[i];
                var d = Vector2.Distance(a, b);
                if (d < 1e-5f) continue;

                var cursor = spacing - acc;
                while (cursor <= d)
                {
                    res.Add(Vector2.Lerp(a, b, cursor / d));
                    cursor += spacing;
                }

                acc = d - (cursor - spacing);
            }

            var end = raw[raw.Count - 1];
            if (Vector2.Distance(res[res.Count - 1], end) > 0.01f)
            {
                res.Add(end);
            }

            return res;
        }

        private static float ComputePolylineLength(List<Vector2> pts)
        {
            var sum = 0f;
            for (var i = 1; i < pts.Count; i++)
            {
                sum += Vector2.Distance(pts[i - 1], pts[i]);
            }

            return sum;
        }

        private static string ComputeHash(TopologyPlan plan)
        {
            var sb = new StringBuilder(1024);
            var ci = CultureInfo.InvariantCulture;

            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                var n = plan.Nodes[i];
                sb.AppendFormat(ci, "N|{0}|{1}|{2:F3},{3:F3}|{4}\n", n.Id, (int)n.Kind, n.Position.x, n.Position.y, n.Degree);
            }

            for (var i = 0; i < plan.Edges.Count; i++)
            {
                var e = plan.Edges[i];
                sb.AppendFormat(ci, "E|{0}|{1}|{2}|{3}|{4:F3}|{5}\n", e.Id, (int)e.Kind, e.FromNodeId, e.ToNodeId, e.HalfWidth, e.Points.Count);
                for (var p = 0; p < e.Points.Count; p++)
                {
                    sb.AppendFormat(ci, "P|{0:F3},{1:F3}\n", e.Points[p].x, e.Points[p].y);
                }
            }

            for (var i = 0; i < plan.Junctions.Count; i++)
            {
                var j = plan.Junctions[i];
                sb.AppendFormat(ci, "J|{0}|{1}\n", j.NodeId, string.Join(",", j.EdgeIds));
            }

            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var r = plan.HutReports[i];
                sb.AppendFormat(ci, "H|{0}|{1}|{2:F1}|{3:F2}|{4}\n",
                    r.HutId, (int)r.Status, r.EffectiveYawDegrees, r.RouteLength, string.Join(",", r.HardViolations));
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                {
                    hex.Append(bytes[i].ToString("X2"));
                }

                return hex.ToString();
            }
        }

        private static long PackCell(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static (long, long) OrderPair(long a, long b) => a < b ? (a, b) : (b, a);

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && ((a < 0) ^ (b < 0))) q--;
            return q;
        }
    }
}
