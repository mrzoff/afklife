using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Animals influence world growth masks during AFK (seeds / humidity soft effects).
    /// Does not touch player plant plot ownership.
    /// </summary>
    public static class FaunaNatureFeedback
    {
        public static int Apply(GameSaveData save, LivingWorldResult living, DeterministicRng rng)
        {
            if (save?.animals == null || save.worldGrowth?.chunks == null || rng == null)
            {
                return 0;
            }

            var birds = 0;
            var butterflies = 0;
            var frogs = 0;
            foreach (var a in save.animals)
            {
                if (a == null)
                {
                    continue;
                }

                switch (a.animalId)
                {
                    case FaunaIds.Bird: birds++; break;
                    case FaunaIds.Butterfly: butterflies++; break;
                    case FaunaIds.Frog: frogs++; break;
                }
            }

            var advanced = 0;
            // Birds spread seeds: Moss→Vegetation or Roots→Soil.
            var birdAttempts = Mathf.Min(birds, 6) + (living?.windEvents ?? 0);
            for (var i = 0; i < birdAttempts; i++)
            {
                if (rng.NextDouble() > 0.55)
                {
                    continue;
                }

                if (TryAdvanceRandomCell(save, rng, preferMossToVeg: true))
                {
                    advanced++;
                }
            }

            // Wind with plants also nudges one soil advance.
            if ((living?.windEvents ?? 0) > 0 && rng.NextDouble() < 0.4)
            {
                if (TryAdvanceRandomCell(save, rng, preferMossToVeg: false))
                {
                    advanced++;
                }
            }

            if (save.ecosystem != null)
            {
                if (butterflies > 0)
                {
                    save.ecosystem.biodiversity = Mathf.Clamp01(
                        save.ecosystem.biodiversity + butterflies * 0.02f);
                    save.ecosystem.fertility = Mathf.Clamp01(
                        save.ecosystem.fertility + butterflies * 0.01f);
                }

                if (frogs > 0)
                {
                    save.ecosystem.humidity = Mathf.Max(
                        save.ecosystem.humidity,
                        Mathf.Clamp01(0.4f + frogs * 0.03f));
                }
            }

            if (living != null)
            {
                living.cellsAdvancedByFauna = advanced;
            }

            return advanced;
        }

        private static bool TryAdvanceRandomCell(GameSaveData save, DeterministicRng rng, bool preferMossToVeg)
        {
            var candidates = new List<(ChunkGrowthSaveData growth, int index, byte stage)>();
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null || growth.isLegacyBase)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    var s = stages[i];
                    if (preferMossToVeg)
                    {
                        if (s == (byte)CellStage.Moss || s == (byte)CellStage.Soil)
                        {
                            candidates.Add((growth, i, s));
                        }
                    }
                    else if (s == (byte)CellStage.Roots || s == (byte)CellStage.Soil)
                    {
                        candidates.Add((growth, i, s));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            var pick = candidates[(int)(rng.NextUInt() % (uint)candidates.Count)];
            if (!ChunkMaskCodec.TryDecodeBase64(pick.growth.stageMaskBase64, out var mask, out _))
            {
                return false;
            }

            var next = pick.stage switch
            {
                (byte)CellStage.Roots => (byte)CellStage.Soil,
                (byte)CellStage.Soil => (byte)CellStage.Moss,
                (byte)CellStage.Moss => (byte)CellStage.Vegetation,
                _ => pick.stage
            };
            if (next == pick.stage)
            {
                return false;
            }

            mask[pick.index] = next;
            pick.growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(mask);
            return true;
        }
    }
}
