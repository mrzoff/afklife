#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.Fishing;
using AfkIsland.Input;
using AfkIsland.Save;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 11E pond smoke: nearest-hit (occluders block), shallow proxy, live rebind, input path.
    /// UI EventSystem blocking remains device freeform (not faked here).
    /// </summary>
    public static class FishingPondInputSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "fishing_pond_input_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Fishing Pond Input 10B")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("FishingPondInput smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] FishingPondInput 10B smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            var created = new List<GameObject>();
            var log = new StringBuilder();
            try
            {
                Physics.SyncTransforms();

                // --- WaterSource + proxy create / idempotent ---
                var pond = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pond.name = FishingPondInput.WaterSourceName;
                pond.transform.position = new Vector3(0f, 1.78f, 0f);
                pond.transform.localScale = new Vector3(2.2f, 0.08f, 1.6f);
                created.Add(pond);
                UnityEngine.Object.DestroyImmediate(pond.GetComponent<Collider>());

                if (!FishingPondInput.EnsurePondHitProxy())
                {
                    return Fail(2, "EnsurePondHitProxy failed when WaterSource exists.", log);
                }

                if (!FishingPondInput.TryGetPondHitProxy(out var proxy1))
                {
                    return Fail(3, "PondHitProxy child missing after Ensure.", log);
                }

                if (pond.GetComponent<MeshCollider>() != null)
                {
                    return Fail(4, "WaterSource root must not keep MeshCollider.", log);
                }

                var col1 = proxy1.GetComponent<BoxCollider>();
                if (col1 == null || col1.isTrigger)
                {
                    return Fail(5, "PondHitProxy BoxCollider must be non-trigger (tall hit volume).", log);
                }

                log.AppendLine("proxy_create=PASS");

                if (!FishingPondInput.EnsurePondHitProxy() ||
                    !FishingPondInput.TryGetPondHitProxy(out var proxy2))
                {
                    return Fail(6, "Second Ensure failed.", log);
                }

                var proxyCount = 0;
                for (var i = 0; i < pond.transform.childCount; i++)
                {
                    if (pond.transform.GetChild(i).name == FishingPondInput.ProxyChildName)
                    {
                        proxyCount++;
                    }
                }

                if (proxyCount != 1 || proxy2 != proxy1)
                {
                    return Fail(7, $"Ensure must be idempotent (proxyCount={proxyCount}).", log);
                }

                log.AppendLine("proxy_idempotent=PASS");

                // --- Settlement upgrade rebind: dying WaterSource must not steal Ensure ---
                pond.name = "WaterSource_Retired";
                var livePond = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                livePond.name = FishingPondInput.WaterSourceName;
                livePond.transform.position = new Vector3(0f, 1.78f, 0f);
                livePond.transform.localScale = new Vector3(2.2f, 0.08f, 1.6f);
                created.Add(livePond);
                UnityEngine.Object.DestroyImmediate(livePond.GetComponent<Collider>());
                if (!FishingPondInput.EnsurePondHitProxy(livePond) ||
                    !FishingPondInput.TryGetPondHitProxy(out var liveProxy) ||
                    liveProxy.parent != livePond.transform)
                {
                    return Fail(8, "Ensure must bind PondHitProxy to the live WaterSource instance.", log);
                }

                pond = livePond;
                proxy1 = liveProxy;
                log.AppendLine("proxy_rebind_live=PASS");

                // --- Stage 11E: occluders above pond must BLOCK (nearest-hit contract) ---
                var yard = GameObject.CreatePrimitive(PrimitiveType.Cube);
                yard.name = "SettlementYard";
                yard.transform.position = pond.transform.position + Vector3.up * 2f;
                yard.transform.localScale = new Vector3(3f, 0.2f, 3f);
                created.Add(yard);
                Physics.SyncTransforms();

                var occludedRay = new Ray(pond.transform.position + Vector3.up * 8f, Vector3.down);
                if (!Physics.Raycast(occludedRay, out var nearest, 500f, ~0, QueryTriggerInteraction.Ignore) ||
                    nearest.collider == null ||
                    nearest.collider.gameObject.name != "SettlementYard")
                {
                    return Fail(
                        9,
                        "Nearest hit must be SettlementYard (occlusion precondition failed).",
                        log);
                }

                if (FishingPondInput.RayHitsPond(occludedRay))
                {
                    return Fail(10, "RayHitsPond must NOT accept occluded pond (yard in front).", log);
                }

                log.AppendLine("occlusion_blocks=PASS");

                var proxyCol = proxy1.GetComponent<Collider>();
                if (!FishingPondInput.IsPondCollider(proxyCol))
                {
                    return Fail(11, "IsPondCollider(PondHitProxy) must be true.", log);
                }

                if (FishingPondInput.IsPondCollider(yard.GetComponent<Collider>()))
                {
                    return Fail(12, "SettlementYard must not count as pond.", log);
                }

                // Clear line of sight: remove yard, nearest must be pond.
                UnityEngine.Object.DestroyImmediate(yard);
                created.Remove(yard);
                Physics.SyncTransforms();
                var clearRay = new Ray(pond.transform.position + Vector3.up * 5f, Vector3.down);
                if (!FishingPondInput.RayHitsPond(clearRay))
                {
                    return Fail(13, "Clear ray must hit pond when nothing occludes.", log);
                }

                log.AppendLine("clear_pond_hit=PASS");

                // Tree canopy above pond must also block.
                var canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                canopy.name = "TreeCanopy_0";
                canopy.transform.position = pond.transform.position + Vector3.up * 2.5f;
                canopy.transform.localScale = Vector3.one * 2f;
                created.Add(canopy);
                Physics.SyncTransforms();
                var canopyRay = new Ray(pond.transform.position + Vector3.up * 8f, Vector3.down);
                if (FishingPondInput.RayHitsPond(canopyRay))
                {
                    return Fail(14, "RayHitsPond must NOT accept pond through tree canopy.", log);
                }

                UnityEngine.Object.DestroyImmediate(canopy);
                created.Remove(canopy);
                Physics.SyncTransforms();
                log.AppendLine("tree_canopy_blocks=PASS");

                // Proxy must stay a shallow disc (not a tall false-positive volume).
                var box = proxy1.GetComponent<BoxCollider>();
                if (box == null || box.size.y > 1.25f)
                {
                    return Fail(17, $"PondHitProxy must be a shallow disc (size.y={box?.size.y}).", log);
                }

                log.AppendLine("proxy_shallow=PASS");

                // --- Ocean isolation ---
                var ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ocean.name = "Ocean";
                ocean.transform.position = new Vector3(20f, 0f, 0f);
                created.Add(ocean);
                FishingOceanInput.EnsureOceanHitProxy();
                var oceanCol = ocean.GetComponent<MeshCollider>();
                if (oceanCol == null || !FishingOceanInput.IsOceanCollider(oceanCol))
                {
                    return Fail(15, "Ocean MeshCollider must be ocean-eligible.", log);
                }

                if (FishingOceanInput.IsOceanCollider(proxyCol) ||
                    FishingPondInput.IsPondCollider(oceanCol))
                {
                    return Fail(16, "Ocean and Pond proxies must not cross-match.", log);
                }

                var oceanRay = new Ray(ocean.transform.position + Vector3.up * 5f, Vector3.down);
                if (FishingPondInput.RayHitsPond(oceanRay))
                {
                    return Fail(17, "Ocean ray must not register as pond.", log);
                }

                log.AppendLine("ocean_isolation=PASS");

                // --- Pond input path: camera + SmokeTryWorldTap ---
                var host = new GameObject("PondSmoke_Host");
                created.Add(host);
                var controller = host.AddComponent<FishingController>();
                var ui = host.AddComponent<FishingUI>();
                var save = host.AddComponent<SaveService>();
                var catalog = Resources.Load<FishCatalog>("FishCatalog");
                if (catalog == null)
                {
                    return Fail(18, "FishCatalog missing.", log);
                }

                controller.Configure(save, catalog, ui, null);

                var camGo = new GameObject("PondSmoke_Cam");
                created.Add(camGo);
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.transform.position = pond.transform.position + new Vector3(0f, 8f, 0f);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                var pondInput = host.AddComponent<FishingPondInput>();
                pondInput.Configure(controller, cam);
                pondInput.SetInputEnabled(false);

                var pondTapRay = new Ray(pond.transform.position + Vector3.up * 5f, Vector3.down);
                if (pondInput.SmokeTryWorldRay(pondTapRay))
                {
                    return Fail(19, "Disabled pond input must not open fishing.", log);
                }

                if (controller.IsOpen)
                {
                    return Fail(20, "Controller must stay closed while pond input disabled.", log);
                }

                log.AppendLine("input_disabled=PASS");

                pondInput.SetInputEnabled(true);
                if (!pondInput.SmokeTryWorldRay(pondTapRay) ||
                    !controller.IsOpen ||
                    controller.ActiveWaterSource != FishingWaterSource.Pond)
                {
                    return Fail(21, "Enabled pond tap path must open Pond fishing.", log);
                }

                controller.RequestClose();
                log.AppendLine("input_pond_tap=PASS");

                // Land miss: ray far from pond/ocean
                var missRay = new Ray(
                    pond.transform.position + new Vector3(50f, 5f, 50f),
                    Vector3.down);
                if (pondInput.SmokeTryWorldRay(missRay) || controller.IsOpen)
                {
                    return Fail(22, "Tap on empty land must not open pond fishing.", log);
                }

                log.AppendLine("input_land_miss=PASS");

                // Ocean open regression (controller API)
                if (!controller.TryOpenFromOcean() ||
                    controller.ActiveWaterSource != FishingWaterSource.Ocean)
                {
                    return Fail(23, "Ocean open regression failed.", log);
                }

                controller.RequestClose();
                log.AppendLine("ocean_open_regression=PASS");

                // Drag cancel via shared gesture (pond input uses same thresholds)
                var press = new WorldPointerGesture.PressState();
                WorldPointerGesture.BeginPress(ref press, Vector2.zero, -1);
                WorldPointerGesture.TrackMove(
                    ref press,
                    new Vector2(WorldPointerGesture.DefaultMaxTapMovePixels + 8f, 0f));
                if (!press.CancelledAsDrag ||
                    WorldPointerGesture.TryCompleteTap(ref press, new Vector2(40f, 0f), out _))
                {
                    return Fail(24, "Drag must cancel pond-style tap.", log);
                }

                log.AppendLine("gesture_drag_cancel=PASS");
                log.AppendLine("ui_eventsystem=DEVICE_FREEFORM_10C");
                log.AppendLine("PASS");

                WriteResult(0, log.ToString());
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] FishingPondInput smoke exception: " + e);
                WriteResult(1, "FAIL 1\n" + e);
                return 1;
            }
            finally
            {
                for (var i = 0; i < created.Count; i++)
                {
                    if (created[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(created[i]);
                    }
                }
            }
        }

        private static int Fail(int code, string message, StringBuilder log)
        {
            Debug.LogError($"[AFK Island] FishingPondInput smoke FAIL {code}: {message}");
            log?.AppendLine($"FAIL {code}: {message}");
            WriteResult(code, log?.ToString() ?? $"FAIL {code}\n");
            return code;
        }

        private static void WriteResult(int code, string body)
        {
            try
            {
                var dir = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var text = code == 0
                    ? (string.IsNullOrEmpty(body) ? "PASS\n" : body)
                    : (body.StartsWith("FAIL", StringComparison.Ordinal)
                        ? body
                        : $"FAIL {code}\n{body}");
                if (!text.EndsWith("\n", StringComparison.Ordinal))
                {
                    text += "\n";
                }

                File.WriteAllText(ResultPath, text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AFK Island] Could not write pond smoke result: " + e.Message);
            }
        }
    }
}
#endif
