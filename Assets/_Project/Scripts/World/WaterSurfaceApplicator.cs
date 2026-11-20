using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Upgrades Ocean / WaterSource to AFK Island/WaterWaves using shared wind globals.
    /// Materials live in Resources so the shader is not stripped from player builds.
    /// </summary>
    public sealed class WaterSurfaceApplicator : MonoBehaviour
    {
        private const string OceanMatResource = "VFX/AfkOceanWaves";
        private const string PondMatResource = "VFX/AfkPondWaves";

        [SerializeField] private int oceanResolution = 64;

        private static Mesh _oceanGrid;
        private Material _oceanMat;
        private Material _pondMat;
        private bool _applied;

        private void Awake()
        {
            Apply();
        }

        private void OnEnable()
        {
            if (!_applied)
            {
                Apply();
            }
        }

        public void Apply()
        {
            EnsureMaterials();

            var ocean = FindNamed("Ocean");
            if (ocean != null && _oceanMat != null)
            {
                UpgradeOcean(ocean);
            }

            var pond = FindNamed("WaterSource");
            if (pond != null && _pondMat != null)
            {
                UpgradePond(pond);
            }

            _applied = true;
        }

        private void UpgradeOcean(GameObject ocean)
        {
            var filter = ocean.GetComponent<MeshFilter>();
            var renderer = ocean.GetComponent<MeshRenderer>();
            if (filter == null || renderer == null)
            {
                return;
            }

            filter.sharedMesh = GetOrCreateOceanGrid(oceanResolution);
            renderer.sharedMaterial = _oceanMat;

            var col = ocean.GetComponent<MeshCollider>();
            if (col != null)
            {
                col.sharedMesh = filter.sharedMesh;
            }
        }

        private void UpgradePond(GameObject pond)
        {
            var renderer = pond.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                return;
            }

            renderer.sharedMaterial = _pondMat;
        }

        private void EnsureMaterials()
        {
            if (_oceanMat == null)
            {
                var src = Resources.Load<Material>(OceanMatResource);
                if (src != null)
                {
                    _oceanMat = new Material(src) { name = "AfkOceanWaves_Runtime", hideFlags = HideFlags.DontSave };
                }
            }

            if (_pondMat == null)
            {
                var src = Resources.Load<Material>(PondMatResource);
                if (src != null)
                {
                    _pondMat = new Material(src) { name = "AfkPondWaves_Runtime", hideFlags = HideFlags.DontSave };
                }
            }

            // Last resort: keep a solid URP tint instead of magenta Error shader.
            if (_oceanMat == null || _oceanMat.shader == null || !_oceanMat.shader.isSupported
                || _oceanMat.shader.name == "Hidden/InternalErrorShader")
            {
                Debug.LogWarning("[AFK Island] WaterWaves unavailable — falling back to URP Lit ocean.");
                _oceanMat = CreateLitFallback("AfkOceanFallback", new Color(0.12f, 0.42f, 0.62f, 1f));
            }

            if (_pondMat == null || _pondMat.shader == null || !_pondMat.shader.isSupported
                || _pondMat.shader.name == "Hidden/InternalErrorShader")
            {
                _pondMat = CreateLitFallback("AfkPondFallback", new Color(0.25f, 0.55f, 0.72f, 1f));
            }
        }

        private static Material CreateLitFallback(string name, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = name, hideFlags = HideFlags.DontSave };
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

        private static GameObject FindNamed(string objectName)
        {
            var all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == objectName)
                {
                    return all[i].gameObject;
                }
            }

            return null;
        }

        private static Mesh GetOrCreateOceanGrid(int resolution)
        {
            resolution = Mathf.Clamp(resolution, 16, 96);
            if (_oceanGrid != null && _oceanGrid.vertexCount == (resolution + 1) * (resolution + 1))
            {
                return _oceanGrid;
            }

            var verts = new Vector3[(resolution + 1) * (resolution + 1)];
            var norms = new Vector3[verts.Length];
            var uvs = new Vector2[verts.Length];
            var tris = new int[resolution * resolution * 6];

            for (var z = 0; z <= resolution; z++)
            {
                for (var x = 0; x <= resolution; x++)
                {
                    var i = z * (resolution + 1) + x;
                    var u = x / (float)resolution;
                    var v = z / (float)resolution;
                    verts[i] = new Vector3((u - 0.5f) * 10f, 0f, (v - 0.5f) * 10f);
                    norms[i] = Vector3.up;
                    uvs[i] = new Vector2(u, v);
                }
            }

            var t = 0;
            for (var z = 0; z < resolution; z++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    var i = z * (resolution + 1) + x;
                    tris[t++] = i;
                    tris[t++] = i + resolution + 1;
                    tris[t++] = i + 1;
                    tris[t++] = i + 1;
                    tris[t++] = i + resolution + 1;
                    tris[t++] = i + resolution + 2;
                }
            }

            var mesh = new Mesh
            {
                name = "AfkOceanGrid_" + resolution,
                indexFormat = verts.Length > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            _oceanGrid = mesh;
            return mesh;
        }

        private void OnDestroy()
        {
            if (_oceanMat != null)
            {
                Destroy(_oceanMat);
            }

            if (_pondMat != null)
            {
                Destroy(_pondMat);
            }
        }
    }
}
