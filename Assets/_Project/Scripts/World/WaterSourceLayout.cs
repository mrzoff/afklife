using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Stage 11E: authored home pond pose + XZ reserve so nature/decor cannot cover WaterSource.
    /// Keep in sync with <see cref="IslandPrototypeBuilder"/> CreateWaterSource.
    /// </summary>
    public static class WaterSourceLayout
    {
        public static readonly Vector3 LocalPosition = new Vector3(1.8f, 1.90f, -1.4f);
        public static readonly Vector3 LocalScale = new Vector3(2.2f, 0.08f, 1.6f);

        /// <summary>Unity cylinder radius 0.5 times LocalScale.x / z. The blue patch itself.</summary>
        public const float WaterHalfExtentX = 1.1f;
        public const float WaterHalfExtentZ = 0.8f;

        /// <summary>JapaneseCedar box collider, the wider horizontal side.</summary>
        public const float CedarTrunkHorizontal = 0.68f;

        public const float MinTrunkRadius = 0.35f;

        /// <summary>Hut / path tile half-extent. Larger than a trunk, far smaller than a crown.</summary>
        public const float SettlementFootprintRadius = 0.7f;

        private static bool _centerCached;
        private static Vector3 _cachedCenter;

        public static float TrunkRadius(float instanceScale)
        {
            return Mathf.Max(MinTrunkRadius, CedarTrunkHorizontal * Mathf.Abs(instanceScale));
        }

        /// <summary>Drop the cached pond center after the authored WaterSource is moved.</summary>
        public static void ClearCenterCache()
        {
            _centerCached = false;
        }

        public static Vector3 GetWorldCenter()
        {
            if (_centerCached)
            {
                return _cachedCenter;
            }

            _cachedCenter = ResolveWorldCenter();
            _centerCached = true;
            return _cachedCenter;
        }

        private static Vector3 ResolveWorldCenter()
        {
            var pond = GameObject.Find("WaterSource");
            if (pond != null)
            {
                return pond.transform.position;
            }

            var island = GameObject.Find("Island");
            if (island != null)
            {
                return island.transform.TransformPoint(LocalPosition);
            }

            return LocalPosition;
        }

        public static bool IsInsideReserveXZ(Vector3 worldPos)
        {
            return BlocksTrunk(worldPos.x, worldPos.z, MinTrunkRadius);
        }

        public static bool IsInsideReserveXZ(float worldX, float worldZ)
        {
            return BlocksTrunk(worldX, worldZ, MinTrunkRadius);
        }

        public static bool BlocksSettlement(float worldX, float worldZ)
        {
            return BlocksTrunk(worldX, worldZ, SettlementFootprintRadius);
        }

        public static bool BlocksSettlementCell(int chunkX, int chunkZ, int cellIndex)
        {
            var center = GrowthWorldPlacement.CellCenterWorld(chunkX, chunkZ, cellIndex);
            return BlocksSettlement(center.x, center.z);
        }

        /// <summary>Clear settlement stages whose cell center sits in the pond. Save version stays 8.</summary>
        public static int ScrubSettlementInPond(GameSaveData save)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return 0;
            }

            var cleared = 0;
            for (var c = 0; c < save.worldGrowth.chunks.Count; c++)
            {
                var growth = save.worldGrowth.chunks[c];
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                var dirty = false;
                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] == (byte)SettlementStage.None ||
                        !BlocksSettlementCell(growth.chunkX, growth.chunkZ, i))
                    {
                        continue;
                    }

                    settle[i] = (byte)SettlementStage.None;
                    dirty = true;
                    cleared++;
                }

                if (dirty)
                {
                    growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);
                }
            }

            return cleared;
        }

        /// <summary>
        /// If the trunk overlaps the pond, step it outward along the existing offset.
        /// A trunk already on the bank is returned unchanged. Crown overhang is ignored.
        /// </summary>
        public static Vector2 PushTrunkOutOfWater(float worldX, float worldZ, float trunkRadius)
        {
            if (!BlocksTrunk(worldX, worldZ, trunkRadius))
            {
                return new Vector2(worldX, worldZ);
            }

            var center = GetWorldCenter();
            var away = new Vector2(worldX - center.x, worldZ - center.z);
            if (away.sqrMagnitude < 0.0001f)
            {
                away = Vector2.right;
            }

            away.Normalize();
            var distance = 0.2f;
            while (distance <= 8f)
            {
                var x = center.x + away.x * distance;
                var z = center.z + away.y * distance;
                if (!BlocksTrunk(x, z, trunkRadius))
                {
                    return new Vector2(x, z);
                }

                distance += 0.15f;
            }

            return new Vector2(center.x + away.x * 8f, center.z + away.y * 8f);
        }

        /// <summary>Trunk footprint overlaps the water ellipse. Crown overhang is allowed.</summary>
        public static bool BlocksTrunk(float worldX, float worldZ, float trunkRadius)
        {
            var radius = Mathf.Max(MinTrunkRadius, trunkRadius);
            var c = GetWorldCenter();
            var dx = worldX - c.x;
            var dz = worldZ - c.z;
            var rx = WaterHalfExtentX + radius;
            var rz = WaterHalfExtentZ + radius;
            var nx = dx / rx;
            var nz = dz / rz;
            return nx * nx + nz * nz <= 1f;
        }

        /// <summary>Remove saved plants whose footprint sits in the pond reserve.</summary>
        public static int ScrubOverlappingPlants(GameSaveData save)
        {
            if (save == null)
            {
                return 0;
            }

            var removed = 0;
            removed += ScrubList(save.plants);
            if (save.chunks != null)
            {
                for (var i = 0; i < save.chunks.Count; i++)
                {
                    removed += ScrubList(save.chunks[i]?.plants);
                }
            }

            return removed;
        }

        private static int ScrubList(List<PlantSaveData> plants)
        {
            if (plants == null || plants.Count == 0)
            {
                return 0;
            }

            var removed = 0;
            for (var i = plants.Count - 1; i >= 0; i--)
            {
                var p = plants[i];
                if (p == null)
                {
                    plants.RemoveAt(i);
                    removed++;
                    continue;
                }

                if (IsInsideReserveXZ(p.posX, p.posZ))
                {
                    plants.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }
    }
}
