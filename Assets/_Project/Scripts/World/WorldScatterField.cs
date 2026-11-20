using System;
using System.Collections.Generic;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Deterministic world-space blue-noise scatter. Candidate sites are anchored to a
    /// world lattice, jittered from integer world coordinates, then filtered by a
    /// deterministic priority rule. Chunk boundaries never participate in generation.
    /// </summary>
    public static class WorldScatterField
    {
        public const float DefaultTreeMinimumSpacing = 3.5f;
        public const float DefaultCandidateSpacing = 2.2f;
        private const float CandidateJitter = 0.44f;

        public readonly struct ScatterPoint
        {
            public readonly int GridX;
            public readonly int GridZ;
            public readonly Vector2 Position;
            public readonly uint Priority;

            public ScatterPoint(int gridX, int gridZ, Vector2 position, uint priority)
            {
                GridX = gridX;
                GridZ = gridZ;
                Position = position;
                Priority = priority;
            }
        }

        /// <summary>
        /// Generates points owned by bounds. Candidate evaluation includes a halo around
        /// bounds, so accepted points are independent of which chunk requested them.
        /// densityAt returns a probability in [0,1]; isValid applies hard world masks.
        /// </summary>
        public static void Generate(
            Rect bounds,
            int worldSeed,
            float minimumSpacing,
            float candidateSpacing,
            Func<Vector2, float> densityAt,
            Func<Vector2, bool> isValid,
            IReadOnlyList<Vector2> blockedPoints,
            List<ScatterPoint> results)
        {
            if (results == null)
            {
                return;
            }

            results.Clear();
            if (densityAt == null || isValid == null ||
                minimumSpacing <= 0f || candidateSpacing <= 0f ||
                bounds.width <= 0f || bounds.height <= 0f)
            {
                return;
            }

            var halo = minimumSpacing + candidateSpacing;
            var minGridX = Mathf.FloorToInt((bounds.xMin - halo) / candidateSpacing) - 1;
            var maxGridX = Mathf.CeilToInt((bounds.xMax + halo) / candidateSpacing) + 1;
            var minGridZ = Mathf.FloorToInt((bounds.yMin - halo) / candidateSpacing) - 1;
            var maxGridZ = Mathf.CeilToInt((bounds.yMax + halo) / candidateSpacing) + 1;
            var neighborRange = Mathf.CeilToInt(minimumSpacing / candidateSpacing) + 2;
            var minSpacingSq = minimumSpacing * minimumSpacing;

            for (var gz = minGridZ; gz <= maxGridZ; gz++)
            {
                for (var gx = minGridX; gx <= maxGridX; gx++)
                {
                    if (!TryCandidate(gx, gz, worldSeed, candidateSpacing, densityAt, isValid,
                            out var point, out var priority))
                    {
                        continue;
                    }

                    if (point.x < bounds.xMin || point.x >= bounds.xMax ||
                        point.y < bounds.yMin || point.y >= bounds.yMax)
                    {
                        continue;
                    }

                    var blocked = false;
                    if (blockedPoints != null)
                    {
                        for (var i = 0; i < blockedPoints.Count; i++)
                        {
                            if ((blockedPoints[i] - point).sqrMagnitude < minSpacingSq)
                            {
                                blocked = true;
                                break;
                            }
                        }
                    }

                    if (blocked)
                    {
                        continue;
                    }

                    // A candidate survives only if it is the deterministic local minimum
                    // among all eligible candidates inside the exclusion radius. This gives
                    // a stable maximal independent set without per-chunk mutable state.
                    for (var dz = -neighborRange; dz <= neighborRange && !blocked; dz++)
                    {
                        for (var dx = -neighborRange; dx <= neighborRange; dx++)
                        {
                            if (dx == 0 && dz == 0)
                            {
                                continue;
                            }

                            var nx = gx + dx;
                            var nz = gz + dz;
                            if (!TryCandidate(nx, nz, worldSeed, candidateSpacing, densityAt, isValid,
                                    out var neighbor, out var neighborPriority))
                            {
                                continue;
                            }

                            if ((neighbor - point).sqrMagnitude >= minSpacingSq)
                            {
                                continue;
                            }

                            if (ComesBefore(neighborPriority, nz, nx, priority, gz, gx))
                            {
                                blocked = true;
                                break;
                            }
                        }
                    }

                    if (!blocked)
                    {
                        results.Add(new ScatterPoint(gx, gz, point, priority));
                    }
                }
            }
        }

        public static uint Hash(int seed, int x, int z, uint salt = 0u)
        {
            unchecked
            {
                var h = (uint)seed;
                h ^= (uint)x * 0x9E3779B9u;
                h = RotateLeft(h, 13);
                h ^= (uint)z * 0x85EBCA6Bu;
                h ^= salt * 0xC2B2AE35u;
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>Continuous deterministic value noise in world space, with smooth interpolation.</summary>
        public static float SampleValueNoise(float worldX, float worldZ, float scale, int seed, uint salt = 0u)
        {
            if (scale <= 0f)
            {
                return 0.5f;
            }

            var x = worldX / scale;
            var z = worldZ / scale;
            var x0 = Mathf.FloorToInt(x);
            var z0 = Mathf.FloorToInt(z);
            var tx = Smooth01(x - x0);
            var tz = Smooth01(z - z0);
            var a = Hash01(seed, x0, z0, salt);
            var b = Hash01(seed, x0 + 1, z0, salt);
            var c = Hash01(seed, x0, z0 + 1, salt);
            var d = Hash01(seed, x0 + 1, z0 + 1, salt);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        private static float Smooth01(float value)
        {
            return value * value * (3f - 2f * value);
        }

        public static float Hash01(int seed, int x, int z, uint salt = 0u)
        {
            return (Hash(seed, x, z, salt) & 0x00FFFFFFu) / 16777215f;
        }

        private static bool TryCandidate(
            int gx,
            int gz,
            int seed,
            float spacing,
            Func<Vector2, float> densityAt,
            Func<Vector2, bool> isValid,
            out Vector2 point,
            out uint priority)
        {
            var jitterX = (Hash01(seed, gx, gz, 11u) * 2f - 1f) * CandidateJitter;
            var jitterZ = (Hash01(seed, gx, gz, 29u) * 2f - 1f) * CandidateJitter;
            point = new Vector2((gx + 0.5f + jitterX) * spacing, (gz + 0.5f + jitterZ) * spacing);
            priority = Hash(seed, gx, gz, 47u);

            var density = Mathf.Clamp01(densityAt(point));
            if (Hash01(seed, gx, gz, 71u) > density)
            {
                return false;
            }

            return isValid(point);
        }

        private static bool ComesBefore(
            uint aPriority, int aZ, int aX,
            uint bPriority, int bZ, int bX)
        {
            if (aPriority != bPriority)
            {
                return aPriority < bPriority;
            }

            if (aZ != bZ)
            {
                return aZ < bZ;
            }

            return aX < bX;
        }

        private static uint RotateLeft(uint value, int bits)
        {
            return (value << bits) | (value >> (32 - bits));
        }
    }
}
