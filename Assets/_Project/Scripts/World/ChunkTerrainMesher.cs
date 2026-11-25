using System;
using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Organic per-chunk terrain from stage masks (visualization only). 0.3.0 polish:
    /// seam-safe SDF domain warp, bilinear material blends, inland hill noise,
    /// grid-based normals (cheap) with world samples only on chunk borders.
    /// </summary>
    public static class ChunkTerrainMesher
    {
        public const int Subdiv = 4;
        public const int MesherRevision = 43; // legacy home = land for SDF neighbors
        private const float IsoLevel = 0f;
        private const int SdfSearchRadius = 3;
        private const float DegenerateAreaEps = 1e-8f;
        private const float WarpAmplitudeCells = 0.22f;
        private const float SandBeltMeters = 2.0f;
        private const float HillAmpNear = 0.04f;
        private const float HillAmpFar = 0.20f;

        public static Mesh Build(
            int chunkX,
            int chunkZ,
            float chunkSize,
            Func<int, int, byte> sampleWorldStage,
            int seed,
            Func<int, int, BiomeType> sampleWorldBiome = null)
        {
            _ = seed;
            var grid = ChunkMaskCodec.GridSize;
            var res = grid * Subdiv;
            var cell = chunkSize / grid;
            var step = cell / Subdiv;

            var worldGridOrigin = -chunkSize * 0.5f;
            var originX = chunkX * chunkSize + worldGridOrigin;
            var originZ = chunkZ * chunkSize + worldGridOrigin;

            var stageCache = new StageCache(sampleWorldStage);
            byte SampleCached(int x, int z) => stageCache.Sample(x, z);
            BiomeType SampleBiome(int x, int z) =>
                sampleWorldBiome != null ? sampleWorldBiome(x, z) : BiomeType.None;

            var padCells = SdfSearchRadius + 3;
            stageCache.Prefetch(
                chunkX * grid - padCells,
                chunkZ * grid - padCells,
                (chunkX + 1) * grid + padCells,
                (chunkZ + 1) * grid + padCells);

            var dim = res + 1;
            var vertCount = dim * dim;
            var sdf = new float[vertCount];
            var heights = new float[vertCount];
            var blends = new Vector4[vertCount];
            var biomeW = new Vector3[vertCount];
            var gridNormals = new Vector3[vertCount];

            for (var jz = 0; jz <= res; jz++)
            {
                for (var jx = 0; jx <= res; jx++)
                {
                    var idx = jz * dim + jx;
                    var wx = originX + jx * step;
                    var wz = originZ + jz * step;
                    SampleSdfField(
                        wx, wz, cell, worldGridOrigin, SampleCached, SampleBiome,
                        out sdf[idx], out heights[idx], out blends[idx], out biomeW[idx]);
                }
            }

            // Finite-difference normals on the dense grid (interior). Border taps use world field.
            for (var jz = 0; jz <= res; jz++)
            {
                for (var jx = 0; jx <= res; jx++)
                {
                    var idx = jz * dim + jx;
                    float hL, hR, hB, hF, sL, sR, sB, sF;
                    if (jx > 0)
                    {
                        hL = heights[idx - 1];
                        sL = sdf[idx - 1];
                    }
                    else
                    {
                        SampleSdfField(originX + jx * step - step, originZ + jz * step, cell, worldGridOrigin,
                            SampleCached, SampleBiome, out sL, out hL, out _, out _);
                    }

                    if (jx < res)
                    {
                        hR = heights[idx + 1];
                        sR = sdf[idx + 1];
                    }
                    else
                    {
                        SampleSdfField(originX + jx * step + step, originZ + jz * step, cell, worldGridOrigin,
                            SampleCached, SampleBiome, out sR, out hR, out _, out _);
                    }

                    if (jz > 0)
                    {
                        hB = heights[idx - dim];
                        sB = sdf[idx - dim];
                    }
                    else
                    {
                        SampleSdfField(originX + jx * step, originZ + jz * step - step, cell, worldGridOrigin,
                            SampleCached, SampleBiome, out sB, out hB, out _, out _);
                    }

                    if (jz < res)
                    {
                        hF = heights[idx + dim];
                        sF = sdf[idx + dim];
                    }
                    else
                    {
                        SampleSdfField(originX + jx * step, originZ + jz * step + step, cell, worldGridOrigin,
                            SampleCached, SampleBiome, out sF, out hF, out _, out _);
                    }

                    var hx = (hR - hL) / (2f * step);
                    var hz = (hF - hB) / (2f * step);
                    var sx = (sR - sL) / (2f * step);
                    var sz = (sF - sB) / (2f * step);
                    var n = new Vector3(-(hx + sx * 0.2f), 1f, -(hz + sz * 0.2f));
                    if (n.sqrMagnitude < 1e-8f)
                    {
                        n = Vector3.up;
                    }
                    else
                    {
                        n.Normalize();
                    }

                    gridNormals[idx] = n;
                }
            }

            var vertices = new List<Vector3>(vertCount * 2);
            var normals = new List<Vector3>(vertCount * 2);
            var uvs = new List<Vector2>(vertCount * 2);
            var colors = new List<Color>(vertCount * 2);
            var topTris = new List<int>(res * res * 6);
            var sideTris = new List<int>(res * res * 6);

            var cornerVerts = new Dictionary<long, int>(vertCount);
            var edgeVerts = new Dictionary<long, int>(vertCount);

            for (var jz = 0; jz < res; jz++)
            {
                for (var jx = 0; jx < res; jx++)
                {
                    EmitMarchingSquare(
                        jx, jz, res, step, originX, originZ,
                        sdf, heights, blends, biomeW, gridNormals,
                        cornerVerts, edgeVerts,
                        vertices, normals, uvs, colors,
                        topTris, sideTris);
                }
            }

            var mesh = new Mesh
            {
                name = $"GrownTerrain_{chunkX}_{chunkZ}",
                indexFormat = vertices.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(topTris, 0, true);
            mesh.SetTriangles(sideTris, 1, true);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        private static void EmitMarchingSquare(
            int jx,
            int jz,
            int res,
            float step,
            float originX,
            float originZ,
            float[] sdf,
            float[] heights,
            Vector4[] blends,
            Vector3[] biomeW,
            Vector3[] gridNormals,
            Dictionary<long, int> cornerVerts,
            Dictionary<long, int> edgeVerts,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> topTris,
            List<int> sideTris)
        {
            var dim = res + 1;
            var i00 = jz * dim + jx;
            var i10 = i00 + 1;
            var i01 = i00 + dim;
            var i11 = i01 + 1;

            var s00 = sdf[i00];
            var s10 = sdf[i10];
            var s01 = sdf[i01];
            var s11 = sdf[i11];

            var c0 = s00 >= IsoLevel ? 1 : 0;
            var c1 = s10 >= IsoLevel ? 1 : 0;
            var c2 = s11 >= IsoLevel ? 1 : 0;
            var c3 = s01 >= IsoLevel ? 1 : 0;
            var mask = c0 | (c1 << 1) | (c2 << 2) | (c3 << 3);
            if (mask == 0)
            {
                return;
            }

            int Corner(int ix, int iz, int src)
            {
                var key = PackCorner(ix, iz);
                if (cornerVerts.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var wx = originX + ix * step;
                var wz = originZ + iz * step;
                // Mesh is chunk-local; GO sits on ChunkOrigin so world = local + origin.
                var p = new Vector3(wx - originX, heights[src], wz - originZ);
                var col = BiomeSampling.TintForWeights(
                    biomeW[src], BlendColor(blends[src], SdfToOcc(sdf[src])));
                var vi = AddVertex(
                    vertices, normals, uvs, colors, p, col, gridNormals[src], wx, wz);
                cornerVerts[key] = vi;
                return vi;
            }

            int Edge(int ax, int az, int aSrc, int bx, int bz, int bSrc)
            {
                int x0 = ax, z0 = az;
                int x1 = bx, z1 = bz;
                if (x1 < x0 || (x1 == x0 && z1 < z0))
                {
                    x0 = bx;
                    z0 = bz;
                    x1 = ax;
                    z1 = az;
                }

                var key = PackEdge(x0, z0, x1, z1);
                if (edgeVerts.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var sa = sdf[aSrc];
                var sb = sdf[bSrc];
                var t = InverseLerpSafe(sa, sb, IsoLevel);
                var wx = Mathf.Lerp(originX + ax * step, originX + bx * step, t);
                var wz = Mathf.Lerp(originZ + az * step, originZ + bz * step, t);
                var hy = Mathf.Lerp(heights[aSrc], heights[bSrc], t);
                hy = Mathf.Min(hy, 0.18f);
                var col = Color.Lerp(
                    BiomeSampling.TintForWeights(biomeW[aSrc], BlendColor(blends[aSrc], SdfToOcc(sa))),
                    BiomeSampling.TintForWeights(biomeW[bSrc], BlendColor(blends[bSrc], SdfToOcc(sb))),
                    t);
                // Soft sand tint on the shore lip.
                col = Color.Lerp(col, new Color(0.78f, 0.68f, 0.45f), 0.35f);
                var nrm = Vector3.Slerp(gridNormals[aSrc], gridNormals[bSrc], t).normalized;
                if (nrm.sqrMagnitude < 1e-8f)
                {
                    nrm = Vector3.up;
                }

                var p = new Vector3(wx - originX, hy, wz - originZ);
                var vi = AddVertex(vertices, normals, uvs, colors, p, col, nrm, wx, wz);
                edgeVerts[key] = vi;
                return vi;
            }

            void Tri(int a, int b, int c)
            {
                if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c)
                {
                    return;
                }

                var pa = vertices[a];
                var pb = vertices[b];
                var pc = vertices[c];
                var area2 = Vector3.Cross(pb - pa, pc - pa).sqrMagnitude;
                if (area2 < DegenerateAreaEps)
                {
                    return;
                }

                topTris.Add(a);
                topTris.Add(b);
                topTris.Add(c);
            }

            void Fan(params int[] idx)
            {
                for (var i = 1; i + 1 < idx.Length; i++)
                {
                    Tri(idx[0], idx[i + 1], idx[i]);
                }
            }

            void Skirt(int topA, int topB)
            {
                if (topA < 0 || topB < 0 || topA == topB)
                {
                    return;
                }

                var pa = vertices[topA];
                var pb = vertices[topB];
                if ((pa - pb).sqrMagnitude < 1e-10f)
                {
                    return;
                }

                const float yBed = -0.14f;
                var ox = pb.z - pa.z;
                var oz = pa.x - pb.x;
                var len = Mathf.Sqrt(ox * ox + oz * oz);
                if (len < 1e-5f)
                {
                    return;
                }

                ox /= len;
                oz /= len;
                var nrm = new Vector3(ox, 0f, oz);
                var sand = new Color(0.74f, 0.64f, 0.42f);
                var cliff = Color.Lerp(new Color(0.42f, 0.28f, 0.14f), sand, 0.35f);
                var bed = new Color(0.32f, 0.22f, 0.12f);

                var iA = vertices.Count;
                vertices.Add(pa);
                normals.Add(nrm);
                uvs.Add(new Vector2(pa.x * 0.28f, pa.y));
                colors.Add(cliff);

                var iB = vertices.Count;
                vertices.Add(pb);
                normals.Add(nrm);
                uvs.Add(new Vector2(pb.x * 0.28f, pb.y));
                colors.Add(cliff);

                var iC = vertices.Count;
                vertices.Add(new Vector3(pb.x, yBed, pb.z));
                normals.Add(nrm);
                uvs.Add(new Vector2(pb.x * 0.28f, yBed));
                colors.Add(bed);

                var iD = vertices.Count;
                vertices.Add(new Vector3(pa.x, yBed, pa.z));
                normals.Add(nrm);
                uvs.Add(new Vector2(pa.x * 0.28f, yBed));
                colors.Add(bed);

                sideTris.Add(iA);
                sideTris.Add(iB);
                sideTris.Add(iC);
                sideTris.Add(iA);
                sideTris.Add(iC);
                sideTris.Add(iD);
            }

            if (mask == 15)
            {
                var v00 = Corner(jx, jz, i00);
                var v10 = Corner(jx + 1, jz, i10);
                var v01 = Corner(jx, jz + 1, i01);
                var v11 = Corner(jx + 1, jz + 1, i11);
                Tri(v00, v01, v11);
                Tri(v00, v11, v10);
                return;
            }

            var p0 = c0 != 0 ? Corner(jx, jz, i00) : -1;
            var p1 = c1 != 0 ? Corner(jx + 1, jz, i10) : -1;
            var p2 = c2 != 0 ? Corner(jx + 1, jz + 1, i11) : -1;
            var p3 = c3 != 0 ? Corner(jx, jz + 1, i01) : -1;

            var e0 = c0 != c1 ? Edge(jx, jz, i00, jx + 1, jz, i10) : -1;
            var e1 = c1 != c2 ? Edge(jx + 1, jz, i10, jx + 1, jz + 1, i11) : -1;
            var e2 = c2 != c3 ? Edge(jx + 1, jz + 1, i11, jx, jz + 1, i01) : -1;
            var e3 = c3 != c0 ? Edge(jx, jz + 1, i01, jx, jz, i00) : -1;

            var center = 0.25f * (s00 + s10 + s01 + s11);
            var centerLand = center >= IsoLevel;

            switch (mask)
            {
                case 1: Fan(p0, e0, e3); Skirt(e0, e3); break;
                case 2: Fan(p1, e1, e0); Skirt(e1, e0); break;
                case 3: Fan(p0, p1, e1, e3); Skirt(e1, e3); break;
                case 4: Fan(p2, e2, e1); Skirt(e2, e1); break;
                case 5:
                    if (centerLand)
                    {
                        Fan(p0, e0, e1, p2, e2, e3);
                        Skirt(e0, e1);
                        Skirt(e2, e3);
                    }
                    else
                    {
                        Fan(p0, e0, e3);
                        Fan(p2, e2, e1);
                        Skirt(e0, e3);
                        Skirt(e2, e1);
                    }

                    break;
                case 6: Fan(p1, p2, e2, e0); Skirt(e2, e0); break;
                case 7: Fan(p0, p1, p2, e2, e3); Skirt(e2, e3); break;
                case 8: Fan(p3, e3, e2); Skirt(e3, e2); break;
                case 9: Fan(p0, e0, e2, p3); Skirt(e0, e2); break;
                case 10:
                    if (centerLand)
                    {
                        Fan(p1, e1, e2, p3, e3, e0);
                        Skirt(e1, e2);
                        Skirt(e3, e0);
                    }
                    else
                    {
                        Fan(p1, e1, e0);
                        Fan(p3, e3, e2);
                        Skirt(e1, e0);
                        Skirt(e3, e2);
                    }

                    break;
                case 11: Fan(p0, p1, e1, e2, p3); Skirt(e1, e2); break;
                case 12: Fan(p2, p3, e3, e1); Skirt(e3, e1); break;
                case 13: Fan(p0, e0, e1, p2, p3); Skirt(e0, e1); break;
                case 14: Fan(p1, p2, p3, e3, e0); Skirt(e3, e0); break;
            }
        }

        /// <summary>
        /// Samples the exact visual height field used by Build at a world-space XZ point.
        /// Returned Y is in world coordinates (including GrowthWorldPlacement.VisualYBase).
        /// </summary>
        public static float SampleVisualHeight(
            float worldX,
            float worldZ,
            Func<int, int, byte> sampleWorldStage,
            Func<int, int, BiomeType> sampleWorldBiome = null,
            float chunkSize = IslandChunk.DefaultSizeMeters)
        {
            if (sampleWorldStage == null || chunkSize <= 0f)
            {
                return GrowthWorldPlacement.VisualYBase;
            }

            SampleSdfField(
                worldX,
                worldZ,
                chunkSize / ChunkMaskCodec.GridSize,
                -chunkSize * 0.5f,
                sampleWorldStage,
                sampleWorldBiome ?? ((_, _) => BiomeType.None),
                out _,
                out var height,
                out _,
                out _);
            return GrowthWorldPlacement.VisualYBase + height;
        }

        private static void SampleSdfField(
            float wx,
            float wz,
            float cell,
            float worldGridOrigin,
            Func<int, int, byte> sampleWorldStage,
            Func<int, int, BiomeType> sampleWorldBiome,
            out float signedDistance,
            out float height,
            out Vector4 blend,
            out Vector3 biomeWeights)
        {
            // Seam-safe domain warp: displace the SAMPLE point only (vertex XZ stay on grid).
            DomainWarp(wx, wz, cell, out var swx, out var swz);

            var fx = (swx - worldGridOrigin) / cell - 0.5f;
            var fz = (swz - worldGridOrigin) / cell - 0.5f;
            var ix = Mathf.FloorToInt(fx);
            var iz = Mathf.FloorToInt(fz);

            var here = sampleWorldStage(ix, iz);
            var inside = here != (byte)CellStage.Void;

            var bestOpp = float.MaxValue;
            var foundLand = false;

            for (var dz = -SdfSearchRadius; dz <= SdfSearchRadius; dz++)
            {
                for (var dx = -SdfSearchRadius; dx <= SdfSearchRadius; dx++)
                {
                    var cx = ix + dx;
                    var cz = iz + dz;
                    var stage = sampleWorldStage(cx, cz);
                    var land = stage != (byte)CellStage.Void;
                    var dist = DistanceToCellAabb(fx, fz, cx, cz);

                    if (land)
                    {
                        foundLand = true;
                    }

                    if (land != inside && dist < bestOpp)
                    {
                        bestOpp = dist;
                    }
                }
            }

            if (bestOpp > SdfSearchRadius * 1.5f)
            {
                bestOpp = SdfSearchRadius * 1.5f;
            }

            signedDistance = (inside ? bestOpp : -bestOpp) * cell;
            biomeWeights = BiomeSampling.SoftWeights(fx, fz, sampleWorldBiome, signedDistance);

            // Bilinear blend of the four cells around the warped sample (smooth stage colors).
            SampleBilinearVisual(fx, fz, sampleWorldStage, out var baseHeight, out blend);

            if (!foundLand && !inside)
            {
                height = -0.04f;
                blend = new Vector4(0.05f, 0.05f, 0.05f, 0.9f);
                return;
            }

            // Wider soft sand belt toward water.
            var sandT = 1f - Mathf.SmoothStep(0f, SandBeltMeters, Mathf.Max(0f, signedDistance));
            if (signedDistance >= 0f)
            {
                blend = Vector4.Lerp(blend, new Vector4(0.08f, 0.08f, 0.05f, 0.9f), sandT * 0.85f);
            }
            else
            {
                blend = new Vector4(0.05f, 0.05f, 0.05f, 0.95f);
            }

            var shoreLift = Mathf.SmoothStep(0f, cell * 1.6f, Mathf.Max(0f, signedDistance));
            height = Mathf.Lerp(0.10f, baseHeight, shoreLift);

            if (signedDistance > 0f)
            {
                // Inland hills — amplitude grows with distance from shore (world XZ only).
                var inland = Mathf.SmoothStep(cell * 0.6f, cell * 3.5f, signedDistance);
                var amp = Mathf.Lerp(HillAmpNear, HillAmpFar, inland);
                // Forest a bit taller, coastal flatter.
                amp *= 0.85f + biomeWeights.z * 0.35f - biomeWeights.x * 0.25f;
                height += HillNoise(wx, wz) * amp;
                height += (ValueNoise(wx * 0.9f + 3.1f, wz * 0.9f - 1.7f) - 0.5f) * 0.04f * inland;
            }
            else
            {
                height = -0.04f;
            }
        }

        private static void DomainWarp(float wx, float wz, float cell, out float swx, out float swz)
        {
            var amp = cell * WarpAmplitudeCells;
            var n0 = ValueNoise(wx * 0.11f + 17.3f, wz * 0.11f - 9.1f);
            var n1 = ValueNoise(wx * 0.11f - 4.7f, wz * 0.11f + 22.5f);
            var n2 = ValueNoise(wx * 0.23f + 2.2f, wz * 0.23f + 8.8f);
            var n3 = ValueNoise(wx * 0.23f - 11.4f, wz * 0.23f - 3.3f);
            swx = wx + (n0 - 0.5f) * 2f * amp + (n2 - 0.5f) * amp * 0.45f;
            swz = wz + (n1 - 0.5f) * 2f * amp + (n3 - 0.5f) * amp * 0.45f;
        }

        private static float HillNoise(float wx, float wz)
        {
            var a = ValueNoise(wx * 0.055f + 1.3f, wz * 0.055f + 4.9f);
            var b = ValueNoise(wx * 0.12f - 7.2f, wz * 0.12f + 2.1f);
            return (a - 0.5f) * 1.4f + (b - 0.5f) * 0.55f;
        }

        private static void SampleBilinearVisual(
            float fx,
            float fz,
            Func<int, int, byte> sampleWorldStage,
            out float height,
            out Vector4 blend)
        {
            var x0 = Mathf.FloorToInt(fx);
            var z0 = Mathf.FloorToInt(fz);
            var tx = fx - x0;
            var tz = fz - z0;

            SampleCellVisual(sampleWorldStage(x0, z0), out var h00, out var b00);
            SampleCellVisual(sampleWorldStage(x0 + 1, z0), out var h10, out var b10);
            SampleCellVisual(sampleWorldStage(x0, z0 + 1), out var h01, out var b01);
            SampleCellVisual(sampleWorldStage(x0 + 1, z0 + 1), out var h11, out var b11);

            var h0 = Mathf.Lerp(h00, h10, tx);
            var h1 = Mathf.Lerp(h01, h11, tx);
            height = Mathf.Lerp(h0, h1, tz);

            var b0 = Vector4.Lerp(b00, b10, tx);
            var b1 = Vector4.Lerp(b01, b11, tx);
            blend = Vector4.Lerp(b0, b1, tz);
        }

        private static float DistanceToCellAabb(float fx, float fz, int cx, int cz)
        {
            var px = fx + 0.5f;
            var pz = fz + 0.5f;
            var minX = cx;
            var maxX = cx + 1;
            var minZ = cz;
            var maxZ = cz + 1;
            var dx = px < minX ? minX - px : (px > maxX ? px - maxX : 0f);
            var dz = pz < minZ ? minZ - pz : (pz > maxZ ? pz - maxZ : 0f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static void SampleCellVisual(byte stage, out float height, out Vector4 blend)
        {
            // blend = (soil, grass, moss, sand)
            switch ((CellStage)stage)
            {
                case CellStage.Roots:
                    height = 0.26f;
                    blend = new Vector4(0.75f, 0.08f, 0.05f, 0.25f);
                    break;
                case CellStage.Soil:
                    height = 0.40f;
                    blend = new Vector4(0.65f, 0.22f, 0.08f, 0.12f);
                    break;
                case CellStage.Moss:
                    height = 0.52f;
                    blend = new Vector4(0.18f, 0.28f, 0.62f, 0.05f);
                    break;
                case CellStage.Vegetation:
                    height = 0.64f;
                    blend = new Vector4(0.10f, 0.72f, 0.22f, 0.04f);
                    break;
                default:
                    height = -0.04f;
                    blend = new Vector4(0.05f, 0.05f, 0.05f, 0.9f);
                    break;
            }
        }

        private static float SdfToOcc(float sdfMetres) =>
            Mathf.Clamp01(sdfMetres / SandBeltMeters + 0.35f);

        private static int AddVertex(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            Vector3 localPos,
            Color c,
            Vector3 n,
            float worldX,
            float worldZ)
        {
            var i = vertices.Count;
            vertices.Add(localPos);
            normals.Add(n);
            // World UVs keep texture continuity across chunk seams.
            uvs.Add(new Vector2(worldX * 0.28f, worldZ * 0.28f));
            colors.Add(c);
            return i;
        }

        private static Color BlendColor(Vector4 w, float occupancy)
        {
            var soil = new Color(0.44f, 0.30f, 0.16f);
            var grass = new Color(0.28f, 0.55f, 0.24f);
            var moss = new Color(0.22f, 0.42f, 0.22f);
            var sand = new Color(0.78f, 0.68f, 0.45f);
            var sum = Mathf.Max(0.001f, w.x + w.y + w.z + w.w);
            var c = (soil * w.x + grass * w.y + moss * w.z + sand * w.w) / sum;
            if (occupancy < 0.65f)
            {
                c = Color.Lerp(sand, c, Mathf.InverseLerp(0.2f, 0.65f, occupancy));
            }

            return c;
        }

        private static float InverseLerpSafe(float a, float b, float v)
        {
            var d = b - a;
            if (Mathf.Abs(d) < 1e-6f)
            {
                return 0.5f;
            }

            return Mathf.Clamp01((v - a) / d);
        }

        private static long PackCorner(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static long PackEdge(int x0, int z0, int x1, int z1) =>
            ((long)x0 << 48) ^ ((long)(uint)z0 << 32) ^ ((long)(uint)x1 << 16) ^ (uint)z1;

        /// <summary>Smooth value noise in world XZ (same on both sides of a chunk seam).</summary>
        private static float ValueNoise(float x, float z)
        {
            var x0 = Mathf.FloorToInt(x);
            var z0 = Mathf.FloorToInt(z);
            var tx = Smooth01(x - x0);
            var tz = Smooth01(z - z0);
            var a = Hash01World(x0, z0);
            var b = Hash01World(x0 + 1, z0);
            var c = Hash01World(x0, z0 + 1);
            var d = Hash01World(x0 + 1, z0 + 1);
            var ab = Mathf.Lerp(a, b, tx);
            var cd = Mathf.Lerp(c, d, tx);
            return Mathf.Lerp(ab, cd, tz);
        }

        private static float Smooth01(float t) => t * t * (3f - 2f * t);

        private static float Hash01World(int x, int z)
        {
            unchecked
            {
                var h = (uint)(x * 374761393 + z * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        private static float Hash01World(float wx, float wz) =>
            Hash01World(Mathf.RoundToInt(wx * 10f), Mathf.RoundToInt(wz * 10f));

        private sealed class StageCache
        {
            private readonly Func<int, int, byte> _source;
            private readonly Dictionary<long, byte> _map = new(1024);

            public StageCache(Func<int, int, byte> source) => _source = source;

            public void Prefetch(int minX, int minZ, int maxXExclusive, int maxZExclusive)
            {
                for (var z = minZ; z < maxZExclusive; z++)
                {
                    for (var x = minX; x < maxXExclusive; x++)
                    {
                        Sample(x, z);
                    }
                }
            }

            public byte Sample(int x, int z)
            {
                var key = ((long)x << 32) ^ (uint)z;
                if (_map.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var value = _source(x, z);
                _map[key] = value;
                return value;
            }
        }
    }
}
