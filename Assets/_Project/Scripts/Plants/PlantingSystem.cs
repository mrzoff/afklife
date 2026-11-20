using System;
using System.Collections.Generic;
using AfkIsland.Input;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Plants
{
    public sealed class PlantingSystem : MonoBehaviour
    {
        [SerializeField] private PlantData defaultPlant;
        [SerializeField] private PlantCatalog plantCatalog;
        [SerializeField] private Camera worldCamera;
        [SerializeField] private List<PlantPlot> plots = new();
        [SerializeField] private Transform plantsRoot;
        [SerializeField] private float maxTapMovePixels = WorldPointerGesture.DefaultMaxTapMovePixels;
        [SerializeField] private float maxTapSeconds = WorldPointerGesture.DefaultMaxTapSeconds;

        private WorldPointerGesture.PressState _press;

        public PlantData DefaultPlant => defaultPlant;
        public PlantCatalog Catalog
        {
            get
            {
                if (plantCatalog == null)
                {
                    plantCatalog = Resources.Load<PlantCatalog>("PlantCatalog");
                }

                return plantCatalog;
            }
        }
        public IReadOnlyList<PlantPlot> Plots => plots;
        public event Action<PlantInstance> Planted;

        public void Configure(
            PlantData plant,
            Camera cam,
            Transform root,
            IEnumerable<PlantPlot> plotList,
            PlantCatalog catalog = null)
        {
            defaultPlant = plant;
            worldCamera = cam;
            plantsRoot = root;
            plots = new List<PlantPlot>(plotList);
            if (catalog != null)
            {
                plantCatalog = catalog;
            }
            else if (plantCatalog == null)
            {
                plantCatalog = Resources.Load<PlantCatalog>("PlantCatalog");
            }

            if (plantCatalog == null && plant != null)
            {
                // Runtime fallback catalog so restore-by-id works even before assets are wired.
                plantCatalog = ScriptableObject.CreateInstance<PlantCatalog>();
                plantCatalog.Configure(new[] { plant });
            }
        }

        private void Update()
        {
            // CIF-B1: ignore UI + camera drags; plant only on completed world taps.
            WorldPointerGesture.Poll(ref _press, TryPlantAtScreen, maxTapMovePixels, maxTapSeconds);
        }

        private void OnDisable()
        {
            WorldPointerGesture.Reset(ref _press);
        }

        public PlantInstance RestorePlant(
            PlantPlot plot,
            PlantData data,
            string instanceId,
            int stage,
            float progress)
        {
            return PlantAtPlot(
                plot,
                data,
                instanceId,
                stage,
                progress,
                saveAfterPlanting: false);
        }

        public PlantInstance PlantAtPlot(
            PlantPlot plot,
            PlantData data = null,
            string forcedId = null,
            int stage = 0,
            float progress = 0f,
            bool saveAfterPlanting = true)
        {
            if (plot == null || plot.IsOccupied)
            {
                return null;
            }

            data ??= defaultPlant;
            if (data == null)
            {
                Debug.LogWarning("[AFK Island] No PlantData assigned.");
                return null;
            }

            // Hut cell keeps its roof: never draw a plant through a hut (save data stays).
            if (IsOnHutCell(plot.PlantPosition))
            {
                return null;
            }

            if (plantsRoot == null)
            {
                plantsRoot = transform;
            }

            var go = new GameObject($"Plant_{data.plantId}");
            go.transform.SetParent(plantsRoot, false);
            go.transform.position = plot.PlantPosition;
            var plant = go.AddComponent<PlantInstance>();
            var id = string.IsNullOrEmpty(forcedId) ? Guid.NewGuid().ToString("N") : forcedId;
            plant.Initialize(data, id, stage, progress);
            plant.SetBoundPlotId(plot.PlotId);
            plant.StageChanged += OnPlantStageChanged;
            plot.AssignPlant(plant);
            Planted?.Invoke(plant);

            if (saveAfterPlanting)
            {
                SaveService.Instance?.SaveNow();
            }

            return plant;
        }

        /// <summary>
        /// Spawn a living plant at a world position without occupying a PlantPlot (wild colonization).
        /// </summary>
        public PlantInstance PlantAtWorldPosition(
            Vector3 worldPosition,
            PlantData data,
            string instanceId,
            string plotId,
            int stage = 0,
            float progress = 0f,
            bool saveAfterPlanting = false)
        {
            if (data == null)
            {
                Debug.LogWarning("[AFK Island] PlantAtWorldPosition: no PlantData.");
                return null;
            }

            if (FindLivePlantByInstanceId(instanceId) != null)
            {
                return null;
            }

            // Hut cell keeps its roof: saved coordinates stay, but nothing is drawn there.
            if (IsOnHutCell(worldPosition))
            {
                return null;
            }

            if (plantsRoot == null)
            {
                plantsRoot = transform;
            }

            var go = new GameObject($"Plant_{data.plantId}_{plotId}");
            go.transform.SetParent(plantsRoot, false);
            go.transform.position = worldPosition;
            var plant = go.AddComponent<PlantInstance>();
            var id = string.IsNullOrEmpty(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
            plant.Initialize(data, id, stage, progress);
            plant.SetBoundPlotId(plotId);
            plant.StageChanged += OnPlantStageChanged;
            Planted?.Invoke(plant);

            if (saveAfterPlanting)
            {
                SaveService.Instance?.SaveNow();
            }

            return plant;
        }

        public PlantInstance FindLivePlantByInstanceId(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId) || plantsRoot == null)
            {
                return null;
            }

            foreach (var plant in EnumerateLivePlants())
            {
                if (plant != null && plant.InstanceId == instanceId)
                {
                    return plant;
                }
            }

            return null;
        }

        public IEnumerable<PlantInstance> EnumerateLivePlants()
        {
            if (plantsRoot == null)
            {
                yield break;
            }

            for (var i = 0; i < plantsRoot.childCount; i++)
            {
                var plant = plantsRoot.GetChild(i).GetComponent<PlantInstance>();
                if (plant != null)
                {
                    yield return plant;
                }
            }
        }

        private void OnPlantStageChanged(PlantInstance plant)
        {
            SaveService.Instance?.SaveNow();
        }

        /// <summary>
        /// True when a world position lands on a hut cell. High decor and wild plants
        /// must not be drawn through a roof; save data is left untouched.
        /// </summary>
        private static bool IsOnHutCell(Vector3 worldPosition)
        {
            var save = SaveService.Instance != null
                ? SaveService.Instance.AuthoritativeSnapshot
                : null;
            return save != null &&
                   SettlementCellQuery.IsHutAtWorldXZ(save, worldPosition.x, worldPosition.z);
        }

        private void TryPlantAtScreen(Vector2 screenPos)
        {
            if (worldCamera == null)
            {
                worldCamera = Camera.main;
            }

            if (worldCamera == null)
            {
                return;
            }

            var ray = worldCamera.ScreenPointToRay(screenPos);
            // Trees often block plot markers; prefer any PlantPlot along the ray.
            var hits = Physics.RaycastAll(ray, 250f, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            PlantPlot plot = null;
            Vector3 fallbackPoint = default;
            var hasFallback = false;
            foreach (var hit in hits)
            {
                if (!hasFallback)
                {
                    fallbackPoint = hit.point;
                    hasFallback = true;
                }

                var candidate = hit.collider != null
                    ? hit.collider.GetComponentInParent<PlantPlot>()
                    : null;
                if (candidate != null && !candidate.IsOccupied)
                {
                    plot = candidate;
                    break;
                }
            }

            if (plot == null && hasFallback)
            {
                // Wider radius: canopy hit points are often >1m from plot origin.
                plot = FindClosestFreePlot(fallbackPoint, 3.5f);
            }

            if (plot == null)
            {
                // Last resort: closest free plot to the camera ray (for occluded markers).
                plot = FindClosestFreePlotToRay(ray, 3.5f);
            }

            if (plot == null)
            {
                Debug.Log($"[AFK Island] Plant tap missed plots. count={plots.Count} pos={screenPos}");
                return;
            }

            if (!plot.IsOccupied)
            {
                PlantAtPlot(plot);
                Debug.Log($"[AFK Island] Planted on {plot.PlotId}");
            }
        }

        private PlantPlot FindClosestFreePlot(Vector3 point, float maxDistance)
        {
            PlantPlot best = null;
            var bestDist = maxDistance;
            foreach (var plot in plots)
            {
                if (plot == null || plot.IsOccupied)
                {
                    continue;
                }

                // Horizontal distance matters more than Y for canopy hits.
                var flat = plot.PlantPosition;
                flat.y = point.y;
                var d = Vector3.Distance(flat, point);
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = plot;
                }
            }

            return best;
        }

        private PlantPlot FindClosestFreePlotToRay(Ray ray, float maxDistance)
        {
            PlantPlot best = null;
            var bestDist = maxDistance;
            foreach (var plot in plots)
            {
                if (plot == null || plot.IsOccupied)
                {
                    continue;
                }

                var toPlot = plot.PlantPosition - ray.origin;
                var along = Vector3.Dot(toPlot, ray.direction);
                if (along < 0f)
                {
                    continue;
                }

                var closest = ray.origin + ray.direction * along;
                var d = Vector3.Distance(closest, plot.PlantPosition);
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = plot;
                }
            }

            return best;
        }
    }
}
