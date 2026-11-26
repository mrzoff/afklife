using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Stage C-3C / C-3D: pure continuous path-network mesh generator.
    /// Consumes SettlementPathTopology.TopologyPlan + a terrain height sampler (e.g. ChunkTerrainMesher.SampleVisualHeight)
    /// and generates per-chunk watertight core & fringe meshes with:
    ///   - 3-column (Left / Center / Right) terrain-hugging ribbon cross-sections;
    ///   - concave C1-smooth Bezier corner fillets at 3-way T/Y and 4-way crossroads (flat back on 180-deg T-junctions);
    ///   - exact vertex-for-vertex (L, C, R) junction attachment (zero gaps, zero T-cracks, zero overlap);
    ///   - inner-corner foldback prevention on tight bends;
    ///   - exact chunk-border plane clipping (X & Z borders) with shared positions, heights, and tangents;
    ///   - selective chunk build filtering & per-chunk input signatures for incremental renderer updates.
    /// Zero MonoBehaviour or GameSaveData dependencies.
    /// </summary>
    public static class SettlementPathMeshBuilder
    {
        [Serializable]
        public sealed class MeshBuildConfig
        {
            public float ChunkSizeMeters = 16f;
            public float CoreYOffset = 0.032f;
            public float FringeYOffset = 0.022f;
            public float FringeExtraWidth = 0.09f;
            public float WidthVariationAmplitude = 0.06f;
            public float NormalSampleDelta = 0.18f;
            public float MaxAllowedSlopeDegrees = 28f;
            public float JunctionRadiusFactor = 1.32f;
            public int JunctionArcSteps = 5;
            public bool LocalToChunkOrigin = true;
            public float LocalChunkYBase = GrowthWorldPlacement.VisualYBase;
        }

        public sealed class ChunkPathMeshData
        {
            public Vector2Int ChunkCoord;
            public Mesh CoreMesh;
            public Mesh FringeMesh;
            public int CoreVertices;
            public int CoreTriangles;
            public int FringeVertices;
            public int FringeTriangles;
            public int JunctionPatches;
            public string ChunkHash;
        }

        public sealed class MeshBuildReport
        {
            public Dictionary<Vector2Int, ChunkPathMeshData> Chunks = new Dictionary<Vector2Int, ChunkPathMeshData>();
            public int TotalCoreVertices;
            public int TotalCoreTriangles;
            public int TotalFringeVertices;
            public int TotalFringeTriangles;
            public int TotalJunctionPatches;
            public float MinCoreGroundClearance = float.MaxValue;
            public float MaxCoreGroundClearance = float.MinValue;
            public float MinRibbonWidthMeters = float.MaxValue;
            public float MaxRibbonWidthMeters;
            public float MaxSlopeDegrees;
            public int SlopeViolations;
            public float MinUpwardNormalDot = 1f;
            public int DownwardTriangles;
            public int FoldbackTriangles;
            public int FoldbacksPrevented;
            public int BorderSharedPointPairs;
            public float MaxBorderPositionDelta;
            public float MaxBorderHeightDelta;
            public int JunctionAttachPairs;
            public float MaxJunctionAttachDelta;
            public string MeshHash;
        }

        private sealed class MeshBuffers
        {
            public readonly List<Vector3> Verts = new List<Vector3>(256);
            public readonly List<Vector3> Normals = new List<Vector3>(256);
            public readonly List<Vector2> Uvs = new List<Vector2>(256);
            public readonly List<int> Tris = new List<int>(512);
        }

        private struct BorderSample
        {
            public Vector2Int Chunk;
            public Vector2 PlaneKey;
            public Vector3 WorldPos;
            public Vector3 Tangent;
        }

        private sealed class JunctionSpoke
        {
            public float Angle;
            public Vector2 OutDir;
            public Vector2 CenterPt;
            public Vector2 LeftPt;
            public Vector2 RightPt;
            public Vector3 RibbonLeftWorld;
            public Vector3 RibbonCenterWorld;
            public Vector3 RibbonRightWorld;
            public bool HasRibbonWorld;
            public float HalfWidth;
        }

        // ------------------------------------------------------------------ Per-chunk input signatures (for incremental updates)

        /// <summary>
        /// Computes a deterministic per-chunk signature of all topology elements (edges + nearby junctions)
        /// that affect each chunk's geometry. Allows SettlementPathNetworkRenderer to rebuild only dirty chunks.
        /// </summary>
        public static Dictionary<Vector2Int, string> ComputeChunkInputSignatures(
            SettlementPathTopology.TopologyPlan plan,
            MeshBuildConfig cfg = null)
        {
            cfg ??= new MeshBuildConfig();
            var result = new Dictionary<Vector2Int, string>();
            if (plan == null || plan.Edges.Count == 0)
            {
                return result;
            }

            var ci = CultureInfo.InvariantCulture;
            var chunkBuilders = new Dictionary<Vector2Int, StringBuilder>();

            StringBuilder GetSb(Vector2Int c)
            {
                if (!chunkBuilders.TryGetValue(c, out var sb))
                {
                    sb = new StringBuilder(512);
                    sb.AppendFormat(ci, "CFG|{0:F3}|{1:F4}|{2:F4}|{3:F4}|{4:F4}|{5:F3}|{6}|{7:F3}\n",
                        cfg.ChunkSizeMeters, cfg.CoreYOffset, cfg.FringeYOffset, cfg.FringeExtraWidth,
                        cfg.WidthVariationAmplitude, cfg.JunctionRadiusFactor, cfg.JunctionArcSteps, cfg.LocalChunkYBase);
                    chunkBuilders[c] = sb;
                }

                return sb;
            }

            var pad = cfg.FringeExtraWidth + 0.65f;
            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var edge = plan.Edges[e];
                if (edge.Points == null || edge.Points.Count < 2) continue;

                var touched = CollectChunksForPolyline(edge.Points, edge.HalfWidth + pad, cfg.ChunkSizeMeters);
                for (var c = 0; c < touched.Count; c++)
                {
                    var sb = GetSb(touched[c]);
                    sb.AppendFormat(ci, "E|{0}|{1}|{2}|{3:F4}|{4}\n",
                        edge.Id, edge.FromNodeId, edge.ToNodeId, edge.HalfWidth, edge.Points.Count);
                    for (var p = 0; p < edge.Points.Count; p++)
                    {
                        sb.AppendFormat(ci, "P|{0:F4},{1:F4}\n", edge.Points[p].x, edge.Points[p].y);
                    }
                }
            }

            for (var j = 0; j < plan.Junctions.Count; j++)
            {
                var junc = plan.Junctions[j];
                var chunk = WorldToChunk(junc.Position, cfg.ChunkSizeMeters);
                var sb = GetSb(chunk);
                sb.AppendFormat(ci, "J|{0}|{1:F4},{2:F4}|{3}\n",
                    junc.NodeId, junc.Position.x, junc.Position.y, junc.EdgeIds.Count);
                for (var k = 0; k < junc.EdgeIds.Count; k++)
                {
                    sb.Append("JE|").Append(junc.EdgeIds[k]).Append('\n');
                }
            }

            using (var sha = SHA256.Create())
            {
                foreach (var kv in chunkBuilders)
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(kv.Value.ToString()));
                    var hex = new StringBuilder(16);
                    for (var i = 0; i < 8; i++) hex.Append(bytes[i].ToString("X2"));
                    result[kv.Key] = hex.ToString();
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ main entry

        public static MeshBuildReport BuildPerChunk(
            SettlementPathTopology.TopologyPlan plan,
            Func<float, float, float> sampleWorldY,
            MeshBuildConfig cfg = null,
            HashSet<Vector2Int> onlyChunks = null)
        {
            cfg ??= new MeshBuildConfig();
            sampleWorldY ??= ((_, _) => cfg.LocalChunkYBase);

            var report = new MeshBuildReport();
            if (plan == null || plan.Edges.Count == 0)
            {
                report.MinCoreGroundClearance = 0f;
                report.MaxCoreGroundClearance = 0f;
                report.MinRibbonWidthMeters = 0f;
                report.MeshHash = ComputeHash(report);
                return report;
            }

            var coreBufs = new Dictionary<Vector2Int, MeshBuffers>();
            var fringeBufs = new Dictionary<Vector2Int, MeshBuffers>();
            var junctionCounts = new Dictionary<Vector2Int, int>();
            var borderSamples = new List<BorderSample>(32);

            bool ShouldEmitChunk(Vector2Int c) => onlyChunks == null || onlyChunks.Contains(c);

            MeshBuffers GetBuf(Dictionary<Vector2Int, MeshBuffers> map, Vector2Int chunk)
            {
                if (!map.TryGetValue(chunk, out var b))
                {
                    b = new MeshBuffers();
                    map[chunk] = b;
                }

                return b;
            }

            // Index junctions and their trim radii
            var junctionTrim = new Dictionary<string, float>(StringComparer.Ordinal);
            var junctionSpokesCore = new Dictionary<string, List<JunctionSpoke>>(StringComparer.Ordinal);
            var junctionSpokesFringe = new Dictionary<string, List<JunctionSpoke>>(StringComparer.Ordinal);
            var nodesById = new Dictionary<string, SettlementPathTopology.PathNode>(StringComparer.Ordinal);

            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                nodesById[plan.Nodes[i].Id] = plan.Nodes[i];
            }

            for (var i = 0; i < plan.Junctions.Count; i++)
            {
                var j = plan.Junctions[i];
                var maxW = 0.22f;
                for (var e = 0; e < plan.Edges.Count; e++)
                {
                    var edge = plan.Edges[e];
                    if (edge.FromNodeId == j.NodeId || edge.ToNodeId == j.NodeId)
                    {
                        if (edge.HalfWidth > maxW) maxW = edge.HalfWidth;
                    }
                }

                junctionTrim[j.NodeId] = maxW * cfg.JunctionRadiusFactor;
                junctionSpokesCore[j.NodeId] = new List<JunctionSpoke>(4);
                junctionSpokesFringe[j.NodeId] = new List<JunctionSpoke>(4);
            }

            // 1. Emit every edge ribbon (trimmed at junction hubs, clipped cleanly at X & Z chunk borders)
            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var edge = plan.Edges[e];
                if (edge.Points == null || edge.Points.Count < 2) continue;

                var startTrim = junctionTrim.TryGetValue(edge.FromNodeId, out var st) ? st : 0f;
                var endTrim = junctionTrim.TryGetValue(edge.ToNodeId, out var et) ? et : 0f;

                var maxTrim = edge.Length * 0.40f;
                startTrim = Mathf.Min(startTrim, maxTrim);
                endTrim = Mathf.Min(endTrim, maxTrim);

                var trimmed = TrimPolyline(edge.Points, startTrim, endTrim);
                if (trimmed.Count < 2)
                {
                    continue;
                }

                var trimmedLen = PolylineLength(trimmed);

                // Record exact ribbon boundary attachment points at incident junctions (vertex-for-vertex!)
                JunctionSpoke startCoreSpoke = null;
                JunctionSpoke endCoreSpoke = null;

                if (startTrim > 0.001f && nodesById.TryGetValue(edge.FromNodeId, out var fromNode))
                {
                    var outDir = (trimmed[1] - trimmed[0]).normalized;
                    startCoreSpoke = RecordSpoke(junctionSpokesCore[edge.FromNodeId], fromNode.Position, trimmed[0], outDir, edge.HalfWidth);
                    RecordSpoke(junctionSpokesFringe[edge.FromNodeId], fromNode.Position, trimmed[0], outDir, edge.HalfWidth + cfg.FringeExtraWidth);
                }

                if (endTrim > 0.001f && nodesById.TryGetValue(edge.ToNodeId, out var toNode))
                {
                    var last = trimmed.Count - 1;
                    var outDir = (trimmed[last - 1] - trimmed[last]).normalized;
                    endCoreSpoke = RecordSpoke(junctionSpokesCore[edge.ToNodeId], toNode.Position, trimmed[last], outDir, edge.HalfWidth);
                    RecordSpoke(junctionSpokesFringe[edge.ToNodeId], toNode.Position, trimmed[last], outDir, edge.HalfWidth + cfg.FringeExtraWidth);
                }

                // Split polyline cleanly at X & Z chunk boundaries with shared exact plane points
                var pieces = SplitPolylineByChunkBorders(trimmed, cfg.ChunkSizeMeters);
                var runningTrimDist = 0f;

                for (var p = 0; p < pieces.Count; p++)
                {
                    var piece = pieces[p];
                    var chunk = piece.Chunk;
                    var pieceLen = PolylineLength(piece.Points);

                    var capStart = p == 0 && startTrim <= 0.001f;
                    var capEnd = p == pieces.Count - 1 && endTrim <= 0.001f;
                    var attachStartSpoke = p == 0 ? startCoreSpoke : null;
                    var attachEndSpoke = p == pieces.Count - 1 ? endCoreSpoke : null;

                    if (ShouldEmitChunk(chunk))
                    {
                        var coreBuf = GetBuf(coreBufs, chunk);
                        var fringeBuf = GetBuf(fringeBufs, chunk);

                        EmitRibbonPiece(
                            coreBuf, piece.Points, piece.StartTangent, piece.EndTangent,
                            edge.HalfWidth, cfg.CoreYOffset, runningTrimDist, trimmedLen,
                            edge.Id, capStart, capEnd, isCore: true,
                            chunk, sampleWorldY, cfg, report, borderSamples,
                            attachStartSpoke, attachEndSpoke);

                        EmitRibbonPiece(
                            fringeBuf, piece.Points, piece.StartTangent, piece.EndTangent,
                            edge.HalfWidth + cfg.FringeExtraWidth, cfg.FringeYOffset, runningTrimDist, trimmedLen,
                            edge.Id, capStart, capEnd, isCore: false,
                            chunk, sampleWorldY, cfg, report, null,
                            null, null);
                    }

                    runningTrimDist += pieceLen;
                }
            }

            // 2. Emit explicit watertight junction hub patches with smooth concave Bezier corner fillets
            for (var i = 0; i < plan.Junctions.Count; i++)
            {
                var j = plan.Junctions[i];
                var chunk = WorldToChunk(j.Position, cfg.ChunkSizeMeters);
                if (!ShouldEmitChunk(chunk)) continue;

                var coreBuf = GetBuf(coreBufs, chunk);
                var fringeBuf = GetBuf(fringeBufs, chunk);

                var radiusCore = junctionTrim[j.NodeId];
                var radiusFringe = radiusCore + cfg.FringeExtraWidth;

                EmitJunctionPatch(
                    coreBuf, j.Position, junctionSpokesCore[j.NodeId], radiusCore,
                    cfg.CoreYOffset, isCore: true, chunk, sampleWorldY, cfg, report);

                EmitJunctionPatch(
                    fringeBuf, j.Position, junctionSpokesFringe[j.NodeId], radiusFringe,
                    cfg.FringeYOffset, isCore: false, chunk, sampleWorldY, cfg, report);

                junctionCounts.TryGetValue(chunk, out var jc);
                junctionCounts[chunk] = jc + 1;
                report.TotalJunctionPatches++;
            }

            // 3. Measure chunk-boundary seam continuity between matching border samples (both X & Z borders)
            EvaluateBorderSeams(borderSamples, report);

            // 4. Finalize Unity Mesh objects per chunk in deterministic sorted order
            var chunkKeys = new List<Vector2Int>(coreBufs.Keys);
            chunkKeys.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

            for (var i = 0; i < chunkKeys.Count; i++)
            {
                var ck = chunkKeys[i];
                var cb = coreBufs[ck];
                var fb = fringeBufs.TryGetValue(ck, out var fBuf) ? fBuf : new MeshBuffers();

                var coreMesh = CreateMesh($"PathCore_{ck.x}_{ck.y}", cb);
                var fringeMesh = CreateMesh($"PathFringe_{ck.x}_{ck.y}", fb);

                junctionCounts.TryGetValue(ck, out var jCount);

                var data = new ChunkPathMeshData
                {
                    ChunkCoord = ck,
                    CoreMesh = coreMesh,
                    FringeMesh = fringeMesh,
                    CoreVertices = cb.Verts.Count,
                    CoreTriangles = cb.Tris.Count / 3,
                    FringeVertices = fb.Verts.Count,
                    FringeTriangles = fb.Tris.Count / 3,
                    JunctionPatches = jCount
                };
                data.ChunkHash = ComputeChunkHash(data);

                report.Chunks[ck] = data;
                report.TotalCoreVertices += data.CoreVertices;
                report.TotalCoreTriangles += data.CoreTriangles;
                report.TotalFringeVertices += data.FringeVertices;
                report.TotalFringeTriangles += data.FringeTriangles;
            }

            if (report.MinCoreGroundClearance == float.MaxValue)
            {
                report.MinCoreGroundClearance = 0f;
                report.MaxCoreGroundClearance = 0f;
            }

            if (report.MinRibbonWidthMeters == float.MaxValue)
            {
                report.MinRibbonWidthMeters = 0f;
            }

            report.MeshHash = ComputeHash(report);
            return report;
        }

        // ------------------------------------------------------------------ 3-column terrain-hugging ribbon emission

        private sealed class ChunkPiece
        {
            public Vector2Int Chunk;
            public List<Vector2> Points = new List<Vector2>();
            public Vector2 StartTangent;
            public Vector2 EndTangent;
        }

        private static void EmitRibbonPiece(
            MeshBuffers buf,
            List<Vector2> pts,
            Vector2 startTangent2D,
            Vector2 endTangent2D,
            float baseHalfWidth,
            float yOffset,
            float distStartTrimmed,
            float totalTrimmedLen,
            string edgeId,
            bool capStart,
            bool capEnd,
            bool isCore,
            Vector2Int chunk,
            Func<float, float, float> sampleWorldY,
            MeshBuildConfig cfg,
            MeshBuildReport report,
            List<BorderSample> borderSamples,
            JunctionSpoke startSpoke,
            JunctionSpoke endSpoke)
        {
            if (pts.Count < 2) return;

            var count = pts.Count;
            var leftXZ = new Vector2[count];
            var rightXZ = new Vector2[count];
            var tangents = new Vector2[count];
            var dists = new float[count];

            var dist = distStartTrimmed;
            var edgePhase = edgeId != null ? (StableStringHash(edgeId) % 1024) * 0.0061f : 0f;

            for (var i = 0; i < count; i++)
            {
                var p = pts[i];
                if (i > 0)
                {
                    dist += Vector2.Distance(pts[i - 1], p);
                }

                dists[i] = dist;

                Vector2 t2;
                if (i == 0 && startTangent2D.sqrMagnitude > 1e-6f) t2 = startTangent2D;
                else if (i == count - 1 && endTangent2D.sqrMagnitude > 1e-6f) t2 = endTangent2D;
                else if (i == 0) t2 = (pts[1] - pts[0]).normalized;
                else if (i == count - 1) t2 = (pts[i] - pts[i - 1]).normalized;
                else
                {
                    var a = (pts[i] - pts[i - 1]).normalized;
                    var b = (pts[i + 1] - pts[i]).normalized;
                    t2 = (a + b).sqrMagnitude > 1e-6f ? (a + b).normalized : a;
                }

                tangents[i] = t2;

                // Subtle organic width variation — fades smoothly to exact 0.0 at trimmed ends so
                // junction attachments & end caps match baseHalfWidth with 0.00000 m error!
                var u = totalTrimmedLen > 0.01f ? Mathf.Clamp01(dist / totalTrimmedLen) : 0.5f;
                var endFade = Mathf.Sin(u * Mathf.PI);
                endFade *= endFade; // C1 smooth zero derivative at both ends
                var wave = Mathf.Sin(dist * 2.1f + edgePhase);
                var hw = baseHalfWidth * (1f + wave * cfg.WidthVariationAmplitude * endFade);

                // Side vector on XZ perpendicular to tangent: Cross(+Up, +tangent) = (t.y, 0, -t.x) = +Right
                var side2 = new Vector2(t2.y, -t2.x);
                leftXZ[i] = p - side2 * hw;
                rightXZ[i] = p + side2 * hw;
            }

            // Multi-pass inner-corner foldback prevention on tight bends
            // (never modifies i==0 or i==count-1 so border & junction seams remain 0.00000 m exact)
            for (var pass = 0; pass < 4; pass++)
            {
                var changed = false;
                for (var i = 1; i < count - 1; i++)
                {
                    var segDir = pts[i] - pts[i - 1];
                    if (segDir.sqrMagnitude < 1e-8f) continue;
                    segDir.Normalize();

                    if (Vector2.Dot(leftXZ[i] - leftXZ[i - 1], segDir) < 0.005f)
                    {
                        var mid = Vector2.Lerp(leftXZ[i - 1], leftXZ[i + 1], 0.5f);
                        var fwdDot = Vector2.Dot(mid - leftXZ[i - 1], segDir);
                        leftXZ[i] = fwdDot < 0.005f ? leftXZ[i - 1] + segDir * 0.008f : mid;
                        if (isCore) report.FoldbacksPrevented++;
                        changed = true;
                    }

                    if (Vector2.Dot(rightXZ[i] - rightXZ[i - 1], segDir) < 0.005f)
                    {
                        var mid = Vector2.Lerp(rightXZ[i - 1], rightXZ[i + 1], 0.5f);
                        var fwdDot = Vector2.Dot(mid - rightXZ[i - 1], segDir);
                        rightXZ[i] = fwdDot < 0.005f ? rightXZ[i - 1] + segDir * 0.008f : mid;
                        if (isCore) report.FoldbacksPrevented++;
                        changed = true;
                    }
                }

                if (!changed) break;
            }

            var chunkOrigin = ChunkOrigin(chunk, cfg);
            var baseVert = buf.Verts.Count;

            // Emit 3 columns per slice: Left (0), Center (1), Right (2) so the ribbon hugs ridges AND gullies
            for (var i = 0; i < count; i++)
            {
                var p = pts[i];
                var lxz = leftXZ[i];
                var rxz = rightXZ[i];

                var groundL = sampleWorldY(lxz.x, lxz.y);
                var groundC = sampleWorldY(p.x, p.y);
                var groundR = sampleWorldY(rxz.x, rxz.y);

                var worldL = new Vector3(lxz.x, groundL + yOffset, lxz.y);
                var worldC = new Vector3(p.x, groundC + yOffset, p.y);
                var worldR = new Vector3(rxz.x, groundR + yOffset, rxz.y);

                var nL = SampleTerrainNormal(lxz.x, lxz.y, sampleWorldY, cfg.NormalSampleDelta);
                var nC = SampleTerrainNormal(p.x, p.y, sampleWorldY, cfg.NormalSampleDelta);
                var nR = SampleTerrainNormal(rxz.x, rxz.y, sampleWorldY, cfg.NormalSampleDelta);

                if (isCore)
                {
                    TrackClearance(report, worldL.y - groundL);
                    TrackClearance(report, worldC.y - groundC);
                    TrackClearance(report, worldR.y - groundR);

                    var fullW = Vector2.Distance(lxz, rxz);
                    if (fullW < report.MinRibbonWidthMeters) report.MinRibbonWidthMeters = fullW;
                    if (fullW > report.MaxRibbonWidthMeters) report.MaxRibbonWidthMeters = fullW;

                    if (i > 0)
                    {
                        var prev = pts[i - 1];
                        var hPrev = sampleWorldY(prev.x, prev.y);
                        var horiz = Mathf.Max(0.001f, Vector2.Distance(prev, p));
                        var slopeDeg = Mathf.Atan2(Mathf.Abs(groundC - hPrev), horiz) * Mathf.Rad2Deg;
                        if (slopeDeg > report.MaxSlopeDegrees) report.MaxSlopeDegrees = slopeDeg;
                        if (slopeDeg > cfg.MaxAllowedSlopeDegrees) report.SlopeViolations++;

                        var segDir = (p - prev).normalized;
                        if (Vector2.Dot(lxz - leftXZ[i - 1], segDir) < -1e-4f) report.FoldbackTriangles++;
                        if (Vector2.Dot(rxz - rightXZ[i - 1], segDir) < -1e-4f) report.FoldbackTriangles++;
                    }

                    if (borderSamples != null && (i == 0 || i == count - 1) && IsOnChunkBorder(p, cfg.ChunkSizeMeters))
                    {
                        borderSamples.Add(new BorderSample
                        {
                            Chunk = chunk,
                            PlaneKey = new Vector2(Mathf.Round(p.x * 100f) * 0.01f, Mathf.Round(p.y * 100f) * 0.01f),
                            WorldPos = worldC,
                            Tangent = new Vector3(tangents[i].x, 0f, tangents[i].y)
                        });
                    }

                    // Record exact world attachment triples for junction continuity verification
                    if (i == 0 && startSpoke != null)
                    {
                        startSpoke.RibbonLeftWorld = worldL;
                        startSpoke.RibbonCenterWorld = worldC;
                        startSpoke.RibbonRightWorld = worldR;
                        startSpoke.HasRibbonWorld = true;
                    }
                    else if (i == count - 1 && endSpoke != null)
                    {
                        // When arriving at endSpoke, spoke's outDir is -tangent, so ribbon's Left/Right swap relative to spoke outDir
                        endSpoke.RibbonLeftWorld = worldR;
                        endSpoke.RibbonCenterWorld = worldC;
                        endSpoke.RibbonRightWorld = worldL;
                        endSpoke.HasRibbonWorld = true;
                    }
                }

                buf.Verts.Add(worldL - chunkOrigin);
                buf.Normals.Add(nL);
                buf.Uvs.Add(new Vector2(0f, dists[i]));

                buf.Verts.Add(worldC - chunkOrigin);
                buf.Normals.Add(nC);
                buf.Uvs.Add(new Vector2(0.5f, dists[i]));

                buf.Verts.Add(worldR - chunkOrigin);
                buf.Normals.Add(nR);
                buf.Uvs.Add(new Vector2(1f, dists[i]));
            }

            // Two strips per step: Left-Center and Center-Right (4 upward triangles per step)
            for (var i = 0; i < count - 1; i++)
            {
                var l0 = baseVert + i * 3;
                var c0 = l0 + 1;
                var r0 = l0 + 2;
                var l1 = l0 + 3;
                var c1 = l0 + 4;
                var r1 = l0 + 5;

                AddVerifiedTriangle(buf, l0, l1, c0, isCore, report);
                AddVerifiedTriangle(buf, c0, l1, c1, isCore, report);
                AddVerifiedTriangle(buf, c0, c1, r0, isCore, report);
                AddVerifiedTriangle(buf, r0, c1, r1, isCore, report);
            }

            // Rounded end caps at free ends (door/dead-end), never at junctions or chunk borders
            if (capStart)
            {
                EmitEndCap(buf, pts[0], -tangents[0], baseHalfWidth, yOffset, chunkOrigin, sampleWorldY, cfg, isCore, report);
            }

            if (capEnd)
            {
                EmitEndCap(buf, pts[count - 1], tangents[count - 1], baseHalfWidth, yOffset, chunkOrigin, sampleWorldY, cfg, isCore, report);
            }
        }

        private static void EmitEndCap(
            MeshBuffers buf,
            Vector2 centerXZ,
            Vector2 outDir,
            float halfWidth,
            float yOffset,
            Vector3 chunkOrigin,
            Func<float, float, float> sampleWorldY,
            MeshBuildConfig cfg,
            bool isCore,
            MeshBuildReport report)
        {
            if (outDir.sqrMagnitude < 1e-6f) return;
            outDir.Normalize();
            var side = new Vector2(outDir.y, -outDir.x);

            var centerIdx = buf.Verts.Count;
            var cy = sampleWorldY(centerXZ.x, centerXZ.y) + yOffset;
            buf.Verts.Add(new Vector3(centerXZ.x, cy, centerXZ.y) - chunkOrigin);
            buf.Normals.Add(SampleTerrainNormal(centerXZ.x, centerXZ.y, sampleWorldY, cfg.NormalSampleDelta));
            buf.Uvs.Add(new Vector2(0.5f, 0.5f));

            const int steps = 6;
            var ringStart = buf.Verts.Count;
            for (var k = 0; k <= steps; k++)
            {
                var ang = -Mathf.PI * 0.5f + Mathf.PI * (k / (float)steps);
                var dir = outDir * Mathf.Cos(ang) + side * Mathf.Sin(ang);
                var p = centerXZ + dir * halfWidth;
                var py = sampleWorldY(p.x, p.y) + yOffset;
                buf.Verts.Add(new Vector3(p.x, py, p.y) - chunkOrigin);
                buf.Normals.Add(SampleTerrainNormal(p.x, p.y, sampleWorldY, cfg.NormalSampleDelta));
                buf.Uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Sin(ang), 0.5f + 0.5f * Mathf.Cos(ang)));
            }

            for (var k = 0; k < steps; k++)
            {
                AddVerifiedTriangle(buf, centerIdx, ringStart + k, ringStart + k + 1, isCore, report);
            }
        }

        // ------------------------------------------------------------------ Watertight junction hub patch with concave Bezier fillets

        private static JunctionSpoke RecordSpoke(
            List<JunctionSpoke> list,
            Vector2 junctionCenter,
            Vector2 attachPoint,
            Vector2 outDir,
            float halfWidth)
        {
            if (outDir.sqrMagnitude < 1e-6f) return null;
            outDir.Normalize();
            var side = new Vector2(outDir.y, -outDir.x);
            var spoke = new JunctionSpoke
            {
                Angle = Mathf.Atan2(outDir.y, outDir.x),
                OutDir = outDir,
                CenterPt = attachPoint,
                LeftPt = attachPoint - side * halfWidth,
                RightPt = attachPoint + side * halfWidth,
                HalfWidth = halfWidth
            };
            list.Add(spoke);
            return spoke;
        }

        private static void EmitJunctionPatch(
            MeshBuffers buf,
            Vector2 centerXZ,
            List<JunctionSpoke> spokes,
            float hubRadius,
            float yOffset,
            bool isCore,
            Vector2Int chunk,
            Func<float, float, float> sampleWorldY,
            MeshBuildConfig cfg,
            MeshBuildReport report)
        {
            var chunkOrigin = ChunkOrigin(chunk, cfg);
            var ring = new List<Vector2>(32);

            if (spokes != null && spokes.Count >= 2)
            {
                // Sort spokes counter-clockwise by outgoing angle in XZ.
                // Looking outward along OutDir, RightPt is clockwise (smaller angle) and LeftPt is CCW (larger angle).
                spokes.Sort((a, b) => a.Angle.CompareTo(b.Angle));

                var filletSteps = Mathf.Max(2, cfg.JunctionArcSteps);

                for (var i = 0; i < spokes.Count; i++)
                {
                    var cur = spokes[i];
                    var next = spokes[(i + 1) % spokes.Count];

                    // Share ALL THREE ribbon boundary vertices (Right, Center, Left) so zero T-crack on sloped terrain!
                    AddRingPoint(ring, cur.RightPt);
                    AddRingPoint(ring, cur.CenterPt);
                    AddRingPoint(ring, cur.LeftPt);

                    if (isCore && cur.HasRibbonWorld)
                    {
                        VerifyJunctionAttach(cur.RightPt, cur.RibbonRightWorld, yOffset, sampleWorldY, report);
                        VerifyJunctionAttach(cur.CenterPt, cur.RibbonCenterWorld, yOffset, sampleWorldY, report);
                        VerifyJunctionAttach(cur.LeftPt, cur.RibbonLeftWorld, yOffset, sampleWorldY, report);
                    }

                    // Build smooth concave Bezier corner fillet from cur.LeftPt to next.RightPt
                    var p0 = cur.LeftPt;
                    var p1 = next.RightPt;
                    if (Vector2.Distance(p0, p1) < 0.02f) continue;

                    var sectorAngle = next.Angle - cur.Angle;
                    if (sectorAngle <= 0f) sectorAngle += Mathf.PI * 2f;

                    var t0 = -cur.OutDir;
                    var t1 = -next.OutDir;
                    var ctrl = ComputeFilletControlPoint(centerXZ, p0, t0, p1, t1, sectorAngle, hubRadius);

                    for (var k = 1; k < filletSteps; k++)
                    {
                        var t = k / (float)filletSteps;
                        var inv = 1f - t;
                        var q = inv * inv * p0 + 2f * inv * t * ctrl + t * t * p1;
                        AddRingPoint(ring, q);
                    }
                }
            }
            else
            {
                const int discSteps = 12;
                for (var k = 0; k < discSteps; k++)
                {
                    var a = Mathf.PI * 2f * (k / (float)discSteps);
                    ring.Add(centerXZ + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * hubRadius);
                }
            }

            if (ring.Count < 3) return;

            var centerIdx = buf.Verts.Count;
            var groundC = sampleWorldY(centerXZ.x, centerXZ.y);
            var cy = groundC + yOffset;
            buf.Verts.Add(new Vector3(centerXZ.x, cy, centerXZ.y) - chunkOrigin);
            buf.Normals.Add(SampleTerrainNormal(centerXZ.x, centerXZ.y, sampleWorldY, cfg.NormalSampleDelta));
            buf.Uvs.Add(new Vector2(0.5f, 0.5f));
            if (isCore) TrackClearance(report, yOffset);

            var ringStart = buf.Verts.Count;
            for (var i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var gp = sampleWorldY(p.x, p.y);
                var py = gp + yOffset;
                buf.Verts.Add(new Vector3(p.x, py, p.y) - chunkOrigin);
                buf.Normals.Add(SampleTerrainNormal(p.x, p.y, sampleWorldY, cfg.NormalSampleDelta));
                buf.Uvs.Add(new Vector2(0.5f + (p.x - centerXZ.x) * 0.5f, 0.5f + (p.y - centerXZ.y) * 0.5f));
                if (isCore) TrackClearance(report, py - gp);
            }

            // Counter-clockwise in XZ means Cross(ring[i+1]-C, ring[i]-C) points +Up
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ringStart + i;
                var b = ringStart + ((i + 1) % ring.Count);
                AddVerifiedTriangle(buf, centerIdx, b, a, isCore, report);
            }
        }

        private static Vector2 ComputeFilletControlPoint(
            Vector2 centerXZ,
            Vector2 p0,
            Vector2 t0,
            Vector2 p1,
            Vector2 t1,
            float sectorAngle,
            float hubRadius)
        {
            var mid = (p0 + p1) * 0.5f;

            // Near-straight back of a T-junction (~180 deg): keep edge completely flush & straight!
            if (Mathf.Abs(sectorAngle - Mathf.PI) < 0.24f)
            {
                return mid;
            }

            // Wide obtuse outer side (> 195 deg, e.g. outside of a shallow Y-fork): gentle outward blend
            if (sectorAngle > Mathf.PI + 0.24f)
            {
                var outDir = (mid - centerXZ);
                if (outDir.sqrMagnitude < 1e-6f) return mid;
                outDir.Normalize();
                var rAvg = 0.5f * (Vector2.Distance(centerXZ, p0) + Vector2.Distance(centerXZ, p1));
                return centerXZ + outDir * Mathf.Min(rAvg * 0.92f, hubRadius * 0.95f);
            }

            // Concave corner (90 deg crossroads / T-junction inner corner / Y-fork crotch):
            // intersect inward edge tangent rays p0 + u*t0 = p1 + v*t1
            var denom = t0.x * t1.y - t0.y * t1.x;
            if (Mathf.Abs(denom) > 0.15f)
            {
                var diff = p1 - p0;
                var u = (diff.x * t1.y - diff.y * t1.x) / denom;
                var v = (diff.x * t0.y - diff.y * t0.x) / denom;
                var maxRay = Mathf.Max(Vector2.Distance(p0, p1) * 0.85f, hubRadius * 1.1f);
                if (u > 0.01f && v > 0.01f && u < maxRay && v < maxRay)
                {
                    var inter = p0 + t0 * u;
                    // Keep control point nicely between the sharp inner corner and hub center
                    var maxR = Mathf.Max(Vector2.Distance(centerXZ, p0), Vector2.Distance(centerXZ, p1)) * 1.05f;
                    var offset = inter - centerXZ;
                    if (offset.magnitude > maxR)
                    {
                        inter = centerXZ + offset.normalized * maxR;
                    }

                    return inter;
                }
            }

            return Vector2.Lerp(mid, centerXZ, 0.20f);
        }

        private static void AddRingPoint(List<Vector2> ring, Vector2 pt)
        {
            if (ring.Count == 0 || Vector2.Distance(ring[ring.Count - 1], pt) > 1e-4f)
            {
                ring.Add(pt);
            }
        }

        private static void VerifyJunctionAttach(
            Vector2 hubPtXZ,
            Vector3 ribbonWorld,
            float yOffset,
            Func<float, float, float> sampleWorldY,
            MeshBuildReport report)
        {
            var hubWorld = new Vector3(hubPtXZ.x, sampleWorldY(hubPtXZ.x, hubPtXZ.y) + yOffset, hubPtXZ.y);
            var d = Vector3.Distance(hubWorld, ribbonWorld);
            report.JunctionAttachPairs++;
            if (d > report.MaxJunctionAttachDelta) report.MaxJunctionAttachDelta = d;
        }

        // ------------------------------------------------------------------ Chunk border splitting (X & Z)

        private static List<ChunkPiece> SplitPolylineByChunkBorders(List<Vector2> pts, float chunkSize)
        {
            var pieces = new List<ChunkPiece>();
            if (pts == null || pts.Count < 2) return pieces;

            // First insert exact intersection points wherever a segment crosses any X or Z chunk boundary plane
            var expanded = new List<Vector2>(pts.Count + 4) { pts[0] };
            for (var i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                InsertBorderCrossings(a, b, chunkSize, expanded);
                if (Vector2.Distance(expanded[expanded.Count - 1], b) > 1e-4f)
                {
                    expanded.Add(b);
                }
            }

            // Group contiguous segments by chunk of segment midpoint
            ChunkPiece current = null;
            for (var i = 1; i < expanded.Count; i++)
            {
                var a = expanded[i - 1];
                var b = expanded[i];
                if (Vector2.Distance(a, b) < 1e-4f) continue;

                var mid = (a + b) * 0.5f;
                var chunk = WorldToChunk(mid, chunkSize);

                if (current == null || current.Chunk != chunk)
                {
                    if (current != null && current.Points.Count >= 2)
                    {
                        pieces.Add(current);
                    }

                    current = new ChunkPiece { Chunk = chunk };
                    current.Points.Add(a);
                    current.StartTangent = ComputeTangentAt(expanded, i - 1);
                }

                current.Points.Add(b);
                current.EndTangent = ComputeTangentAt(expanded, i);
            }

            if (current != null && current.Points.Count >= 2)
            {
                pieces.Add(current);
            }

            return pieces;
        }

        private static void InsertBorderCrossings(Vector2 a, Vector2 b, float chunkSize, List<Vector2> output)
        {
            var half = chunkSize * 0.5f;
            var ts = new List<float>(4);

            void CollectAxis(float v0, float v1)
            {
                var d = v1 - v0;
                if (Mathf.Abs(d) < 1e-5f) return;
                var kMin = Mathf.CeilToInt((Mathf.Min(v0, v1) + half) / chunkSize);
                var kMax = Mathf.FloorToInt((Mathf.Max(v0, v1) + half) / chunkSize);
                for (var k = kMin; k <= kMax; k++)
                {
                    var plane = k * chunkSize - half;
                    var t = (plane - v0) / d;
                    if (t > 1e-4f && t < 1f - 1e-4f)
                    {
                        ts.Add(t);
                    }
                }
            }

            CollectAxis(a.x, b.x);
            CollectAxis(a.y, b.y);
            if (ts.Count == 0) return;

            ts.Sort();
            for (var i = 0; i < ts.Count; i++)
            {
                var p = Vector2.Lerp(a, b, ts[i]);
                if (Vector2.Distance(output[output.Count - 1], p) > 1e-4f)
                {
                    output.Add(p);
                }
            }
        }

        private static Vector2 ComputeTangentAt(List<Vector2> pts, int idx)
        {
            if (pts.Count < 2) return Vector2.up;
            if (idx <= 0) return (pts[1] - pts[0]).normalized;
            if (idx >= pts.Count - 1) return (pts[pts.Count - 1] - pts[pts.Count - 2]).normalized;
            var a = (pts[idx] - pts[idx - 1]).normalized;
            var b = (pts[idx + 1] - pts[idx]).normalized;
            return (a + b).sqrMagnitude > 1e-6f ? (a + b).normalized : a;
        }

        // ------------------------------------------------------------------ Helpers

        private static List<Vector2Int> CollectChunksForPolyline(List<Vector2> pts, float pad, float chunkSize)
        {
            var set = new HashSet<Vector2Int>();
            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var cMin = WorldToChunk(new Vector2(p.x - pad, p.y - pad), chunkSize);
                var cMax = WorldToChunk(new Vector2(p.x + pad, p.y + pad), chunkSize);
                for (var cz = cMin.y; cz <= cMax.y; cz++)
                {
                    for (var cx = cMin.x; cx <= cMax.x; cx++)
                    {
                        set.Add(new Vector2Int(cx, cz));
                    }
                }
            }

            var list = new List<Vector2Int>(set);
            list.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            return list;
        }

        private static List<Vector2> TrimPolyline(List<Vector2> pts, float startTrim, float endTrim)
        {
            if (startTrim <= 1e-4f && endTrim <= 1e-4f)
            {
                return new List<Vector2>(pts);
            }

            var total = PolylineLength(pts);
            var sStart = Mathf.Clamp(startTrim, 0f, total * 0.45f);
            var sEnd = Mathf.Clamp(total - endTrim, sStart + 0.05f, total);

            var result = new List<Vector2>(pts.Count);
            var acc = 0f;
            var started = false;

            for (var i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                var seg = Vector2.Distance(a, b);
                if (seg < 1e-5f) continue;

                var segStart = acc;
                var segEnd = acc + seg;

                if (!started && segEnd >= sStart)
                {
                    var t = Mathf.Clamp01((sStart - segStart) / seg);
                    result.Add(Vector2.Lerp(a, b, t));
                    started = true;
                }

                if (started)
                {
                    if (segEnd >= sEnd)
                    {
                        var t = Mathf.Clamp01((sEnd - segStart) / seg);
                        var p = Vector2.Lerp(a, b, t);
                        if (Vector2.Distance(result[result.Count - 1], p) > 1e-4f)
                        {
                            result.Add(p);
                        }

                        break;
                    }

                    if (Vector2.Distance(result[result.Count - 1], b) > 1e-4f)
                    {
                        result.Add(b);
                    }
                }

                acc = segEnd;
            }

            return result;
        }

        private static void AddVerifiedTriangle(
            MeshBuffers buf, int i0, int i1, int i2, bool isCore, MeshBuildReport report)
        {
            var v0 = buf.Verts[i0];
            var v1 = buf.Verts[i1];
            var v2 = buf.Verts[i2];
            var cross = Vector3.Cross(v1 - v0, v2 - v0);
            if (cross.sqrMagnitude < 1e-10f) return;

            // Enforce upward winding if a curved fillet inverted order
            if (cross.y < 0f)
            {
                var tmp = i1;
                i1 = i2;
                i2 = tmp;
                cross = -cross;
            }

            buf.Tris.Add(i0);
            buf.Tris.Add(i1);
            buf.Tris.Add(i2);

            if (isCore)
            {
                var upDot = Vector3.Dot(cross.normalized, Vector3.up);
                if (upDot < report.MinUpwardNormalDot) report.MinUpwardNormalDot = upDot;
                if (upDot <= 0f) report.DownwardTriangles++;
            }
        }

        private static Vector3 SampleTerrainNormal(
            float x, float z, Func<float, float, float> sampleWorldY, float d)
        {
            var hL = sampleWorldY(x - d, z);
            var hR = sampleWorldY(x + d, z);
            var hB = sampleWorldY(x, z - d);
            var hF = sampleWorldY(x, z + d);
            var n = new Vector3(hL - hR, 2f * d, hB - hF);
            return n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.up;
        }

        private static void TrackClearance(MeshBuildReport report, float clearance)
        {
            if (clearance < report.MinCoreGroundClearance) report.MinCoreGroundClearance = clearance;
            if (clearance > report.MaxCoreGroundClearance) report.MaxCoreGroundClearance = clearance;
        }

        private static void EvaluateBorderSeams(List<BorderSample> samples, MeshBuildReport report)
        {
            for (var i = 0; i < samples.Count; i++)
            {
                for (var j = i + 1; j < samples.Count; j++)
                {
                    if (samples[i].Chunk == samples[j].Chunk) continue;
                    if (Vector2.Distance(samples[i].PlaneKey, samples[j].PlaneKey) > 0.05f) continue;

                    report.BorderSharedPointPairs++;
                    var posDelta = Vector3.Distance(samples[i].WorldPos, samples[j].WorldPos);
                    var hDelta = Mathf.Abs(samples[i].WorldPos.y - samples[j].WorldPos.y);
                    if (posDelta > report.MaxBorderPositionDelta) report.MaxBorderPositionDelta = posDelta;
                    if (hDelta > report.MaxBorderHeightDelta) report.MaxBorderHeightDelta = hDelta;
                }
            }
        }

        private static bool IsOnChunkBorder(Vector2 p, float chunkSize)
        {
            var half = chunkSize * 0.5f;
            var rx = Mathf.Abs(Mathf.Repeat(p.x + half, chunkSize));
            var rz = Mathf.Abs(Mathf.Repeat(p.y + half, chunkSize));
            return rx < 0.02f || Mathf.Abs(rx - chunkSize) < 0.02f ||
                   rz < 0.02f || Mathf.Abs(rz - chunkSize) < 0.02f;
        }

        private static Vector3 ChunkOrigin(Vector2Int chunk, MeshBuildConfig cfg)
        {
            if (!cfg.LocalToChunkOrigin) return Vector3.zero;
            var o = GrowthWorldPlacement.ChunkOriginXZ(chunk.x, chunk.y, cfg.ChunkSizeMeters);
            return new Vector3(o.x, cfg.LocalChunkYBase, o.y);
        }

        private static Vector2Int WorldToChunk(Vector2 worldXZ, float chunkSize)
        {
            var cx = Mathf.FloorToInt((worldXZ.x + chunkSize * 0.5f) / chunkSize);
            var cz = Mathf.FloorToInt((worldXZ.y + chunkSize * 0.5f) / chunkSize);
            return new Vector2Int(cx, cz);
        }

        private static float PolylineLength(List<Vector2> pts)
        {
            var s = 0f;
            for (var i = 1; i < pts.Count; i++) s += Vector2.Distance(pts[i - 1], pts[i]);
            return s;
        }

        private static uint StableStringHash(string s)
        {
            // FNV-1a 32-bit (deterministic across runtimes & platforms, unlike string.GetHashCode)
            uint h = 2166136261u;
            for (var i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }

            return h;
        }

        private static Mesh CreateMesh(string name, MeshBuffers buf)
        {
            var m = new Mesh { name = name };
            if (buf.Verts.Count > 65000)
            {
                m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            m.SetVertices(buf.Verts);
            m.SetNormals(buf.Normals);
            m.SetUVs(0, buf.Uvs);
            m.SetTriangles(buf.Tris, 0);
            m.RecalculateBounds();
            return m;
        }

        private static string ComputeChunkHash(ChunkPathMeshData d)
        {
            var sb = new StringBuilder(512);
            var ci = CultureInfo.InvariantCulture;
            sb.AppendFormat(ci, "C|{0},{1}|{2}|{3}|{4}|{5}|{6}\n",
                d.ChunkCoord.x, d.ChunkCoord.y, d.CoreVertices, d.CoreTriangles,
                d.FringeVertices, d.FringeTriangles, d.JunctionPatches);
            if (d.CoreMesh != null)
            {
                var verts = d.CoreMesh.vertices;
                var stride = Mathf.Max(1, verts.Length / 64);
                for (var v = 0; v < verts.Length; v += stride)
                {
                    sb.AppendFormat(ci, "V|{0:F3},{1:F3},{2:F3}\n", verts[v].x, verts[v].y, verts[v].z);
                }
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(16);
                for (var i = 0; i < 8; i++) hex.Append(bytes[i].ToString("X2"));
                return hex.ToString();
            }
        }

        private static string ComputeHash(MeshBuildReport report)
        {
            var sb = new StringBuilder(1024);
            var keys = new List<Vector2Int>(report.Chunks.Keys);
            keys.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            for (var i = 0; i < keys.Count; i++)
            {
                var d = report.Chunks[keys[i]];
                sb.Append(d.ChunkCoord.x).Append(',').Append(d.ChunkCoord.y).Append('|').Append(d.ChunkHash).Append('\n');
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(16);
                for (var i = 0; i < 8; i++) hex.Append(bytes[i].ToString("X2"));
                return hex.ToString();
            }
        }
    }
}
