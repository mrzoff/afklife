#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AfkIsland.Animals;
using AfkIsland.Plants;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Regression checks for the Stage 11G visual placement fixes.
    /// Does not write or migrate any save data.
    /// </summary>
    public static class Stage11GVisualFixSmoke
    {
        [MenuItem("AFK Island/Smoke Stage 11G Visual Fixes")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Stage 11G visual fix smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Stage 11G visual fix smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            try
            {
                if (!CheckHutFootprints()) return 2;
                if (!CheckLegacyHutVisibility()) return 7;
                if (!CheckBirdCatalog()) return 3;
                if (!CheckPlantPlotMarkers()) return 4;
                if (!CheckOrganicPlatformMesh()) return 5;
                if (!CheckPlantBlockerRefresh()) return 6;

                Debug.Log("[AFK Island] Stage 11G visual fixes: hut footprints, option D legacy " +
                          "visibility, bird tuning, hidden plot markers, organic platform mesh " +
                          "and plant blocker refresh passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return 1;
            }
        }

        private static bool CheckHutFootprints()
        {
            const long now = 1_700_000_000L;
            var save = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now)
            };

            var left = SaveMigrationService.CreateGrowthRecord(1, 0, false);
            var right = SaveMigrationService.CreateGrowthRecord(2, 0, false);
            var leftMask = new byte[ChunkMaskCodec.CellCount];
            var rightMask = new byte[ChunkMaskCodec.CellCount];
            var leftCell = ChunkMaskCodec.CellIndex(15, 4);
            var rightCell = ChunkMaskCodec.CellIndex(0, 4);
            leftMask[leftCell] = (byte)SettlementStage.Hut;
            rightMask[rightCell] = (byte)SettlementStage.Hut;
            left.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(leftMask);
            right.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(rightMask);
            save.worldGrowth.chunks.Add(left);
            save.worldGrowth.chunks.Add(right);

            var leftCenter = GrowthWorldPlacement.CellCenterWorld(1, 0, leftCell);
            var rightCenter = GrowthWorldPlacement.CellCenterWorld(2, 0, rightCell);
            if (!SettlementCellQuery.IsHutFootprintBlocked(
                    save, rightCenter.x, rightCenter.z, SettlementCellQuery.DefaultHutCenterSpacing))
            {
                Debug.LogError("Hut footprint query missed an overlapping hut across a chunk seam.");
                return false;
            }

            // Option D: scaled footprints fit in 1 m cells, so adjacent mask huts both draw.
            if (SettlementCellQuery.IsHutVisualSuppressed(save, 1, 0, leftCell) ||
                SettlementCellQuery.IsHutVisualSuppressed(save, 2, 0, rightCell))
            {
                Debug.LogError(
                    "Option D: adjacent 1 m hut cells must both remain visible with visual spacing.");
                return false;
            }

            var authoredHutWidth = 1.59f;
            var scaledFootprint = SettlementCellQuery.HutVisualUniformScale * authoredHutWidth;
            if (scaledFootprint > SettlementCellQuery.HutVisualFootprintMeters + 0.02f)
            {
                Debug.LogError(
                    $"Hut visual scale {SettlementCellQuery.HutVisualUniformScale} exceeds " +
                    $"{SettlementCellQuery.HutVisualFootprintMeters} m footprint target " +
                    $"(scaled={scaledFootprint:0.###}).");
                return false;
            }

            if (!SettlementCellQuery.IsHutFootprintBlocked(save, -1.6f, 0.9f, 2.0f))
            {
                Debug.LogError("Authored starter hut footprint was not reserved.");
                return false;
            }

            // Authored clearance for visuals: cell (~-1.5, 1.5) is ~0.61 m from Hut_A (-1.6, 0.9).
            var nearAuthored = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now)
            };
            var home = SaveMigrationService.CreateGrowthRecord(0, 0, true);
            var homeMask = new byte[ChunkMaskCodec.CellCount];
            var nearCell = ChunkMaskCodec.CellIndex(6, 9);
            homeMask[nearCell] = (byte)SettlementStage.Hut;
            home.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(homeMask);
            nearAuthored.worldGrowth.chunks.Add(home);
            if (!SettlementCellQuery.IsHutVisualSuppressed(nearAuthored, 0, 0, nearCell))
            {
                Debug.LogError("Hut visual suppression missed authored starter clearance.");
                return false;
            }

            var far = GrowthWorldPlacement.CellCenterWorld(2, 0, ChunkMaskCodec.CellIndex(12, 12));
            if (SettlementCellQuery.IsHutFootprintBlocked(save, far.x, far.z, 2.0f))
            {
                Debug.LogError("Hut footprint query blocked a distant free location.");
                return false;
            }

            if (Mathf.Abs(leftCenter.x - rightCenter.x) > 1.01f)
            {
                Debug.LogError("Cross-chunk hut fixture is not adjacent as expected.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Option D acceptance on the reference legacy save: almost all Hut cells must remain
        /// visually unsuppressed (authored starter clearance may hide a few).
        /// </summary>
        private static bool CheckLegacyHutVisibility()
        {
            var path = Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName, "Builds", "save056.json");
            if (!File.Exists(path))
            {
                Debug.LogError("Reference save missing for hut visibility check: " + path);
                return false;
            }

            var save = JsonUtility.FromJson<GameSaveData>(File.ReadAllText(path));
            if (save?.worldGrowth?.chunks == null)
            {
                Debug.LogError("Reference save could not be deserialized for hut visibility.");
                return false;
            }

            var hutCells = 0;
            var visible = 0;
            for (var c = 0; c < save.worldGrowth.chunks.Count; c++)
            {
                var growth = save.worldGrowth.chunks[c];
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] != (byte)SettlementStage.Hut)
                    {
                        continue;
                    }

                    hutCells++;
                    if (!SettlementCellQuery.IsHutVisualSuppressed(
                            save, growth.chunkX, growth.chunkZ, i))
                    {
                        visible++;
                    }
                }
            }

            // Pre-D baseline was 7 visible of 64. Option D must restore the dense field.
            if (hutCells < 60 || visible < hutCells - 3)
            {
                Debug.LogError(
                    $"Option D legacy hut visibility failed: hutCells={hutCells}, visible={visible} " +
                    "(expected nearly all cells visible; only authored clearance may hide a few).");
                return false;
            }

            Debug.Log($"[AFK Island] Option D legacy huts: {visible}/{hutCells} visually unsuppressed.");
            return true;
        }

        private static bool CheckBirdCatalog()
        {
            var bird = Resources.Load<AnimalData>("Animals/AnimalData_bird");
            if (bird == null)
            {
                Debug.LogError("Bird AnimalData resource was not found.");
                return false;
            }

            if (bird.visualScale.x > 0.3f || bird.moveSpeed > 0.5f ||
                bird.wanderRadius > 2f || bird.modelForwardYawOffset != 180f)
            {
                Debug.LogError(
                    $"Bird tuning unexpected: scale={bird.visualScale}, speed={bird.moveSpeed}, " +
                    $"radius={bird.wanderRadius}, yawOffset={bird.modelForwardYawOffset}.");
                return false;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Art/Animals/Prefabs/Animal_Bird.prefab");
            var model = prefab != null ? prefab.transform.Find("Visual/Bird_Quaternius") : null;
            if (model == null || Mathf.Abs(Mathf.DeltaAngle(model.localEulerAngles.y, 180f)) > 1f)
            {
                Debug.LogError("Bird prefab's expected 180-degree model-forward offset was not found.");
                return false;
            }

            var corrected = Quaternion.LookRotation(Vector3.forward, Vector3.up) *
                           Quaternion.Euler(0f, bird.modelForwardYawOffset, 0f) *
                           model.localRotation * Vector3.forward;
            if (Vector3.Dot(corrected.normalized, Vector3.forward) < 0.99f)
            {
                Debug.LogError("Bird model yaw correction does not align its forward axis with travel.");
                return false;
            }

            return true;
        }

        private static bool CheckPlantPlotMarkers()
        {
            var go = new GameObject("Stage11G_PlantPlotSmoke");
            try
            {
                var plot = go.AddComponent<PlantPlot>();
                plot.Setup("stage11g_smoke");
                var marker = go.GetComponentInChildren<Renderer>(true);
                var collider = go.GetComponent<BoxCollider>();
                if (marker != null && marker.enabled)
                {
                    Debug.LogError("Available planting marker is still visible by default.");
                    return false;
                }

                if (collider == null || !collider.enabled)
                {
                    Debug.LogError("Hiding planting markers removed the plot tap collider.");
                    return false;
                }

                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static bool CheckOrganicPlatformMesh()
        {
            var cedarField = typeof(IslandPrototypeBuilder).GetField(
                "AuthoredCedarPosition",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (cedarField == null)
            {
                Debug.LogError("Authored starter cedar position was not found.");
                return false;
            }

            var cedarPosition = (Vector3)cedarField.GetValue(null);
            if (Vector2.Distance(
                    new Vector2(cedarPosition.x, cedarPosition.z), new Vector2(-1.6f, 0.9f)) < 2.5f ||
                !GrowthWorldPlacement.IsInsideAuthoredHomeFootprint(cedarPosition.x, cedarPosition.z))
            {
                Debug.LogError("Authored cedar is too close to Hut_A or outside the safe home footprint.");
                return false;
            }

            var method = typeof(IslandPrototypeBuilder).GetMethod(
                "BuildOrganicPlatformMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                Debug.LogError("Organic starter platform mesh builder was not found.");
                return false;
            }

            var mesh = method.Invoke(null, new object[] { "BaseRock" }) as Mesh;
            if (mesh == null)
            {
                Debug.LogError("Organic starter platform mesh was not generated.");
                return false;
            }

            try
            {
                if (mesh.vertexCount != 194 || mesh.triangles.Length != 64 * 18)
                {
                    Debug.LogError(
                        $"Organic platform mesh dimensions unexpected: vertices={mesh.vertexCount}, " +
                        $"indices={mesh.triangles.Length}.");
                    return false;
                }

                var vertices = mesh.vertices;
                var minRadius = float.MaxValue;
                var maxRadius = 0f;
                for (var i = 1; i <= 64; i++)
                {
                    var radius = new Vector2(vertices[i].x, vertices[i].z).magnitude;
                    minRadius = Mathf.Min(minRadius, radius);
                    maxRadius = Mathf.Max(maxRadius, radius);
                }

                if (maxRadius - minRadius < 0.02f)
                {
                    Debug.LogError("Organic platform perimeter is too regular.");
                    return false;
                }

                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// Regression: saved plants are global blockers for tall-tree scatter, so planting,
        /// moving or removing one must invalidate the cached rebuild signature and must drop
        /// the tree candidate it covers. The final decor re-placement is a Play Mode step:
        /// <see cref="IslandMeshVisualizer.RebuildDecor"/> calls Object.Destroy, which cannot
        /// run in an editor-only smoke.
        /// </summary>
        private static bool CheckPlantBlockerRefresh()
        {
            var blockerMethod = typeof(IslandMeshVisualizer).GetMethod(
                "BuildPlantBlockerSignature",
                BindingFlags.NonPublic | BindingFlags.Static);
            var rebuildMethod = typeof(IslandMeshVisualizer).GetMethod(
                "BuildSignature",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (blockerMethod == null || rebuildMethod == null)
            {
                Debug.LogError("IslandMeshVisualizer plant blocker signature was not found.");
                return false;
            }

            string BlockerSignature(GameSaveData save) =>
                (string)blockerMethod.Invoke(null, new object[] { save });

            string RebuildSignature(GameSaveData save) =>
                (string)rebuildMethod.Invoke(null, new object[] { save });

            const long now = 1_700_000_000L;
            var save = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now)
            };
            save.chunks.Add(new ChunkSaveData
            {
                chunkX = 1,
                chunkZ = 0,
                isUnlocked = true,
                biomeId = "wild_expansion"
            });

            var emptyBlocker = BlockerSignature(save);
            var emptyRebuild = RebuildSignature(save);

            var plant = new PlantSaveData
            {
                instanceId = "stage11g_refresh",
                plantId = "plant_test",
                plotId = "wild_1_0_17",
                posX = 9.5f,
                posZ = 0.5f
            };
            save.plants.Add(plant);

            var afterAdd = BlockerSignature(save);
            if (afterAdd == emptyBlocker || RebuildSignature(save) == emptyRebuild)
            {
                Debug.LogError("Adding a plant did not invalidate the scatter rebuild signature.");
                return false;
            }

            // Real saves can hold the same plant in the root list and in its chunk list.
            // Duplicates must not change the blocker set or trigger another rebuild.
            var rebuildAfterAdd = RebuildSignature(save);
            save.chunks[0].plants.Add(plant);
            if (BlockerSignature(save) != afterAdd || RebuildSignature(save) != rebuildAfterAdd)
            {
                Debug.LogError("Duplicating a plant into the chunk list changed the blocker signature.");
                return false;
            }

            plant.posX = 6.25f;
            if (BlockerSignature(save) == afterAdd)
            {
                Debug.LogError("Moving a plant did not invalidate the blocker signature.");
                return false;
            }

            save.plants.Remove(plant);
            save.chunks[0].plants.Remove(plant);
            if (BlockerSignature(save) != emptyBlocker)
            {
                Debug.LogError("Removing a plant did not restore the blocker signature.");
                return false;
            }

            // The blocker must remove the tree candidate it covers, not just change a hash.
            var bounds = new Rect(-8f, -8f, 16f, 16f);
            const int seed = 424242;
            var free = new List<WorldScatterField.ScatterPoint>(128);
            WorldScatterField.Generate(bounds, seed,
                WorldScatterField.DefaultTreeMinimumSpacing,
                WorldScatterField.DefaultCandidateSpacing,
                _ => 1f, _ => true, null, free);
            if (free.Count == 0)
            {
                Debug.LogError("Dense scatter produced no candidates for the blocker regression.");
                return false;
            }

            var covered = free[0];
            var blockedPoints = new List<Vector2> { covered.Position };
            var blocked = new List<WorldScatterField.ScatterPoint>(128);
            WorldScatterField.Generate(bounds, seed,
                WorldScatterField.DefaultTreeMinimumSpacing,
                WorldScatterField.DefaultCandidateSpacing,
                _ => 1f, _ => true, blockedPoints, blocked);
            for (var i = 0; i < blocked.Count; i++)
            {
                if (blocked[i].GridX == covered.GridX && blocked[i].GridZ == covered.GridZ)
                {
                    Debug.LogError("A plant blocker did not remove the tree candidate it covers.");
                    return false;
                }
            }

            return true;
        }
    }
}
#endif
