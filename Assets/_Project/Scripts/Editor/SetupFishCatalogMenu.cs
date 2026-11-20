#if UNITY_EDITOR
using System.IO;
using AfkIsland.Fishing;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Contract 0.3 stage 2: create FishData assets + FishCatalog (Resources + SO folder).
    /// </summary>
    public static class SetupFishCatalogMenu
    {
        private const string SoFolder = "Assets/_Project/ScriptableObjects/Fish";
        private const string ResourcesFolder = "Assets/_Project/Resources";
        private const string CatalogAssetName = "FishCatalog.asset";

        [MenuItem("AFK Island/Setup Fish Catalog 0.3")]
        public static void Setup()
        {
            SetupQuiet();
            EditorUtility.DisplayDialog("AFK Island", "Fish catalog 0.3 ready.", "OK");
        }

        /// <summary>Idempotent asset create/update without dialog (for smoke/batch).</summary>
        public static void SetupQuiet()
        {
            RunInternal();
        }

        public static void RunBatch()
        {
            var code = 0;
            try
            {
                SetupQuiet();
                Debug.Log("[AFK Island] Setup Fish Catalog 0.3 OK.");
            }
            catch (System.Exception e)
            {
                code = 1;
                Debug.LogError("[AFK Island] Setup Fish Catalog failed: " + e);
            }

            EditorApplication.Exit(code);
        }

        private static void RunInternal()
        {
            DirectoryEnsure("Assets/_Project/ScriptableObjects");
            DirectoryEnsure(SoFolder);
            DirectoryEnsure(ResourcesFolder);

            var mackerel = EnsureFish(
                "fish_mackerel",
                "Скумбрия",
                "Обычная океанская рыба у берега.",
                FishRarity.Common,
                FishWaterType.Ocean,
                pondEligible: false,
                weather: null,
                time: FishTimeCondition.Any,
                weight: 1.2f,
                color: new Color(0.25f, 0.45f, 0.7f));

            var anchovy = EnsureFish(
                "fish_anchovy",
                "Анчоус",
                "Мелкая стайная рыба. Часто попадается новичкам.",
                FishRarity.Common,
                FishWaterType.Ocean,
                pondEligible: false,
                weather: null,
                time: FishTimeCondition.Any,
                weight: 1.5f,
                color: new Color(0.7f, 0.75f, 0.8f));

            var bass = EnsureFish(
                "fish_bass",
                "Морской окунь",
                "Любит волну и дождь у скал.",
                FishRarity.Uncommon,
                FishWaterType.Ocean,
                pondEligible: false,
                weather: new[] { WeatherSimulation.Rain, WeatherSimulation.Wind },
                time: FishTimeCondition.Any,
                weight: 0.7f,
                color: new Color(0.35f, 0.55f, 0.4f));

            var tuna = EnsureFish(
                "fish_tuna",
                "Тунец",
                "Крупный дневной улов в ясную погоду.",
                FishRarity.Rare,
                FishWaterType.Ocean,
                pondEligible: false,
                weather: new[] { WeatherSimulation.Sunny },
                time: FishTimeCondition.Day,
                weight: 0.35f,
                color: new Color(0.15f, 0.35f, 0.65f));

            // Catalog presence for P1; P0 ocean pool ignores pure pond species.
            var carp = EnsureFish(
                "fish_carp",
                "Карась",
                "Прудовая рыба. В P0 океане не ловится.",
                FishRarity.Common,
                FishWaterType.Pond,
                pondEligible: true,
                weather: null,
                time: FishTimeCondition.Any,
                weight: 1f,
                color: new Color(0.55f, 0.4f, 0.25f));

            var list = new[] { mackerel, anchovy, bass, tuna, carp };

            var soCatalog = EnsureCatalog($"{SoFolder}/{CatalogAssetName}", list);
            var resCatalog = EnsureCatalog($"{ResourcesFolder}/{CatalogAssetName}", list);

            EditorUtility.SetDirty(soCatalog);
            EditorUtility.SetDirty(resCatalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static FishData EnsureFish(
            string fishId,
            string displayName,
            string descriptionRu,
            FishRarity rarity,
            FishWaterType waterType,
            bool pondEligible,
            string[] weather,
            FishTimeCondition time,
            float weight,
            Color color)
        {
            var path = $"{SoFolder}/FishData_{fishId}.asset";
            var data = AssetDatabase.LoadAssetAtPath<FishData>(path);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<FishData>();
                AssetDatabase.CreateAsset(data, path);
            }

            data.fishId = fishId;
            data.displayName = displayName;
            data.descriptionRu = descriptionRu;
            data.rarity = rarity;
            data.waterType = waterType;
            data.pondEligible = pondEligible;
            data.weatherConditions = weather ?? System.Array.Empty<string>();
            data.timeConditions = time;
            data.catchWeight = weight;
            data.fallbackColor = color;
            EditorUtility.SetDirty(data);
            return data;
        }

        private static FishCatalog EnsureCatalog(string path, FishData[] list)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FishCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<FishCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }

            catalog.Configure(list);
            EditorUtility.SetDirty(catalog);
            return catalog;
        }

        private static void DirectoryEnsure(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return;
            }

            var parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            var name = Path.GetFileName(assetPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            {
                return;
            }

            if (!AssetDatabase.IsValidFolder(parent))
            {
                DirectoryEnsure(parent);
            }

            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
#endif
