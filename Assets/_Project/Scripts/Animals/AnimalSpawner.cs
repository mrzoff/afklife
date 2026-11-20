using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Animals
{
    public sealed class AnimalSpawner : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private Transform animalsRoot;
        [SerializeField] private AnimalData[] catalog;

        private readonly List<AnimalInstance> _live = new();
        private readonly Dictionary<string, AnimalData> _byId = new();

        public void Configure(SaveService save, Transform root = null, AnimalData[] animals = null)
        {
            saveService = save;
            if (root != null)
            {
                animalsRoot = root;
            }

            if (animals != null)
            {
                catalog = animals;
            }

            RebuildCatalogIndex();
        }

        private void Awake()
        {
            if (animalsRoot == null)
            {
                var go = new GameObject("Animals");
                go.transform.SetParent(transform, false);
                animalsRoot = go.transform;
            }

            RebuildCatalogIndex();
        }

        public void RestoreFromSave(GameSaveData data = null)
        {
            data ??= saveService != null ? saveService.AuthoritativeSnapshot : null;
            ClearLive();
            if (data?.animals == null || data.animals.Count == 0)
            {
                return;
            }

            RebuildCatalogIndex();
            // CIF-A5: no save writes from visual restore. Land clamp runs in WorldGrowthService commit.

            foreach (var animal in data.animals)
            {
                if (animal == null || string.IsNullOrWhiteSpace(animal.animalId))
                {
                    continue;
                }

                SpawnVisual(animal);
            }
        }

        private void SpawnVisual(AnimalSaveData data)
        {
            _byId.TryGetValue(data.animalId, out var def);
            GameObject go;
            if (def != null && def.prefab != null)
            {
                go = Instantiate(def.prefab, animalsRoot);
                go.transform.localScale = def.visualScale;
            }
            else
            {
                Debug.LogWarning(
                    $"[AFK Island] Animal prefab missing for '{data.animalId}' — using fallback visual.");
                go = CreateFallbackVisual(data.animalId, def);
                go.transform.SetParent(animalsRoot, false);
            }

            go.name = $"Animal_{data.animalId}_{data.instanceId}";
            var instance = go.GetComponent<AnimalInstance>() ?? go.AddComponent<AnimalInstance>();
            instance.Configure(data, def);
            _live.Add(instance);
        }

        private GameObject CreateFallbackVisual(string animalId, AnimalData def)
        {
            var color = def != null
                ? def.fallbackColor
                : animalId switch
                {
                    FaunaIds.Butterfly => new Color(0.95f, 0.55f, 0.2f),
                    FaunaIds.Bird => new Color(0.52f, 0.46f, 0.38f),
                    FaunaIds.Frog => new Color(0.25f, 0.7f, 0.3f),
                    _ => Color.white
                };
            var scale = def != null
                ? def.visualScale
                : animalId switch
                {
                    FaunaIds.Butterfly => new Vector3(0.22f, 0.08f, 0.22f),
                    FaunaIds.Bird => new Vector3(0.28f, 0.18f, 0.35f),
                    FaunaIds.Frog => new Vector3(0.3f, 0.18f, 0.3f),
                    _ => Vector3.one * 0.25f
                };

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.localScale = scale;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")
                                                       ?? Shader.Find("Standard"))
                {
                    color = color
                };
            }

            return go;
        }

        private void ClearLive()
        {
            for (var i = 0; i < _live.Count; i++)
            {
                if (_live[i] != null)
                {
                    Destroy(_live[i].gameObject);
                }
            }

            _live.Clear();
            if (animalsRoot == null)
            {
                return;
            }

            for (var i = animalsRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(animalsRoot.GetChild(i).gameObject);
            }
        }

        private void RebuildCatalogIndex()
        {
            _byId.Clear();
            if (catalog == null || catalog.Length == 0)
            {
                var loaded = Resources.LoadAll<AnimalData>("Animals");
                if (loaded != null && loaded.Length > 0)
                {
                    catalog = loaded;
                }
            }

            if (catalog == null)
            {
                return;
            }

            foreach (var item in catalog)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.animalId))
                {
                    _byId[item.animalId] = item;
                }
            }
        }
    }
}
