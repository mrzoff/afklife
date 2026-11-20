#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 11G-B: deterministic world-space tree scatter invariants.
    /// </summary>
    public static class WorldScatterFieldSmoke
    {
        private const int Seed = 0x11B056;

        [MenuItem("AFK Island/Smoke Organic Tree Placement (11G-B)")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Organic Tree Placement smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Organic Tree Placement smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            try
            {
                var wholeBounds = new Rect(-16f, -8f, 32f, 16f);
                var leftBounds = new Rect(-16f, -8f, 16f, 16f);
                var rightBounds = new Rect(0f, -8f, 16f, 16f);
                var all = new List<WorldScatterField.ScatterPoint>(128);
                var repeat = new List<WorldScatterField.ScatterPoint>(128);
                var left = new List<WorldScatterField.ScatterPoint>(64);
                var right = new List<WorldScatterField.ScatterPoint>(64);
                var leftReverse = new List<WorldScatterField.ScatterPoint>(64);
                var rightReverse = new List<WorldScatterField.ScatterPoint>(64);

                float Density(Vector2 p) =>
                    0.68f * Mathf.Lerp(0.7f, 1f,
                        WorldScatterField.SampleValueNoise(p.x, p.y, 17f, Seed, 7u));

                bool Valid(Vector2 p) => true;

                WorldScatterField.Generate(wholeBounds, Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    Density, Valid, null, all);
                WorldScatterField.Generate(wholeBounds, Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    Density, Valid, null, repeat);
                if (!SamePoints(all, repeat))
                {
                    Debug.LogError("Determinism failed: identical input produced different points.");
                    return 2;
                }

                // Deliberately request the right chunk first, then the left.
                WorldScatterField.Generate(rightBounds, Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    Density, Valid, null, right);
                WorldScatterField.Generate(leftBounds, Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    Density, Valid, null, left);

                WorldScatterField.Generate(leftBounds, Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    Density, Valid, null, leftReverse);
                WorldScatterField.Generate(rightBounds, Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    Density, Valid, null, rightReverse);

                if (!SamePoints(left, leftReverse) || !SamePoints(right, rightReverse))
                {
                    Debug.LogError("Chunk order independence failed.");
                    return 3;
                }

                var partitioned = new List<WorldScatterField.ScatterPoint>(left.Count + right.Count);
                partitioned.AddRange(left);
                partitioned.AddRange(right);
                if (!SamePoints(all, partitioned))
                {
                    Debug.LogError("Chunk partition does not match one world-space generation.");
                    return 4;
                }

                var minSq = WorldScatterField.DefaultTreeMinimumSpacing *
                            WorldScatterField.DefaultTreeMinimumSpacing;
                for (var i = 0; i < all.Count; i++)
                {
                    for (var j = i + 1; j < all.Count; j++)
                    {
                        if ((all[i].Position - all[j].Position).sqrMagnitude < minSq - 0.0001f)
                        {
                            Debug.LogError(
                                $"Minimum separation failed: {all[i].Position} / {all[j].Position}");
                            return 5;
                        }
                    }
                }

                for (var i = 0; i < all.Count; i++)
                {
                    var p = all[i].Position;
                    if (!wholeBounds.Contains(p))
                    {
                        Debug.LogError("Scatter emitted a point outside the requested bounds.");
                        return 6;
                    }
                }

                var blocked = new List<Vector2> { all.Count > 0 ? all[0].Position : Vector2.zero };
                var withoutBlocked = new List<WorldScatterField.ScatterPoint>(128);
                WorldScatterField.Generate(wholeBounds, Seed,
                    WorldScatterField.DefaultTreeMinimumSpacing,
                    WorldScatterField.DefaultCandidateSpacing,
                    Density, Valid, blocked, withoutBlocked);
                for (var i = 0; i < withoutBlocked.Count; i++)
                {
                    if ((withoutBlocked[i].Position - blocked[0]).sqrMagnitude < minSq)
                    {
                        Debug.LogError("Blocked point exclusion failed.");
                        return 7;
                    }
                }

                var empty = new List<WorldScatterField.ScatterPoint>(16);
                WorldScatterField.Generate(wholeBounds, Seed, 3.5f, 2.2f,
                    _ => 0f, Valid, null, empty);
                if (empty.Count != 0)
                {
                    Debug.LogError("Zero-density field emitted points.");
                    return 8;
                }

                Debug.Log(
                    $"[AFK Island] 11G-B scatter smoke: points={all.Count}, " +
                    $"partitioned={partitioned.Count}, minSpacing={Mathf.Sqrt(minSq):0.00}m.");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return 1;
            }
        }

        private static bool SamePoints(
            List<WorldScatterField.ScatterPoint> a,
            List<WorldScatterField.ScatterPoint> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            var keys = new HashSet<long>();
            for (var i = 0; i < a.Count; i++)
            {
                keys.Add(Pack(a[i].GridX, a[i].GridZ));
            }

            for (var i = 0; i < b.Count; i++)
            {
                if (!keys.Remove(Pack(b[i].GridX, b[i].GridZ)))
                {
                    return false;
                }
            }

            return keys.Count == 0;
        }

        private static long Pack(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
#endif
