using System;
using System.Collections.Generic;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Deterministic world-space scatter field.
    /// Candidate cells are anchored to world coordinates, never to chunk coordinates,
    /// so adjacent chunks evaluate the same candidates on both sides of their seam.
    /// </summary>
    public static class WorldScatterField
    {
        /// <summary>
        /// Generates a Poisson-like set of world points inside bounds.
        /// densityAt returns a probability in [0,1]; isAllowed applies gameplay masks/reserves.
        /// The callbacks must be pure functions of world position.
        /// </summary>
        public static List<Vector2> Generate(
            Rect bounds,
            int worldSeed,
            float minDistance,
            Func<Vector2, float> densityAt,
            Func<Vector2, bool> isAllowed)
        {
            var result = new List<Vector2>();
            if (bounds.width <= 0f || bounds.height <= 0f ||
                minDistance <= 0.01f || densityAt == null || isAllowed == null)
            {
                return result;
            }

            // One deterministic candidate per world-space lattice cell. The lattice is
            // only an implementation detail; jitter and neighbor rejection hide its pattern.
            var cellSize = minDistance;
            var minX = Mathf.FloorToInt(bounds.xMin / cellSize) - 1;
            var maxX = Mathf.FloorToInt(bounds.xMax / cellSize) + 1;
            var minZ = Mathf.FloorToInt(bounds.yMin / cellSize) - 1;
            var maxZ = Mathf.FloorToInt(bounds.yMax / cellSize) + 1;

            for (var z = minZ; z <= maxZ; z++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var point = Candidate(x, z, worldSeed, cellSize);
                    if (!bounds.Contains(point) || !IsAccepted(x, z, point, worldSeed,
                            cellSize, minDistance, densityAt, isAllowed))
                    {
                        continue;
                    }

                    result.Add(point);
                }
            }

            return result;
        }

        private static bool IsAccepted(
            int cellX,
            int cellZ,
            Vector2 point,
            int seed,
            float cellSize,
            float minDistance,
            Func<Vector2, float> densityAt,
            Func<Vector2, bool> isAllowed)
        {
            var density = Mathf.Clamp01(densityAt(point));
            if (density <= 0f || !isAllowed(point) ||
                Hash01(seed, cellX, cellZ, 11) > density)
            {
                return false;
            }

            var minDistanceSq = minDistance * minDistance;
            var priority = Hash01(seed, cellX, cellZ, 29);

            // Check a 5x5 neighborhood. With one candidate per minDistance-sized
            // cell, this covers every candidate that can violate the spacing radius.
            for (var dz = -2; dz <= 2; dz++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    var nx = cellX + dx;
                    var nz = cellZ + dz;
                    var neighbor = Candidate(nx, nz, seed, cellSize);
                    var delta = neighbor - point;
                    if (delta.sqrMagnitude >= minDistanceSq)
                    {
                        continue;
                    }

                    var neighborDensity = Mathf.Clamp01(densityAt(neighbor));
                    if (neighborDensity <= 0f || !isAllowed(neighbor) ||
                        Hash01(seed, nx, nz, 11) > neighborDensity)
                    {
                        continue;
                    }

                    if (Hash01(seed, nx, nz, 29) < priority)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static Vector2 Candidate(int cellX, int cellZ, int seed, float cellSize)
        {
            var x = (cellX + Hash01(seed, cellX, cellZ, 41)) * cellSize;
            var z = (cellZ + Hash01(seed, cellX, cellZ, 43)) * cellSize;
            return new Vector2(x, z);
        }

        private static float Hash01(int seed, int x, int z, int channel)
        {
            unchecked
            {
                var h = (uint)seed;
                h ^= (uint)x * 0x9E3779B9u;
                h = (h ^ (h >> 16)) * 0x85EBCA6Bu;
                h ^= (uint)z * 0xC2B2AE35u;
                h = (h ^ (h >> 13)) * 0x27D4EB2Fu;
                h ^= (uint)channel * 0x165667B1u;
                h ^= h >> 16;
                return (h & 0x00FFFFFFu) / 16777215f;
            }
        }
    }
}
