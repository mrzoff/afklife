using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.World
{
    /// <summary>
    /// Drives settlement residents. One director for the whole island: it picks a
    /// hut pair every tick and hands a route to a free resident. Residents never run
    /// their own AI loop and never write to the save.
    /// </summary>
    public sealed class ResidentDirector : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private float tickSeconds = 3f;
        [SerializeField] private int maxMovingResidents = 3;
        [SerializeField] private float moveSpeed = 0.7f;

        private readonly List<ResidentInstance> _residents = new();
        private readonly List<Vector3> _pathBuffer = new();
        private readonly ResidentPathGraph _graph = new();
        private Transform _root;
        private Material _bodyMat;
        private GameObject _prefabA;
        private GameObject _prefabB;
        private float _tickTimer;
        private int _tickCount;
        private int _cursor;
        private string _lastSignature;

        public int ResidentCount => _residents.Count;

        public void Configure(SaveService save)
        {
            saveService = save;
        }

        public void RebuildFromSave(GameSaveData data = null)
        {
            data ??= saveService != null ? saveService.AuthoritativeSnapshot : null;
            var signature = BuildSignature(data);
            if (signature == _lastSignature && _residents.Count > 0)
            {
                return;
            }

            _lastSignature = signature;
            ClearResidents();
            if (data?.worldGrowth?.chunks == null)
            {
                return;
            }

            EnsureRoot();
            EnsureVisualResources();
            _graph.Bind(data);

            var roster = ResidentRoster.Build(data);
            for (var i = 0; i < roster.Count; i++)
            {
                Spawn(roster[i]);
            }

            var blocked = 0;
            for (var i = 0; i < roster.Count; i++)
            {
                if (SettlementCellQuery.IsHutAtWorldXZ(data, roster[i].standWorld.x, roster[i].standWorld.z))
                {
                    blocked++;
                }
            }

            Debug.Log(
                $"[AFK Island] Residents rebuilt: {_residents.Count} from {roster.Count} hut cells, " +
                $"{blocked} stand on another hut cell.");

            _cursor = 0;
            _tickTimer = 0f;
        }

        private void LateUpdate()
        {
            var data = saveService != null ? saveService.AuthoritativeSnapshot : null;
            if (data != null)
            {
                var signature = BuildSignature(data);
                if (signature != _lastSignature)
                {
                    RebuildFromSave(data);
                }
            }

            var dt = Time.deltaTime;
            for (var i = 0; i < _residents.Count; i++)
            {
                if (_residents[i] != null)
                {
                    _residents[i].Advance(dt, moveSpeed);
                }
            }

            _tickTimer += dt;
            if (_tickTimer < tickSeconds)
            {
                return;
            }

            _tickTimer = 0f;
            _tickCount++;
            AssignJobs();
        }

        private void AssignJobs()
        {
            if (_residents.Count < 2 || maxMovingResidents <= 0)
            {
                return;
            }

            var moving = 0;
            for (var i = 0; i < _residents.Count; i++)
            {
                if (_residents[i] != null && !_residents[i].IsIdle)
                {
                    moving++;
                }
            }

            if (moving >= maxMovingResidents)
            {
                return;
            }

            var attempts = Mathf.Min(_residents.Count, maxMovingResidents * 2);
            for (var a = 0; a < attempts; a++)
            {
                if (moving >= maxMovingResidents)
                {
                    return;
                }

                var fromIndex = (_cursor + a) % _residents.Count;
                var from = _residents[fromIndex];
                if (from == null || !from.IsIdle)
                {
                    continue;
                }

                // Rotate the destination so the same pair is not chosen every tick.
                var toIndex = (fromIndex + 1 + _tickCount + a) % _residents.Count;
                var to = _residents[toIndex];
                if (to == null || ReferenceEquals(from, to))
                {
                    continue;
                }

                if (!_graph.TryFindPath(from.Entry, to.Entry, _pathBuffer))
                {
                    continue;
                }

                if (from.TryAssignPath(_pathBuffer))
                {
                    moving++;
                    _cursor = (fromIndex + 1) % _residents.Count;
                }
            }
        }

        private void Spawn(ResidentEntry entry)
        {
            var prefab = _prefabA != null && entry.slot % 2 == 1 ? _prefabB : _prefabA;
            GameObject go;
            if (prefab != null)
            {
                go = Instantiate(prefab, _root);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.transform.SetParent(_root, false);
                // Capsule primitive is 2 m tall at scale 1: 0.45 gives a readable ~0.9 m figure.
                go.transform.localScale = new Vector3(0.42f, 0.45f, 0.42f);
                var collider = go.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = _bodyMat;
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                }
            }

            go.name = $"Resident_{entry.residentId}";
            var instance = go.GetComponent<ResidentInstance>() ?? go.AddComponent<ResidentInstance>();
            instance.Initialize(entry);
            _residents.Add(instance);
        }

        private void EnsureRoot()
        {
            if (_root != null)
            {
                return;
            }

            var go = new GameObject("SettlementResidents");
            Transform parent = transform;
            var worldGo = GameObject.Find("World");
            if (worldGo != null)
            {
                parent = worldGo.transform;
            }

            go.transform.SetParent(parent, false);
            _root = go.transform;
        }

        private void EnsureVisualResources()
        {
            if (_bodyMat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit")
                             ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                             ?? Shader.Find("Standard");
                _bodyMat = new Material(shader) { name = "Settle_Resident" };
                var color = new Color(0.78f, 0.66f, 0.5f);
                if (_bodyMat.HasProperty("_BaseColor"))
                {
                    _bodyMat.SetColor("_BaseColor", color);
                }

                if (_bodyMat.HasProperty("_Color"))
                {
                    _bodyMat.SetColor("_Color", color);
                }
            }

            _prefabA ??= Resources.Load<GameObject>("AfkIsland/Settlement/Resident_A");
            _prefabB ??= Resources.Load<GameObject>("AfkIsland/Settlement/Resident_B");
        }

        private void ClearResidents()
        {
            for (var i = 0; i < _residents.Count; i++)
            {
                if (_residents[i] != null)
                {
                    Destroy(_residents[i].gameObject);
                }
            }

            _residents.Clear();
            if (_root == null)
            {
                return;
            }

            for (var i = _root.childCount - 1; i >= 0; i--)
            {
                Destroy(_root.GetChild(i).gameObject);
            }
        }

        private static string BuildSignature(GameSaveData data)
        {
            if (data?.worldGrowth?.chunks == null)
            {
                return "empty";
            }

            var parts = new List<string>(data.worldGrowth.chunks.Count);
            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth != null)
                {
                    parts.Add($"{growth.chunkX}:{growth.chunkZ}:{growth.settlementMaskBase64}");
                }
            }

            parts.Sort();
            return string.Join("|", parts);
        }

        private void OnDestroy()
        {
            ClearResidents();
            if (_bodyMat != null)
            {
                Destroy(_bodyMat);
                _bodyMat = null;
            }
        }
    }
}
