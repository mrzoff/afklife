using System.Collections.Generic;
using AfkIsland.CameraControl;
using AfkIsland.Collection;
using AfkIsland.Fishing;
using AfkIsland.Plants;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace AfkIsland.UI
{
    /// <summary>
    /// Stage 8A/8B: read-only collection modal + Play HUD entry.
    /// Does not write Save / fishing FSM.
    /// </summary>
    public sealed class CollectionUI : MonoBehaviour
    {
        private CollectionService _collection;
        private FishCatalog _catalog;
        private FishingController _fishing;
        private FishingOceanInput _oceanInput;
        private PlantingSystem _planting;
        private IsometricCameraController _cameraController;

        private Canvas _canvas;
        private GameObject _entryRoot;
        private GameObject _panelRoot;
        private Text _progressText;
        private RectTransform _listContent;
        private readonly List<GameObject> _rowPool = new(16);
        private bool _built;
        private bool _playEntryAllowed;
        private bool _worldSuppressed;

        public bool IsOpen => _panelRoot != null && _panelRoot.activeSelf;

        /// <summary>Stage 8A API: bind progress + catalog.</summary>
        public void Configure(CollectionService collection, FishCatalog catalog)
        {
            _collection = collection;
            _catalog = catalog;
            EnsureBuilt();
            Close();
            SetPlayEntryVisible(false);
        }

        /// <summary>
        /// Stage 8B: wire fishing close + world-input suppress without changing fishing FSM.
        /// </summary>
        public void WireGameplay(
            FishingController fishing = null,
            FishingOceanInput oceanInput = null,
            PlantingSystem planting = null,
            IsometricCameraController cameraController = null)
        {
            _fishing = fishing;
            _oceanInput = oceanInput;
            _planting = planting;
            _cameraController = cameraController;
        }

        public void SetPlayEntryVisible(bool visible)
        {
            _playEntryAllowed = visible;
            EnsureBuilt();
            RefreshEntryVisibility();
            if (!visible && IsOpen)
            {
                Close();
            }
        }

        public void Open()
        {
            EnsureBuilt();
            if (_fishing != null && _fishing.IsOpen)
            {
                // Cancels Wait/Bite with no catch progress (existing RequestClose contract).
                _fishing.RequestClose();
            }

            RebuildList();
            _panelRoot.SetActive(true);
            SuppressWorldInput(true);
            RefreshEntryVisibility();
        }

        public void Close()
        {
            EnsureBuilt();
            if (_panelRoot != null)
            {
                _panelRoot.SetActive(false);
            }

            SuppressWorldInput(false);
            RefreshEntryVisibility();
        }

        private void RefreshEntryVisibility()
        {
            if (_entryRoot == null)
            {
                return;
            }

            _entryRoot.SetActive(_playEntryAllowed && !IsOpen);
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }

            // Keep suppress after fishing RequestClose (FishingOceanInput may restore briefly).
            ApplyWorldSuppress();
        }

        private void OnDisable()
        {
            SuppressWorldInput(false);
        }

        private void SuppressWorldInput(bool suppress)
        {
            if (suppress == _worldSuppressed)
            {
                if (suppress)
                {
                    ApplyWorldSuppress();
                }

                return;
            }

            if (suppress)
            {
                ApplyWorldSuppress();
            }
            else if (_playEntryAllowed)
            {
                _oceanInput?.SetInputEnabled(true);
                if (_planting != null)
                {
                    _planting.enabled = true;
                }

                if (_cameraController != null)
                {
                    _cameraController.enabled = true;
                }
            }

            _worldSuppressed = suppress;
        }

        private void ApplyWorldSuppress()
        {
            // Ocean input off → its Update will not re-enable planting/camera while _inputEnabled is false.
            _oceanInput?.SetInputEnabled(false);
            if (_planting != null)
            {
                _planting.enabled = false;
            }

            if (_cameraController != null)
            {
                _cameraController.enabled = false;
            }
        }

        private void EnsureBuilt()
        {
            if (_built)
            {
                return;
            }

            EnsureEventSystem();

            var canvasGo = new GameObject("CollectionCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 90;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            BuildEntryButton(canvasGo.transform);
            BuildPanel(canvasGo.transform);
            _built = true;
            _panelRoot.SetActive(false);
            _entryRoot.SetActive(false);
        }

        private void BuildEntryButton(Transform canvas)
        {
            _entryRoot = new GameObject("CollectionEntry", typeof(RectTransform));
            _entryRoot.transform.SetParent(canvas, false);
            var rt = _entryRoot.GetComponent<RectTransform>();
            // Top-right: below freeform caption, clear of Ecosystem (top-left) and demo AFK (bottom-right).
            rt.anchorMin = new Vector2(0.72f, 0.82f);
            rt.anchorMax = new Vector2(0.97f, 0.89f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var img = _entryRoot.AddComponent<Image>();
            img.color = new Color(0.16f, 0.36f, 0.34f, 0.95f);
            var button = _entryRoot.AddComponent<Button>();
            button.targetGraphic = img;
            button.onClick.AddListener(Open);

            var label = CreateText(_entryRoot.transform, "Label", 26, FontStyle.Bold, TextAnchor.MiddleCenter);
            StretchFull(label.rectTransform);
            label.text = "Коллекция";
        }

        private void BuildPanel(Transform canvas)
        {
            var dimGo = new GameObject("CollectionDim", typeof(RectTransform));
            dimGo.transform.SetParent(canvas, false);
            _panelRoot = dimGo;
            StretchFull(dimGo.GetComponent<RectTransform>());
            var dimImg = dimGo.AddComponent<Image>();
            dimImg.color = new Color(0.02f, 0.06f, 0.08f, 0.55f);

            var panel = CreatePanel(dimGo.transform, "CollectionPanel");
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.06f, 0.12f);
            panelRt.anchorMax = new Vector2(0.94f, 0.88f);
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            var title = CreateText(panel.transform, "Title", 40, FontStyle.Bold, TextAnchor.MiddleCenter);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0.06f, 0.88f);
            titleRt.anchorMax = new Vector2(0.72f, 0.98f);
            titleRt.offsetMin = Vector2.zero;
            titleRt.offsetMax = Vector2.zero;
            title.text = "Коллекция";

            _progressText = CreateText(panel.transform, "Progress", 24, FontStyle.Normal, TextAnchor.MiddleLeft);
            var progressRt = _progressText.rectTransform;
            progressRt.anchorMin = new Vector2(0.06f, 0.80f);
            progressRt.anchorMax = new Vector2(0.72f, 0.88f);
            progressRt.offsetMin = Vector2.zero;
            progressRt.offsetMax = Vector2.zero;

            var section = CreateText(panel.transform, "Section", 28, FontStyle.Bold, TextAnchor.MiddleLeft);
            var sectionRt = section.rectTransform;
            sectionRt.anchorMin = new Vector2(0.06f, 0.72f);
            sectionRt.anchorMax = new Vector2(0.5f, 0.80f);
            sectionRt.offsetMin = Vector2.zero;
            sectionRt.offsetMax = Vector2.zero;
            section.text = "Рыбы";

            var close = CreateButton(
                panel.transform,
                "CloseButton",
                "Закрыть",
                new Vector2(0.72f, 0.88f),
                new Vector2(0.96f, 0.98f));
            close.onClick.AddListener(Close);

            var scrollGo = new GameObject("FishScroll", typeof(RectTransform));
            scrollGo.transform.SetParent(panel.transform, false);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = new Vector2(0.04f, 0.04f);
            scrollRt.anchorMax = new Vector2(0.96f, 0.70f);
            scrollRt.offsetMin = Vector2.zero;
            scrollRt.offsetMax = Vector2.zero;

            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(scrollGo.transform, false);
            var viewportRt = viewport.GetComponent<RectTransform>();
            StretchFull(viewportRt);
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            var viewportImg = viewport.AddComponent<Image>();
            viewportImg.color = new Color(0f, 0f, 0f, 0.02f);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            _listContent = content.GetComponent<RectTransform>();
            _listContent.anchorMin = new Vector2(0f, 1f);
            _listContent.anchorMax = new Vector2(1f, 1f);
            _listContent.pivot = new Vector2(0.5f, 1f);
            _listContent.offsetMin = Vector2.zero;
            _listContent.offsetMax = Vector2.zero;

            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;

            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.viewport = viewportRt;
            scroll.content = _listContent;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
        }

        private void RebuildList()
        {
            for (var i = 0; i < _rowPool.Count; i++)
            {
                if (_rowPool[i] != null)
                {
                    Destroy(_rowPool[i]);
                }
            }

            _rowPool.Clear();

            // Re-read CollectionService on every Open().
            var rows = CollectionFishListBuilder.Build(_catalog, _collection);
            var discovered = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Discovered)
                {
                    discovered++;
                }
            }

            if (_progressText != null)
            {
                _progressText.text = $"{discovered} / {rows.Count} открыто";
            }

            for (var i = 0; i < rows.Count; i++)
            {
                _rowPool.Add(CreateRow(_listContent, rows[i]));
            }

            if (_listContent != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_listContent);
            }
        }

        private static GameObject CreateRow(Transform parent, CollectionFishRowModel row)
        {
            var go = new GameObject("FishRow_" + (row.FishId ?? "unknown"), typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 110f;
            le.preferredHeight = 110f;

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.16f, 0.18f, 0.9f);

            var plate = CreateImage(go.transform, "Plate", row.PlateColor);
            var plateRt = plate.rectTransform;
            plateRt.anchorMin = new Vector2(0.03f, 0.12f);
            plateRt.anchorMax = new Vector2(0.18f, 0.88f);
            plateRt.offsetMin = Vector2.zero;
            plateRt.offsetMax = Vector2.zero;

            if (row.Icon != null)
            {
                plate.sprite = row.Icon;
                plate.preserveAspect = true;
                plate.color = row.Discovered
                    ? Color.white
                    : new Color(0.15f, 0.15f, 0.18f, 0.95f);
            }

            var name = CreateText(go.transform, "Name", 30, FontStyle.Bold, TextAnchor.MiddleLeft);
            var nameRt = name.rectTransform;
            nameRt.anchorMin = new Vector2(0.22f, 0.48f);
            nameRt.anchorMax = new Vector2(0.72f, 0.92f);
            nameRt.offsetMin = Vector2.zero;
            nameRt.offsetMax = Vector2.zero;
            name.text = row.Title;

            var rarity = CreateText(go.transform, "Rarity", 22, FontStyle.Normal, TextAnchor.MiddleLeft);
            var rarityRt = rarity.rectTransform;
            rarityRt.anchorMin = new Vector2(0.22f, 0.10f);
            rarityRt.anchorMax = new Vector2(0.72f, 0.48f);
            rarityRt.offsetMin = Vector2.zero;
            rarityRt.offsetMax = Vector2.zero;
            rarity.text = row.RarityLabel;
            rarity.color = row.Discovered
                ? new Color(0.75f, 0.85f, 0.8f, 1f)
                : new Color(0.45f, 0.5f, 0.48f, 1f);

            var count = CreateText(go.transform, "Count", 28, FontStyle.Bold, TextAnchor.MiddleRight);
            var countRt = count.rectTransform;
            countRt.anchorMin = new Vector2(0.74f, 0.20f);
            countRt.anchorMax = new Vector2(0.96f, 0.80f);
            countRt.offsetMin = Vector2.zero;
            countRt.offsetMax = Vector2.zero;
            count.text = row.CountLabel;

            return go;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        private static GameObject CreatePanel(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.06f, 0.14f, 0.18f, 0.96f);
            return go;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            int fontSize,
            FontStyle style,
            TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null)
            {
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = anchor;
            text.color = new Color(0.9f, 0.95f, 0.92f, 1f);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static Image CreateImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private static Button CreateButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var img = go.AddComponent<Image>();
            img.color = new Color(0.18f, 0.42f, 0.38f, 1f);
            var button = go.AddComponent<Button>();
            button.targetGraphic = img;

            var text = CreateText(go.transform, "Label", 26, FontStyle.Bold, TextAnchor.MiddleCenter);
            StretchFull(text.rectTransform);
            text.text = label;
            return button;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }

    /// <summary>Pure row model for UI + editor smoke (no Save writes).</summary>
    public readonly struct CollectionFishRowModel
    {
        public readonly string FishId;
        public readonly bool Discovered;
        public readonly string Title;
        public readonly string RarityLabel;
        public readonly string CountLabel;
        public readonly Color PlateColor;
        public readonly Sprite Icon;

        public CollectionFishRowModel(
            string fishId,
            bool discovered,
            string title,
            string rarityLabel,
            string countLabel,
            Color plateColor,
            Sprite icon)
        {
            FishId = fishId;
            Discovered = discovered;
            Title = title;
            RarityLabel = rarityLabel;
            CountLabel = countLabel;
            PlateColor = plateColor;
            Icon = icon;
        }
    }

    /// <summary>
    /// Builds unique valid catalog rows; skips null / empty fishId; first fishId wins on duplicates.
    /// </summary>
    public static class CollectionFishListBuilder
    {
        public static List<CollectionFishRowModel> Build(FishCatalog catalog, CollectionService collection)
        {
            var rows = new List<CollectionFishRowModel>(8);
            if (catalog?.Fish == null)
            {
                return rows;
            }

            var seen = new HashSet<string>();
            foreach (var fish in catalog.Fish)
            {
                if (fish == null || string.IsNullOrWhiteSpace(fish.fishId))
                {
                    continue;
                }

                if (!seen.Add(fish.fishId))
                {
                    continue;
                }

                var discovered = collection != null && collection.IsDiscovered(fish.fishId);
                var count = collection != null ? collection.GetDiscoveryCount(fish.fishId) : 0;
                if (discovered)
                {
                    rows.Add(new CollectionFishRowModel(
                        fish.fishId,
                        true,
                        string.IsNullOrWhiteSpace(fish.displayName) ? fish.fishId : fish.displayName,
                        RarityRu(fish.rarity),
                        "×" + Mathf.Max(0, count),
                        fish.fallbackColor,
                        fish.icon));
                }
                else
                {
                    var dim = fish.fallbackColor * 0.22f;
                    dim.a = 0.95f;
                    rows.Add(new CollectionFishRowModel(
                        fish.fishId,
                        false,
                        "???",
                        "—",
                        "",
                        dim,
                        fish.icon));
                }
            }

            return rows;
        }

        public static string RarityRu(FishRarity rarity)
        {
            return rarity switch
            {
                FishRarity.Common => "Обычная",
                FishRarity.Uncommon => "Необычная",
                FishRarity.Rare => "Редкая",
                FishRarity.Legendary => "Легендарная",
                _ => "—"
            };
        }
    }
}
