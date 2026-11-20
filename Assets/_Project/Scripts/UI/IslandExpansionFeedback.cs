using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.UI
{
    /// <summary>
    /// Status strip for AFK expansion. Grown land mesh is drawn by IslandMeshVisualizer.
    /// </summary>
    public sealed class IslandExpansionFeedback : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private WorldGrowthService worldGrowthService;
        [SerializeField] private bool showStatus = true;
        // Chunk pads retired once IslandMeshVisualizer draws grown cells.
        [SerializeField] private bool showMarkers;

        private readonly List<GameObject> _markers = new();
        private Transform _markerRoot;
        private string _sessionDeltaLine;
        private int _lastUnlocked = -1;
        private float _lastArea = -1f;

        public void Configure(SaveService save, ChunkManager chunks, WorldGrowthService worldGrowth)
        {
            saveService = save;
            chunkManager = chunks;
            worldGrowthService = worldGrowth;
        }

        public void RefreshFromSession()
        {
            var data = saveService != null ? saveService.AuthoritativeSnapshot : null;
            var unlocked = CountUnlocked(data);
            var area = data?.worldGrowth != null ? data.worldGrowth.totalExpandedArea : 0f;
            var result = worldGrowthService != null ? worldGrowthService.LastWorldResult : null;

            if (result != null && result.success && (result.expandedArea > 0.1f || result.chunksCreated > 0))
            {
                _sessionDeltaLine =
                    $"За этот AFK: +{result.expandedArea:0.#} клеток земли" +
                    (result.chunksCreated > 0 ? $", +{result.chunksCreated} новых участков" : string.Empty);
            }
            else if (result != null && result.success && result.appliedSeconds > 0)
            {
                _sessionDeltaLine = "За этот AFK остров пока не вырос (нужно больше времени отсутствия).";
            }
            else
            {
                _sessionDeltaLine = null;
            }

            _lastUnlocked = unlocked;
            _lastArea = area;
            RebuildMarkers(data);
        }

        private void LateUpdate()
        {
            var data = saveService != null ? saveService.AuthoritativeSnapshot : null;
            if (data == null)
            {
                return;
            }

            var unlocked = CountUnlocked(data);
            var area = data.worldGrowth != null ? data.worldGrowth.totalExpandedArea : 0f;
            if (unlocked != _lastUnlocked || !Mathf.Approximately(area, _lastArea))
            {
                _lastUnlocked = unlocked;
                _lastArea = area;
                RebuildMarkers(data);
            }
        }

        private void OnGUI()
        {
            if (!showStatus)
            {
                return;
            }

            var data = saveService != null ? saveService.AuthoritativeSnapshot : null;
            if (data == null)
            {
                return;
            }

            var unlocked = CountUnlocked(data);
            var area = data.worldGrowth != null ? data.worldGrowth.totalExpandedArea : 0f;
            var landCells = CountLandCells(data);
            var body =
                $"Остров (AFK): участков {unlocked} · клеток земли {landCells:0}" +
                (area > 0f ? $" · накопл. клеток роста {area:0.#}" : string.Empty);

            var report = worldGrowthService != null ? worldGrowthService.LastReturnReport : null;
            if (report != null && report.absenceSeconds > 0)
            {
                body += "\n" + AfkDurationFormat.FormatCatchUpTriple(
                    report.absenceSeconds,
                    report.worldAppliedSeconds,
                    report.catchUpRemainingSeconds);
            }

            if (!string.IsNullOrEmpty(_sessionDeltaLine))
            {
                body += "\n" + _sessionDeltaLine;
            }

            body += "\n" + SettlementSimulation.ToStatusLine(data);
            if (report != null && !string.IsNullOrWhiteSpace(report.settlementSummaryLine))
            {
                body += "\nЗа AFK: " + report.settlementSummaryLine;
            }

            body += "\nНовая земля от AFK — сглаженный берег и трава у края острова.";

            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 14,
                padding = new RectOffset(10, 10, 8, 8),
                wordWrap = true
            };

            var width = Mathf.Min(520f, Screen.width - 24f);
            var height = string.IsNullOrEmpty(_sessionDeltaLine) &&
                         (report == null || string.IsNullOrWhiteSpace(report.settlementSummaryLine))
                ? 100f
                : 124f;
            GUI.Box(new Rect(12f, Screen.height - height - 12f, width, height), body, style);
        }

        private void RebuildMarkers(GameSaveData data)
        {
            ClearMarkers();
            if (!showMarkers || data?.chunks == null || chunkManager == null)
            {
                return;
            }

            EnsureMarkerRoot();
            var size = chunkManager.ChunkSizeMeters;
            foreach (var chunk in data.chunks)
            {
                if (chunk == null || !chunk.isUnlocked)
                {
                    continue;
                }

                var isHome = chunk.chunkX == IslandChunk.HomeChunkX && chunk.chunkZ == IslandChunk.HomeChunkZ;
                var land = CountLandCellsInChunk(data, chunk.chunkX, chunk.chunkZ);
                if (isHome && land <= 0)
                {
                    land = ChunkMaskCodec.CellCount;
                }

                // Home already has scene island; still drop a thin ring so status matches markers.
                var marker = GameObject.CreatePrimitive(PrimitiveType.Quad);
                marker.name = $"ExpansionMarker_{chunk.chunkX}_{chunk.chunkZ}";
                marker.transform.SetParent(_markerRoot, false);
                marker.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                marker.transform.localPosition = new Vector3(
                    chunk.chunkX * size,
                    isHome ? 0.05f : 0.12f,
                    chunk.chunkZ * size);
                marker.transform.localScale = new Vector3(
                    size * (isHome ? 0.92f : 0.88f),
                    size * (isHome ? 0.92f : 0.88f),
                    1f);

                var col = marker.GetComponent<Collider>();
                if (col != null)
                {
                    Object.Destroy(col);
                }

                var renderer = marker.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    Color color;
                    if (isHome)
                    {
                        color = new Color(0.25f, 0.55f, 0.3f, 0.22f);
                    }
                    else
                    {
                        // Brighter for newly unlocked plots so AFK expansion is obvious.
                        var t = Mathf.Clamp01(land / 64f);
                        color = Color.Lerp(
                            new Color(0.45f, 0.75f, 0.35f, 0.55f),
                            new Color(0.2f, 0.55f, 0.25f, 0.75f),
                            t);
                    }

                    renderer.sharedMaterial = CreateMarkerMaterial(color);
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }

                _markers.Add(marker);
            }
        }

        private void EnsureMarkerRoot()
        {
            if (_markerRoot != null)
            {
                return;
            }

            var go = new GameObject("IslandExpansionMarkers");
            go.transform.SetParent(transform, false);
            _markerRoot = go.transform;
        }

        private void ClearMarkers()
        {
            for (var i = 0; i < _markers.Count; i++)
            {
                if (_markers[i] != null)
                {
                    Object.Destroy(_markers[i]);
                }
            }

            _markers.Clear();
        }

        private static int CountUnlocked(GameSaveData data)
        {
            if (data?.chunks == null)
            {
                return 0;
            }

            var n = 0;
            foreach (var c in data.chunks)
            {
                if (c != null && c.isUnlocked)
                {
                    n++;
                }
            }

            return n;
        }

        private static float CountLandCells(GameSaveData data)
        {
            if (data?.worldGrowth?.chunks == null)
            {
                return 0f;
            }

            var total = 0;
            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] != (byte)CellStage.Void)
                    {
                        total++;
                    }
                }
            }

            return total;
        }

        private static int CountLandCellsInChunk(GameSaveData data, int chunkX, int chunkZ)
        {
            if (data?.worldGrowth?.chunks == null)
            {
                return 0;
            }

            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null || growth.chunkX != chunkX || growth.chunkZ != chunkZ)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    return 0;
                }

                var n = 0;
                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] != (byte)CellStage.Void)
                    {
                        n++;
                    }
                }

                return n;
            }

            return 0;
        }

        private static Material CreateMarkerMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", color);
            }
            else
            {
                mat.color = color;
            }

            // Best-effort transparency for Unlit/Lit without a dedicated transparent asset.
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
            }

            if (mat.HasProperty("_ZWrite"))
            {
                mat.SetFloat("_ZWrite", 0f);
            }

            mat.renderQueue = 3000;
            return mat;
        }

        private void OnDestroy()
        {
            ClearMarkers();
        }
    }
}
