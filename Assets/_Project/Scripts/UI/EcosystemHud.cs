using AfkIsland.Save;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.UI
{
    /// <summary>
    /// Compact ecosystem strip while playing (humidity / biodiversity / weather).
    /// </summary>
    public sealed class EcosystemHud : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private bool visible;

        public void Configure(SaveService save)
        {
            saveService = save;
        }

        public void SetVisible(bool value)
        {
            visible = value;
        }

        private void OnGUI()
        {
            if (!visible || saveService == null)
            {
                return;
            }

            var data = saveService.AuthoritativeSnapshot;
            if (data?.ecosystem == null)
            {
                return;
            }

            var eco = data.ecosystem;
            var weather = data.weather;
            var weatherRu = weather?.weatherId?.ToLowerInvariant() switch
            {
                "rain" => "дождь",
                "fog" => "туман",
                "wind" => "ветер",
                _ => "ясно"
            };
            var dayRu = WeatherSimulation.IsDaytime(weather) ? "день" : "ночь";
            var animals = data.animals != null ? data.animals.Count : 0;
            var habitats = data.habitats != null ? data.habitats.Count : 0;
            var settlement = SettlementSimulation.ToStatusLine(data);
            var residents = ResidentRoster.Count(data);
            if (residents > 0)
            {
                settlement += $" · жителей {residents}";
            }

            var label =
                $"Экосистема  влажн. {eco.humidity:0.00} · плодор. {eco.fertility:0.00} · " +
                $"биоразн. {eco.biodiversity:0.00} · {weatherRu}/{dayRu} · фауна {animals} · места {habitats}" +
                $"\n{settlement}";

            var style = new GUIStyle(GUI.skin.box)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                padding = new RectOffset(10, 10, 6, 6),
                normal = { textColor = new Color(0.9f, 0.95f, 0.88f) }
            };

            var w = Mathf.Min(Screen.width - 24f, 620f);
            GUI.Box(new Rect(12f, 12f, w, 56f), label, style);
        }
    }
}
