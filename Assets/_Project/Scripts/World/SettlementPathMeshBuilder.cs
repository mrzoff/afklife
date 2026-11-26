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
    /// and generates per-chunk watertight core & fringe meshes with explicit junction patches,
    /// shared chunk-boundary vertices, and terrain-aligned upward normals.
    /// Zero MonoBehaviour or GameSaveData dependencies.
    /// </summary>
    public static class SettlementPathMeshBuilder
    {
        public sealed class MeshBuildConfig
        {
            public float ChunkSizeMeters = 16f;
            public float CoreYOffset = 0.032f;
            public float FringeYOffset = 0.022f;
            public float FringeExtraWidth = 0.09f;
            public float WidthVariationAmplitude = 0.06f;
            public float NormalSampleDelta = 0.18f;
            public float MaxAllowedSlopeDegrees = 28f;
            public float JunctionRadiusFactor = 1.05f;
            public int JunctionArcSteps = 4;
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
            public float MaxSlopeDegrees;
            public int SlopeViolations;
            public float MinUpwardNormalDot = 1f;
            public int DownwardTriangles;
            public int BorderSharedPointPairs;
            public float MaxBorderPositionDelta;
            public float MaxBorderHeightDelta;
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
            public Vector2 LeftPt;
            public Vector2 RightPt;
            public float HalfWidth;
        }

        // ------------------------------------------------------------------ main entry

        public static MeshBuildReport BuildPerChunk(
            SettlementPathTopology.TopologyPlan plan,
            Func<float, float, float> sampleWorldY,
            MeshBuildConfig cfg = null)
        {
            cfg ??= new MeshBuildConfig();
            sampleWorldY ??= ((_, _) => cfg.LocalChunkYBase);

            var report = new MeshBuildReport();
            if (plan == null || plan.Edges.Count == 0)
            {
                report.MinCoreGroundClearance = 0f;
                report.MaxCoreGroundClearance = 0f;
                report.MeshHash = ComputeHash(report);
                return report;
            }

            var coreBufs = new Dictionary<Vector2Int, MeshBuffers>();
            var fringeBufs = new Dictionary<Vector2Int, MeshBuffers>();
            var junctionCounts = new Dictionary<Vector2Int, int>();
            var borderSamples = new List<BorderSample>(32);

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

            // 1. Emit every edge ribbon (trimmed at junction hubs, clipped cleanly at chunk borders)
            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var edge = plan.Edges[e];
                if (edge.Points == null || edge.Points.Count < 2) continue;

                var startTrim = junctionTrim.TryGetValue(edge.FromNodeId, out var st) ? st : 0f;
                var endTrim = junctionTrim.TryGetValue(edge.ToNodeId, out var et) ? et : 0f;

                var maxTrim = edge.Length * 0.38f;
                startTrim = Mathf.Min(startTrim, maxTrim);
                endTrim = Mathf.Min(endTrim, maxTrim);

                var trimmed = TrimPolyline(edge.Points, startTrim, endTrim);
                if (trimmed.Count < 2)
                {
                    continue;
                }

                // Record exact ribbon boundary attachment points at incident junctions (so hub meets ribbon vertex-for-vertex!)
                if (startTrim > 0.001f && nodesById.TryGetValue(edge.FromNodeId, out var fromNode))
                {
                    var outDir = (trimmed[1] - trimmed[0]).normalized;
                    RecordSpoke(junctionSpokesCore[edge.FromNodeId], fromNode.Position, trimmed[0], outDir, edge.HalfWidth);
                    RecordSpoke(junctionSpokesFringe[edge.FromNodeId], fromNode.Position, trimmed[0], outDir, edge.HalfWidth + cfg.FringeExtraWidth);
                }

                if (endTrim > 0.001f && nodesById.TryGetValue(edge.ToNodeId, out var toNode))
                {
                    var last = trimmed.Count - 1;
                    var outDir = (trimmed[last - 1] - trimmed[last]).normalized;
                    RecordSpoke(junctionSpokesCore[edge.ToNodeId], toNode.Position, trimmed[last], outDir, edge.HalfWidth);
                    RecordSpoke(junctionSpokesFringe[edge.ToNodeId], toNode.Position, trimmed[last], outDir, edge.HalfWidth + cfg.FringeExtraWidth);
                }

                // Split polyline cleanly at chunk boundaries with shared exact plane points
                var pieces = SplitPolylineByChunkBorders(trimmed, cfg.ChunkSizeMeters);
                var runningDist = startTrim;

                for (var p = 0; p < pieces.Count; p++)
                {
                    var piece = pieces[p];
                    var chunk = piece.Chunk;
                    var coreBuf = GetBuf(coreBufs, chunk);
                    var fringeBuf = GetBuf(fringeBufs, chunk);

                    var capStart = p == 0 && startTrim <= 0.001f;
                    var capEnd = p == pieces.Count - 1 && endTrim <= 0.001f;

                    // Compute shared tangents from the unsplit trimmed polyline at piece boundaries
                    EmitRibbonPiece(
                        coreBuf, piece.Points, piece.StartTangent, piece.EndTangent,
                        edge.HalfWidth, cfg.CoreYOffset, runningDist, edge.Length,
                        edge.Id, capStart, capEnd, isCore: true,
                        chunk, sampleWorldY, cfg, report, borderSamples);

                    EmitRibbonPiece(
                        fringeBuf, piece.Points, piece.StartTangent, piece.EndTangent,
                        edge.HalfWidth + cfg.FringeExtraWidth, cfg.FringeYOffset, runningDist, edge.Length,
                        edge.Id, capStart, capEnd, isCore: false,
                        chunk, sampleWorldY, cfg, report, null);

                    runningDist += PolylineLength(piece.Points);
                }
            }

            // 2. Emit explicit watertight junction hub patches (zero overlap with trimmed ribbons!)
            for (var i = 0; i < plan.Junctions.Count; i++)
            {
                var j = plan.Junctions[i];
                var chunk = WorldToChunk(j.Position, cfg.ChunkSizeMeters);
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

            // 3. Measure chunk-boundary seam continuity between matching border samples
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

            report.MeshHash = ComputeHash(report);
            return report;
        }

        // ------------------------------------------------------------------ Ribbon emission

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
            float distStart,
            float totalEdgeLen,
            string edgeId,
            bool capStart,
            bool capEnd,
            bool isCore,
            Vector2Int chunk,
            Func<float, float, float> sampleWorldY,
            MeshBuildConfig cfg,
            MeshBuildReport report,
            List<BorderSample> borderSamples)
        {
            if (pts.Count < 2) return;

            var chunkOrigin = ChunkOrigin(chunk, cfg);
            var baseVert = buf.Verts.Count;
            var dist = distStart;

            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (i > 0)
                {
                    dist += Vector2.Distance(pts[i - 1], p);
                }

                Vector2 t2;
                if (i == 0 && startTangent2D.sqrMagnitude > 1e-6f) t2 = startTangent2D;
                else if (i == pts.Count - 1 && endTangent2D.sqrMagnitude > 1e-6f) t2 = endTangent2D;
                else if (i == 0) t2 = (pts[1] - pts[0]).normalized;
                else if (i == pts.Count - 1) t2 = (pts[i] - pts[i - 1]).normalized;
                else
                {
                    var a = (pts[i] - pts[i - 1]).normalized;
                    var b = (pts[i + 1] - pts[i]).normalized;
                    t2 = (a + b).sqrMagnitude > 1e-6f ? (a + b).normalized : a;
                }

                // Subtle organic width variation — fades to 0 near ends so junctions/doors stay exact
                var u = totalEdgeLen > 0.01f ? Mathf.Clamp01(dist / totalEdgeLen) : 0.5f;
                var endFade = Mathf.Sin(u * Mathf.PI);
                var wave = Mathf.Sin(dist * 2.1f + (edgeId != null ? edgeId.GetHashCode() * 0.001f : 0f));
                var hw = baseHalfWidth * (1f + wave * cfg.WidthVariationAmplitude * endFade);

                // Side vector on XZ perpendicular to tangent
                var side2 = new Vector2(t2.y, -t2.x); // matches Cross(Up, tangent) = (t.z, 0, -t.x)
                var leftXZ = p - side2 * hw;
                var rightXZ = p + side2 * hw;

                var groundC = sampleWorldY(p.x, p.y);
                var groundL = sampleWorldY(leftXZ.x, leftXZ.y);
                var groundR = sampleWorldY(rightXZ.x, rightXZ.y);

                // Lift slightly if center ridge sits above left/right average to prevent mid-ribbon clipping
                var centerLift = Mathf.Max(0f, groundC - 0.5f * (groundL + groundR));
                var yL = groundL + yOffset + centerLift * 0.5f;
                var yR = groundR + yOffset + centerLift * 0.5f;

                var worldL = new Vector3(leftXZ.x, yL, leftXZ.y);
                var worldR = new Vector3(rightXZ.x, yR, rightXZ.y);

                var nL = SampleTerrainNormal(leftXZ.x, leftXZ.y, sampleWorldY, cfg.NormalSampleDelta);
                var nR = SampleTerrainNormal(rightXZ.x, rightXZ.y, sampleWorldY, cfg.NormalSampleDelta);

                if (isCore)
                {
                    TrackClearance(report, yL - groundL);
                    TrackClearance(report, yR - groundR);

                    if (i > 0)
                    {
                        var prev = pts[i - 1];
                        var hPrev = sampleWorldY(prev.x, prev.y);
                        var horiz = Mathf.Max(0.001f, Vector2.Distance(prev, p));
                        var slopeDeg = Mathf.Atan2(Mathf.Abs(groundC - hPrev), horiz) * Mathf.Rad2Deg;
                        if (slopeDeg > report.MaxSlopeDegrees) report.MaxSlopeDegrees = slopeDeg;
                        if (slopeDeg > cfg.MaxAllowedSlopeDegrees) report.SlopeViolations++;
                    }

                    if (borderSamples != null && (i == 0 || i == pts.Count - 1) && IsOnChunkBorder(p, cfg.ChunkSizeMeters))
                    {
                        borderSamples.Add(new BorderSample
                        {
                            Chunk = chunk,
                            PlaneKey = new Vector2(Mathf.Round(p.x * 100f) * 0.01f, Mathf.Round(p.y * 100f) * 0.01f),
                            WorldPos = new Vector3(p.x, groundC + yOffset, p.y),
                            Tangent = new Vector3(t2.x, 0f, t2.y)
                        });
                    }
                }

                buf.Verts.Add(worldL - chunkOrigin);
                buf.Normals.Add(nL);
                buf.Uvs.Add(new Vector2(0f, dist));

                buf.Verts.Add(worldR - chunkOrigin);
                buf.Normals.Add(nR);
                buf.Uvs.Add(new Vector2(1f, dist));
            }

            // Upward-facing winding: for left=a, right=a+1, nextLeft=b, nextRight=b+1:
            // Cross(nextLeft - left, right - left) = Cross(+tangent, +right) = +Up
            for (var i = 0; i < pts.Count - 1; i++)
            {
                var a = baseVert + i * 2;
                var b = a + 2;
                AddVerifiedTriangle(buf, a, b, a + 1, isCore, report);
                AddVerifiedTriangle(buf, a + 1, b, b + 1, isCore, report);
            }

            // Rounded end caps at free ends (door/dead-end), never at junctions or chunk borders
            if (capStart)
            {
                EmitEndCap(buf, pts[0], -startTangent2D, baseHalfWidth, yOffset, chunkOrigin, sampleWorldY, cfg, isCore, report);
            }

            if (capEnd)
            {
                EmitEndCap(buf, pts[pts.Count - 1], endTangent2D, baseHalfWidth, yOffset, chunkOrigin, sampleWorldY, cfg, isCore, report);
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

            const int steps = 5;
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

        // ------------------------------------------------------------------ Watertight junction hub patch

        private static void RecordSpoke(
            List<JunctionSpoke> list,
            Vector2 junctionCenter,
            Vector2 attachPoint,
            Vector2 outDir,
            float halfWidth)
        {
            if (outDir.sqrMagnitude < 1e-6f) return;
            outDir.Normalize();
            var side = new Vector2(outDir.y, -outDir.x);
            list.Add(new JunctionSpoke
            {
                Angle = Mathf.Atan2(outDir.y, outDir.x),
                OutDir = outDir,
                LeftPt = attachPoint - side * halfWidth,
                RightPt = attachPoint + side * halfWidth,
                HalfWidth = halfWidth
            });
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
            var ring = new List<Vector2>(24);

            if (spokes != null && spokes.Count >= 2)
            {
                // Build ordered counter-clockwise perimeter connecting exact ribbon left/right endpoints
                // with smooth rounded fillets between adjacent outgoing edges.
                var tagged = new List<(float ang, Vector2 pt)>(spokes.Count * 2);
                for (var i = 0; i < spokes.Count; i++)
                {
                    var s = spokes[i];
                    var dL = s.LeftPt - centerXZ;
                    var dR = s.RightPt - centerXZ;
                    tagged.Add((Mathf.Atan2(dL.y, dL.x), s.LeftPt));
                    tagged.Add((Mathf.Atan2(dR.y, dR.x), s.RightPt));
                }

                tagged.Sort((a, b) => a.ang.CompareTo(b.ang));
                for (var i = 0; i < tagged.Count; i++)
                {
                    var cur = tagged[i];
                    var next = tagged[(i + 1) % tagged.Count];
                    ring.Add(cur.pt);

                    var delta = next.ang - cur.ang;
                    if (delta < 0f) delta += Mathf.PI * 2f;

                    // Insert smooth arc points across wide gaps between distinct spokes
                    if (delta > 0.45f)
                    {
                        var steps = Mathf.Max(1, cfg.JunctionArcSteps);
                        for (var k = 1; k < steps; k++)
                        {
                            var t = k / (float)steps;
                            var a = cur.ang + delta * t;
                            var r = Mathf.Lerp(
                                Vector2.Distance(centerXZ, cur.pt),
                                Vector2.Distance(centerXZ, next.pt), t);
                            r = Mathf.Min(r, hubRadius * 1.05f);
                            ring.Add(centerXZ + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
                        }
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

        // ------------------------------------------------------------------ Chunk border splitting

        private static List<ChunkPiece> SplitPolylineByChunkBorders(List<Vector2> pts, float chunkSize)
        {
            var pieces = new List<ChunkPiece>();
            if (pts == null || pts.Count < 2) return pieces;

            // First insert exact intersection points wherever a segment crosses a chunk boundary plane
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

        private static string ComputeHash(MeshBuildReport report)
        {
            var sb = new StringBuilder(1024);
            var ci = CultureInfo.InvariantCulture;
            var keys = new List<Vector2Int>(report.Chunks.Keys);
            keys.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            for (var i = 0; i < keys.Count; i++)
            {
                var d = report.Chunks[keys[i]];
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
