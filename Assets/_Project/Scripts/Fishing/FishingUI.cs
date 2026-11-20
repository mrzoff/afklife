using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace AfkIsland.Fishing
{
    /// <summary>
    /// Contract 0.3 Stage 5: runtime uGUI fishing panel (mobile-safe CanvasScaler).
    /// UI never mutates FSM directly beyond calling controller request methods.
    /// </summary>
    public sealed class FishingUI : MonoBehaviour
    {
        private FishingController _controller;
        private Canvas _canvas;
        private GameObject _root;
        private Text _status;
        private Text _detail;
        private Button _castButton;
        private Button _reactButton;
        private Button _againButton;
        private Button _closeButton;
        private Image _biteFlash;
        private bool _built;

        public bool IsVisible => _root != null && _root.activeSelf;

        public void Configure(FishingController controller)
        {
            _controller = controller;
            EnsureBuilt();
            PresentClosed();
        }

        public void PresentClosed()
        {
            EnsureBuilt();
            if (_root != null)
            {
                _root.SetActive(false);
            }
        }

        public void PresentReady()
        {
            EnsureBuilt();
            _root.SetActive(true);
            SetTexts("Рыбалка", "Тапни «Забросить», когда будешь готов.");
            SetButtons(cast: true, react: false, again: false);
            SetBiteFlash(false);
        }

        public void PresentWait()
        {
            EnsureBuilt();
            _root.SetActive(true);
            SetTexts("Ожидание…", "Жди клёва. Не отпускай внимание.");
            SetButtons(cast: false, react: false, again: false);
            SetBiteFlash(false);
        }

        public void PresentBite()
        {
            EnsureBuilt();
            _root.SetActive(true);
            SetTexts("КЛЁВ!", "Жми «Подсечь»!");
            SetButtons(cast: false, react: true, again: false);
            SetBiteFlash(true);
        }

        public void PresentResult(FishingAttemptOutcome outcome, FishData fish, string message)
        {
            EnsureBuilt();
            _root.SetActive(true);
            SetBiteFlash(false);

            var title = outcome switch
            {
                FishingAttemptOutcome.Success => "Улов!",
                FishingAttemptOutcome.EmptyPool => "Тишина",
                FishingAttemptOutcome.Miss => "Промах",
                _ => "Результат"
            };
            var detail = string.IsNullOrWhiteSpace(message)
                ? (fish != null ? fish.displayName : "")
                : message;
            SetTexts(title, detail);
            SetButtons(cast: false, react: false, again: true);
        }

        private void EnsureBuilt()
        {
            if (_built)
            {
                return;
            }

            EnsureEventSystem();

            var canvasGo = new GameObject("FishingCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            _root = CreatePanel(canvasGo.transform, "FishingPanel");
            var panelRt = _root.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.06f, 0.08f);
            panelRt.anchorMax = new Vector2(0.94f, 0.42f);
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            _status = CreateText(_root.transform, "Status", 40, FontStyle.Bold, TextAnchor.MiddleCenter);
            var statusRt = _status.GetComponent<RectTransform>();
            statusRt.anchorMin = new Vector2(0.06f, 0.72f);
            statusRt.anchorMax = new Vector2(0.94f, 0.95f);
            statusRt.offsetMin = Vector2.zero;
            statusRt.offsetMax = Vector2.zero;

            _detail = CreateText(_root.transform, "Detail", 28, FontStyle.Normal, TextAnchor.UpperCenter);
            var detailRt = _detail.GetComponent<RectTransform>();
            detailRt.anchorMin = new Vector2(0.08f, 0.48f);
            detailRt.anchorMax = new Vector2(0.92f, 0.72f);
            detailRt.offsetMin = Vector2.zero;
            detailRt.offsetMax = Vector2.zero;

            _biteFlash = CreateImage(_root.transform, "BiteFlash", new Color(1f, 0.85f, 0.2f, 0.35f));
            var flashRt = _biteFlash.rectTransform;
            flashRt.anchorMin = Vector2.zero;
            flashRt.anchorMax = Vector2.one;
            flashRt.offsetMin = Vector2.zero;
            flashRt.offsetMax = Vector2.zero;
            _biteFlash.raycastTarget = false;
            _biteFlash.gameObject.SetActive(false);

            _castButton = CreateButton(_root.transform, "CastButton", "Забросить", new Vector2(0.08f, 0.12f), new Vector2(0.48f, 0.42f));
            _reactButton = CreateButton(_root.transform, "ReactButton", "Подсечь", new Vector2(0.52f, 0.12f), new Vector2(0.92f, 0.42f));
            _againButton = CreateButton(_root.transform, "AgainButton", "Ещё раз", new Vector2(0.08f, 0.12f), new Vector2(0.48f, 0.42f));
            // Top-right: never shares Cast/React/Again slots (Stage 7: Wait Close was eating Bite taps).
            _closeButton = CreateButton(_root.transform, "CloseButton", "Закрыть", new Vector2(0.72f, 0.78f), new Vector2(0.96f, 0.96f));

            _castButton.onClick.AddListener(() => _controller?.RequestCast());
            _reactButton.onClick.AddListener(() => _controller?.RequestReact());
            _againButton.onClick.AddListener(() => _controller?.RequestAgain());
            _closeButton.onClick.AddListener(() => _controller?.RequestClose());

            _built = true;
            _root.SetActive(false);
        }

        private void SetTexts(string status, string detail)
        {
            if (_status != null)
            {
                _status.text = status ?? "";
            }

            if (_detail != null)
            {
                _detail.text = detail ?? "";
            }
        }

        private void SetButtons(bool cast, bool react, bool again)
        {
            if (_castButton != null)
            {
                _castButton.gameObject.SetActive(cast);
            }

            if (_reactButton != null)
            {
                _reactButton.gameObject.SetActive(react);
            }

            if (_againButton != null)
            {
                _againButton.gameObject.SetActive(again);
            }

            if (_closeButton != null)
            {
                _closeButton.gameObject.SetActive(true);
            }
        }

        private void SetBiteFlash(bool on)
        {
            if (_biteFlash != null)
            {
                _biteFlash.gameObject.SetActive(on);
            }
        }

        private void Update()
        {
            if (_biteFlash != null && _biteFlash.gameObject.activeSelf)
            {
                var a = 0.25f + 0.25f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 10f));
                var c = _biteFlash.color;
                c.a = a;
                _biteFlash.color = c;
            }
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
            img.color = new Color(0.06f, 0.14f, 0.18f, 0.92f);
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

            var text = CreateText(go.transform, "Label", 30, FontStyle.Bold, TextAnchor.MiddleCenter);
            var textRt = text.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            text.text = label;
            return button;
        }
    }
}
