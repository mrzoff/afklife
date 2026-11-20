using UnityEngine;

namespace AfkIsland.Plants
{
    public sealed class PlantPlot : MonoBehaviour
    {
        [SerializeField] private string plotId;
        [SerializeField] private bool occupied;
        [SerializeField] private PlantInstance currentPlant;
        [SerializeField] private Renderer markerRenderer;
        [SerializeField] private bool showAvailableMarker;

        public string PlotId => plotId;
        public bool IsOccupied => occupied;
        public PlantInstance CurrentPlant => currentPlant;
        public Vector3 PlantPosition => transform.position;

        private void Awake()
        {
            EnsureMarker();
            // Scene may contain stale marker colors (e.g. blue) — refresh from state.
            SetAvailableVisual(!occupied);
        }

        public void Setup(string id)
        {
            plotId = id;
            occupied = false;
            currentPlant = null;
            EnsureMarker();
            SetAvailableVisual(true);
        }

        public void AssignPlant(PlantInstance plant)
        {
            currentPlant = plant;
            occupied = plant != null;
            SetAvailableVisual(!occupied);
        }

        public void ClearPlant()
        {
            currentPlant = null;
            occupied = false;
            SetAvailableVisual(true);
        }

        private void EnsureMarker()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null)
            {
                box = gameObject.AddComponent<BoxCollider>();
            }

            // Larger tap target so markers under trees are still hittable.
            box.size = new Vector3(1.6f, 1.2f, 1.6f);
            box.center = new Vector3(0f, 0.4f, 0f);
            box.isTrigger = false;

            if (markerRenderer != null)
            {
                return;
            }

            var existing = transform.Find("PlotMarker");
            if (existing != null)
            {
                markerRenderer = existing.GetComponent<Renderer>();
                return;
            }

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = "PlotMarker";
            marker.transform.SetParent(transform, false);
            marker.transform.localPosition = Vector3.up * 0.03f;
            marker.transform.localScale = new Vector3(1.05f, 0.025f, 1.05f);
            var col = marker.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(col);
                }
                else
                {
                    Object.DestroyImmediate(col);
                }
            }

            markerRenderer = marker.GetComponent<Renderer>();
        }

        private void SetAvailableVisual(bool available)
        {
            if (markerRenderer == null)
            {
                return;
            }

            // Keep the plot collider interactive, but do not show floating green pads by default.
            markerRenderer.enabled = showAvailableMarker && available;
            if (!showAvailableMarker)
            {
                return;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            // Optional available-plot affordance; disabled by default to keep the starter island clean.
            var color = available
                ? new Color(0.25f, 0.85f, 0.3f, 0.85f)
                : new Color(0.45f, 0.45f, 0.45f, 0.25f);
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else
            {
                mat.color = color;
            }

            markerRenderer.sharedMaterial = mat;
            markerRenderer.enabled = available;
        }
    }
}
