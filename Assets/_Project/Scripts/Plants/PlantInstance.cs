using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Plants
{
    public sealed class PlantInstance : MonoBehaviour
    {
        [SerializeField] private PlantData plantData;
        [SerializeField] private string instanceId;
        [SerializeField] private string boundPlotId;
        [SerializeField] private int stageIndex;
        [SerializeField] private float stageProgress;
        [SerializeField] private Transform visualRoot;

        public PlantData Data => plantData;
        public string InstanceId => instanceId;
        public string BoundPlotId => boundPlotId;
        public int StageIndex => stageIndex;
        public float StageProgress => stageProgress;
        public bool IsFullyGrown =>
            plantData != null && stageIndex >= plantData.MaxStageIndex && stageProgress >= 1f;

        public void SetBoundPlotId(string plotId) => boundPlotId = plotId ?? string.Empty;

        public void Initialize(PlantData data, string id, int stage = 0, float progress = 0f)
        {
            plantData = data;
            instanceId = id;

            if (plantData == null)
            {
                Debug.LogWarning("[AFK Island] PlantInstance initialized without PlantData.");
                stageIndex = 0;
                stageProgress = 0f;
                return;
            }

            stageIndex = Mathf.Clamp(stage, 0, plantData.MaxStageIndex);
            stageProgress = Mathf.Clamp01(progress);
            if (stageIndex >= plantData.MaxStageIndex)
            {
                // Adult stage: progress is either still growing inside last stage or complete.
                stageProgress = Mathf.Clamp01(progress);
            }

            if (visualRoot == null)
            {
                var go = new GameObject("Visual");
                go.transform.SetParent(transform, false);
                visualRoot = go.transform;
            }

            RefreshVisual();
        }

        public event System.Action<PlantInstance> StageChanged;

        public void ApplyOfflineSeconds(double seconds)
        {
            if (plantData == null || seconds <= 0d || IsFullyGrown)
            {
                return;
            }

            var previousStage = stageIndex;
            var remaining = seconds;
            while (remaining > 0d && !IsFullyGrown)
            {
                var duration = plantData.GetStageDuration(stageIndex);
                var leftInStage = (1f - stageProgress) * duration;
                if (remaining >= leftInStage)
                {
                    remaining -= leftInStage;
                    stageProgress = 0f;
                    if (stageIndex < plantData.MaxStageIndex)
                    {
                        stageIndex++;
                        RefreshVisual();
                    }
                    else
                    {
                        stageProgress = 1f;
                        break;
                    }
                }
                else
                {
                    stageProgress += (float)(remaining / duration);
                    remaining = 0d;
                }
            }

            stageIndex = Mathf.Clamp(stageIndex, 0, plantData.MaxStageIndex);
            stageProgress = Mathf.Clamp01(stageProgress);

            if (stageIndex != previousStage)
            {
                StageChanged?.Invoke(this);
            }
        }

        public void SetState(int stage, float progress)
        {
            if (plantData == null)
            {
                stageIndex = Mathf.Max(0, stage);
                stageProgress = Mathf.Clamp01(progress);
                return;
            }

            stageIndex = Mathf.Clamp(stage, 0, plantData.MaxStageIndex);
            stageProgress = Mathf.Clamp01(progress);
            RefreshVisual();
        }

        private void RefreshVisual()
        {
            if (visualRoot == null || plantData == null || plantData.growthStagePrefabs == null)
            {
                return;
            }

            for (var i = visualRoot.childCount - 1; i >= 0; i--)
            {
                DestroyImmediateSafe(visualRoot.GetChild(i).gameObject);
            }

            var idx = Mathf.Clamp(stageIndex, 0, plantData.MaxStageIndex);
            if (idx >= plantData.growthStagePrefabs.Length)
            {
                Debug.LogWarning(
                    $"[AFK Island] Plant '{plantData.plantId}' stage {idx} has no prefab.");
                return;
            }

            var prefab = plantData.growthStagePrefabs[idx];
            if (prefab == null)
            {
                return;
            }

            var visual = Instantiate(prefab, visualRoot);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            EnsureRendererMaterials(visual, idx);
            // Stage 9.3 — same VegetationWind path as grown decor; globals from WorldVisualEnvironment only.
            VegetationWindApplicator.ApplyToInstance(
                visual, isRock: false, styleName: ResolvePlantWindStyle(prefab, idx));
            visual.name = $"Stage_{idx}";
        }

        private static string ResolvePlantWindStyle(GameObject prefab, int stageIndex)
        {
            // Stage_* prefab names lose Polyeler tokens; map by stage / nested mesh name.
            if (prefab != null)
            {
                var nested = prefab.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < nested.Length; i++)
                {
                    var n = nested[i].name;
                    if (string.IsNullOrEmpty(n) || n.StartsWith("Stage_"))
                    {
                        continue;
                    }

                    if (n.IndexOf("Viola", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Mugwort", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Eurya", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Bamboo", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Laurel", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Cedar", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Pine", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Oak", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Cypress", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return n;
                    }
                }

                if (!string.IsNullOrEmpty(prefab.name) && !prefab.name.StartsWith("Stage_"))
                {
                    return prefab.name;
                }
            }

            switch (stageIndex)
            {
                case 0: return "Viola";
                case 1: return "Mugwort";
                case 2: return "Cedar";
                default: return "Oak";
            }
        }

        private static void EnsureRendererMaterials(GameObject visual, int stageIndex)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                var mats = renderer.sharedMaterials;
                var dirty = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null)
                    {
                        continue;
                    }

                    mats[i] = CreateFallbackMaterial(stageIndex);
                    dirty = true;
                }

                if (dirty)
                {
                    renderer.sharedMaterials = mats;
                }
            }
        }

        private static Material CreateFallbackMaterial(int stageIndex)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            var color = stageIndex <= 0
                ? new Color(0.45f, 0.3f, 0.15f, 1f)
                : new Color(0.3f, 0.7f, 0.25f, 1f);
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else
            {
                mat.color = color;
            }

            return mat;
        }

        private static void DestroyImmediateSafe(GameObject go)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(go);
            }
            else
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
