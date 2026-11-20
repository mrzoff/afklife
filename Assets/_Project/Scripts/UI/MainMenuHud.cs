using AfkIsland.Core;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.UI
{
    public sealed class MainMenuHud : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private GameBootstrap bootstrap;

        private bool visible = true;
        private bool showConfirm;

        public void Configure(SaveService save, GameBootstrap boot)
        {
            saveService = save;
            bootstrap = boot;
        }

        public void Show()
        {
            visible = true;
            showConfirm = false;
        }

        public void Hide()
        {
            visible = false;
            showConfirm = false;
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            var w = Mathf.Min(420f, Screen.width - 40f);
            var h = showConfirm ? 260f : 320f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 24f, rect.y + 20f, rect.width - 48f, rect.height - 40f));

            var title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            var subtitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            var button = new GUIStyle(GUI.skin.button) { fontSize = 18, fixedHeight = 44f };

            if (showConfirm)
            {
                GUILayout.Label("Новая игра", title);
                GUILayout.Space(8f);
                GUILayout.Label("Весь текущий прогресс будет удалён. Начать заново?", subtitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Начать заново", button))
                {
                    bootstrap?.ConfirmNewGame();
                }

                GUILayout.Space(8f);
                if (GUILayout.Button("Отмена", button))
                {
                    showConfirm = false;
                }
            }
            else
            {
                GUILayout.Label("AFK ISLAND", title);
                GUILayout.Space(6f);
                GUILayout.Label("Твой остров живёт, даже когда тебя нет", subtitle);
                GUILayout.FlexibleSpace();

                var hasSave = saveService != null && saveService.HasSave();
                GUI.enabled = hasSave;
                if (GUILayout.Button("ПРОДОЛЖИТЬ", button))
                {
                    bootstrap?.ContinueGame();
                }

                GUI.enabled = true;
                GUILayout.Space(10f);
                if (GUILayout.Button("НОВАЯ ИГРА", button))
                {
                    if (hasSave)
                    {
                        showConfirm = true;
                    }
                    else
                    {
                        bootstrap?.StartFreshSession();
                    }
                }

                GUILayout.Space(10f);
                if (GUILayout.Button("НАСТРОЙКИ", button))
                {
                    Debug.Log("[AFK Island] Settings placeholder");
                }
            }

            GUILayout.EndArea();
        }
    }
}
