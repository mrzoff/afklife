using AfkIsland.Offline;
using UnityEngine;

namespace AfkIsland.UI
{
    public sealed class ReturnSummaryHud : MonoBehaviour
    {
        [SerializeField] private OfflineGrowthService offlineGrowth;
        [SerializeField] private bool visible = true;

        private string _overrideText;

        public void Configure(OfflineGrowthService offline)
        {
            offlineGrowth = offline;
            _overrideText = null;
        }

        public void ShowText(string text)
        {
            _overrideText = text;
            visible = true;
        }

        public void SetVisible(bool value)
        {
            visible = value;
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            var body = !string.IsNullOrEmpty(_overrideText)
                ? _overrideText
                : offlineGrowth != null
                    ? offlineGrowth.LastReturnSummary
                    : null;

            if (string.IsNullOrEmpty(body))
            {
                return;
            }

            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 15,
                padding = new RectOffset(12, 12, 12, 12),
                wordWrap = true
            };

            var text = "AFK ISLAND — отчёт возвращения\n\n" + body +
                       "\n\nКак понять расширение: смотри «Остров вырос» и новую землю у края острова " +
                       "(неровный берег, трава/почва, кусты и камни)." +
                       "\nТап по зелёной точке на острове = посадить дерево.";
            GUI.Box(new Rect(16, 16, 500, 320), text, style);
        }
    }
}
