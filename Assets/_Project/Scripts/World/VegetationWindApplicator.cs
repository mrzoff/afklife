using System.Collections.Generic;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Upgrades decor renderers to the shared VegetationWind shader (cached materials).
    /// Rocks are skipped. No per-instance Update.
    /// </summary>
    public static class VegetationWindApplicator
    {
        private static Shader _windShader;
        private static readonly Dictionary<int, Material> Cache = new();

        public static void ApplyToInstance(GameObject instance, bool isRock, string styleName = null)
        {
            if (instance == null || isRock)
            {
                return;
            }

            EnsureShader();
            if (_windShader == null)
            {
                return;
            }

            var sway = ResolveSway(string.IsNullOrEmpty(styleName) ? instance.name : styleName);
            var renderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.sharedMaterial == null)
                {
                    continue;
                }

                // Trunk and crown are two slots on one mesh. sharedMaterial only
                // replaces the first, so the leaves stayed still while the wood swayed.
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    continue;
                }

                for (var m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null)
                    {
                        continue;
                    }

                    mats[m] = GetWindMaterial(mats[m], sway);
                }

                r.sharedMaterials = mats;
            }
        }

        private static float ResolveSway(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return 0.35f;
            }

            // Planted Stage_* fallbacks (when Polyeler token was stripped from prefab name).
            if (name.StartsWith("Stage_Seed", System.StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Stage_0", System.StringComparison.OrdinalIgnoreCase))
            {
                return 0.78f;
            }

            if (name.StartsWith("Stage_Sprout", System.StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Stage_1", System.StringComparison.OrdinalIgnoreCase))
            {
                return 0.68f;
            }

            if (name.StartsWith("Stage_Young", System.StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Stage_2", System.StringComparison.OrdinalIgnoreCase))
            {
                return 0.46f;
            }

            if (name.StartsWith("Stage_Adult", System.StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Stage_3", System.StringComparison.OrdinalIgnoreCase))
            {
                return 0.4f;
            }

            if (name.IndexOf("Mugwort", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Viola", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Eurya", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("FallenLeaves", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.7f; // bush / flower / scrub
            }

            if (name.IndexOf("Bamboo", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Laurel", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.55f; // mid canopy
            }

            if (name.IndexOf("Cedar", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Pine", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Oak", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Cypress", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Redwood", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Cherry", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("PlaneTree", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.42f; // taller trunks — gentler sway
            }

            return 0.4f;
        }

        private static void EnsureShader()
        {
            if (_windShader == null)
            {
                _windShader = Shader.Find("AFK Island/VegetationWind");
            }
        }

        private static Material GetWindMaterial(Material source, float sway)
        {
            var cull = source.HasProperty("_Cull") ? source.GetFloat("_Cull") : 2f;
            var key = (source.name?.GetHashCode() ?? 0)
                      ^ (Mathf.RoundToInt(sway * 100f) * 397)
                      ^ (source.mainTexture != null ? source.mainTexture.name.GetHashCode() : 0)
                      ^ (Mathf.RoundToInt(cull) * 17);
            if (Cache.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            var mat = new Material(_windShader)
            {
                name = source.name + "_Wind",
                hideFlags = HideFlags.DontSave
            };

            if (source.HasProperty("_BaseMap") && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
            }
            else if (source.mainTexture != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", source.mainTexture);
            }

            var color = source.HasProperty("_BaseColor")
                ? source.GetColor("_BaseColor")
                : source.color;
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }

            // Leaf cards are authored double-sided. Bark stays Cull Back.
            if (mat.HasProperty("_Cull"))
            {
                mat.SetFloat("_Cull", cull);
            }

            // Stage 11E-4: leaf cards keep their cutout. Opaque bark stays solid.
            if (source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f)
            {
                mat.EnableKeyword("_ALPHATEST_ON");
                if (source.HasProperty("_Cutoff") && mat.HasProperty("_Cutoff"))
                {
                    mat.SetFloat("_Cutoff", source.GetFloat("_Cutoff"));
                }
            }

            if (mat.HasProperty("_SwayAmount"))
            {
                mat.SetFloat("_SwayAmount", sway);
            }

            Cache[key] = mat;
            return mat;
        }
    }
}
