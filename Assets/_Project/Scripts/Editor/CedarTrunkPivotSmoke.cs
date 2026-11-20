#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// JapaneseCedar mesh trunk sits on the prefab origin. The 10.3 m child offset must stay off.
    /// LOD screenRelativeHeight is not part of this check.
    /// </summary>
    public static class CedarTrunkPivotSmoke
    {
        private const float MaxCenterOffset = 0.75f;

        [MenuItem("AFK Island/Smoke Cedar Trunk Pivot")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Cedar trunk pivot smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Cedar trunk pivot smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            var code = CheckCedar("Assets/_Project/Resources/AfkIsland/Decor/JapaneseCedar.prefab");
            if (code != 0)
            {
                return code;
            }

            code = CheckCedar("Assets/Polyeler/EssentialNaturePack/Prefabs/JapaneseCedar.prefab");
            if (code != 0)
            {
                return code;
            }

            string[] stayed =
            {
                "Assets/_Project/Resources/AfkIsland/Decor/Pine.prefab",
                "Assets/_Project/Resources/AfkIsland/Decor/SawtoothOak.prefab",
                "Assets/_Project/Resources/AfkIsland/Decor/JapaneseCypress.prefab",
                "Assets/_Project/Resources/AfkIsland/Decor/Bamboo.prefab",
                "Assets/Polyeler/EssentialNaturePack/Prefabs/Pine.prefab",
                "Assets/Polyeler/EssentialNaturePack/Prefabs/SawtoothOak.prefab",
                "Assets/Polyeler/EssentialNaturePack/Prefabs/JapaneseCypress.prefab",
                "Assets/Polyeler/EssentialNaturePack/Prefabs/Bamboo.prefab"
            };
            for (var i = 0; i < stayed.Length; i++)
            {
                code = CheckChildrenNearOrigin(stayed[i]);
                if (code != 0)
                {
                    return 30 + code;
                }
            }

            return 0;
        }

        private static int CheckCedar(string path)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
            {
                Debug.LogError("Cedar pivot smoke: missing " + path);
                return 2;
            }

            var lodGroup = root.GetComponent<LODGroup>();
            if (lodGroup == null)
            {
                Debug.LogError("Cedar pivot smoke: no LODGroup " + path);
                return 3;
            }

            var lods = lodGroup.GetLODs();
            if (lods.Length < 2 ||
                Mathf.Abs(lods[0].screenRelativeTransitionHeight - 0.02f) > 0.0001f)
            {
                Debug.LogError(
                    "Cedar pivot smoke: LOD0 threshold moved on " + path +
                    " -> " + (lods.Length > 0 ? lods[0].screenRelativeTransitionHeight : -1f));
                return 4;
            }

            var childCode = CheckChildrenNearOrigin(path);
            if (childCode != 0)
            {
                return childCode;
            }

            var instance = UnityEngine.Object.Instantiate(root);
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            try
            {
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length < 2)
                {
                    Debug.LogError("Cedar pivot smoke: expected both LODs on " + path);
                    return 5;
                }

                var bounds = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                var offset = new Vector2(bounds.center.x, bounds.center.z).magnitude;
                Debug.Log(
                    $"[AFK Island] Cedar bounds {path} center=({bounds.center.x:0.00},{bounds.center.y:0.00},{bounds.center.z:0.00}) " +
                    $"xz={offset:0.00} size={bounds.size.magnitude:0.00}");
                if (offset > MaxCenterOffset)
                {
                    Debug.LogError("Cedar pivot smoke: drawn center is off the trunk on " + path);
                    return 6;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            return 0;
        }

        private static int CheckChildrenNearOrigin(string path)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
            {
                Debug.LogError("Cedar pivot smoke: missing " + path);
                return 2;
            }

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == root.transform)
                {
                    continue;
                }

                var xz = new Vector2(t.localPosition.x, t.localPosition.z).magnitude;
                if (xz > MaxCenterOffset)
                {
                    Debug.LogError(
                        $"Cedar pivot smoke: {path}/{t.name} local XZ {xz:0.00} m");
                    return 7;
                }
            }

            return 0;
        }
    }
}
#endif
