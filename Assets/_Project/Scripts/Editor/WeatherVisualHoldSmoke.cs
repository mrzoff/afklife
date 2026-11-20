#if UNITY_EDITOR
using System;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Package D (11F): fog weather is a light distant haze, rain emits drops,
    /// weather does not recolor the sun, day/night and wind globals still update.
    /// </summary>
    public static class WeatherVisualHoldSmoke
    {
        private static readonly Color NightSun = new(0.35f, 0.4f, 0.7f);
        private static readonly Color DayAmbient = new(0.45f, 0.5f, 0.55f);
        private static readonly Color NightAmbient = new(0.08f, 0.1f, 0.18f);

        [MenuItem("AFK Island/Smoke Weather Visual Hold (11F-A)")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Weather Visual Hold smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Weather Visual Hold smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            GameObject sunGo = null;
            GameObject envGo = null;
            var fogBefore = RenderSettings.fog;
            var densityBefore = RenderSettings.fogDensity;

            try
            {
                Cleanup();

                var baseSun = new Color(0.2f, 0.8f, 0.3f);
                sunGo = new GameObject("SmokeSun");
                var light = sunGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = baseSun;
                light.intensity = 1.4f;
                var visual = sunGo.AddComponent<DayNightWeatherVisual>();

                envGo = new GameObject("SmokeWeatherEnv");
                envGo.AddComponent<WorldVisualEnvironment>();

                RenderSettings.fog = true;
                RenderSettings.fogDensity = 0.02f;

                var noonFog = SaveWith("fog", 0.5f);
                visual.ApplyFromSave(noonFog);
                var code = AssertDistantHaze(baseSun, 1.4f, "noon-fog");
                if (code != 0)
                {
                    return code;
                }

                var midnightRain = SaveWith("rain", 0f);
                visual.ApplyFromSave(midnightRain);
                code = AssertReadableNight("midnight-rain");
                if (code != 0)
                {
                    return code;
                }

                var noonWind = SaveWith("wind", 0.5f);
                visual.ApplyFromSave(noonWind);
                code = AssertReadableDay(baseSun, 1.4f, "noon-wind");
                if (code != 0)
                {
                    return code;
                }

                var wind = Shader.GetGlobalVector("_AfkWindParams");
                if (wind.z < 0.5f)
                {
                    Debug.LogError("Wind globals were not pushed for windy weather. z=" + wind.z);
                    return 8;
                }

                visual.ApplyFromSave(SaveWith("rain", 0.5f));
                var vfx = WorldAtmosphereVfx.EnsureIsolatedInstance();
                vfx.TickFollowAndState(forceState: true);
                code = AssertParticles(expectPollen: false, expectRain: true, label: "rain");
                if (code != 0)
                {
                    return code;
                }

                if (RenderSettings.fog)
                {
                    Debug.LogError("rain: fog should stay off");
                    return 15;
                }

                visual.ApplyFromSave(SaveWith("sunny", 0.5f));
                vfx.TickFollowAndState(forceState: true);
                code = AssertParticles(expectPollen: true, expectRain: false, label: "sunny");
                if (code != 0)
                {
                    return code;
                }

                Debug.Log("[AFK Island] Weather Visual Hold smoke OK.");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return 1;
            }
            finally
            {
                if (sunGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(sunGo);
                }

                if (envGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(envGo);
                }

                Cleanup();
                RenderSettings.fog = fogBefore;
                RenderSettings.fogDensity = densityBefore;
            }
        }

        private static int AssertDistantHaze(Color baseSun, float baseIntensity, string label)
        {
            if (!RenderSettings.fog)
            {
                Debug.LogError(label + ": distant haze was not enabled");
                return 16;
            }

            if (RenderSettings.fogMode != FogMode.ExponentialSquared)
            {
                Debug.LogError(label + ": fog mode " + RenderSettings.fogMode);
                return 17;
            }

            if (RenderSettings.fogDensity <= 0.001f || RenderSettings.fogDensity >= 0.01f)
            {
                Debug.LogError(label + ": haze density out of range " + RenderSettings.fogDensity);
                return 18;
            }

            return AssertSun(baseSun, baseIntensity, DayAmbient, label, day: true);
        }

        private static int AssertReadableDay(Color baseSun, float baseIntensity, string label)
        {
            if (RenderSettings.fog)
            {
                Debug.LogError(label + ": RenderSettings.fog stayed enabled");
                return 2;
            }

            return AssertSun(baseSun, baseIntensity, DayAmbient, label, day: true);
        }

        private static int AssertSun(Color expectedSun, float expectedIntensity, Color expectedAmbient, string label, bool day)
        {

            var sun = UnityEngine.Object.FindFirstObjectByType<Light>();
            if (sun == null)
            {
                Debug.LogError(label + ": sun missing");
                return 3;
            }

            if (!Close(sun.color, expectedSun) || Mathf.Abs(sun.intensity - expectedIntensity) > 0.02f)
            {
                Debug.LogError(label + ": sun recolored. color=" + sun.color + " intensity=" + sun.intensity);
                return day ? 4 : 6;
            }

            if (!Close(RenderSettings.ambientLight, expectedAmbient))
            {
                Debug.LogError(label + ": ambient recolored " + RenderSettings.ambientLight);
                return day ? 5 : 7;
            }

            return 0;
        }

        private static int AssertReadableNight(string label)
        {
            if (RenderSettings.fog)
            {
                Debug.LogError(label + ": RenderSettings.fog stayed enabled");
                return 2;
            }

            var sun = UnityEngine.Object.FindFirstObjectByType<Light>();
            if (sun == null)
            {
                Debug.LogError(label + ": sun missing");
                return 3;
            }

            if (!Close(sun.color, NightSun) || Mathf.Abs(sun.intensity - 0.25f) > 0.02f)
            {
                Debug.LogError(label + ": night sun was weather-tinted. color=" + sun.color + " intensity=" + sun.intensity);
                return 6;
            }

            if (!Close(RenderSettings.ambientLight, NightAmbient))
            {
                Debug.LogError(label + ": night ambient recolored " + RenderSettings.ambientLight);
                return 7;
            }

            return 0;
        }

        private static int AssertParticles(bool expectPollen, bool expectRain, string label)
        {
            var root = GameObject.Find(WorldAtmosphereVfx.IsolatedRootName);
            if (root == null)
            {
                Debug.LogError(label + ": VFX root missing");
                return 9;
            }

            var rain = root.transform.Find("Rain");
            var leaves = root.transform.Find("Leaves");
            var pollen = root.transform.Find("Pollen");
            if (rain == null || leaves == null || pollen == null)
            {
                Debug.LogError(label + ": particle children missing");
                return 10;
            }

            var rainRate = rain.GetComponent<ParticleSystem>().emission.rateOverTime.constant;
            if (expectRain && rainRate < 1f)
            {
                Debug.LogError(label + ": rain particles missing. rate=" + rainRate);
                return 11;
            }

            if (!expectRain && rainRate > 0.01f)
            {
                Debug.LogError(label + ": rain particles enabled at rate " + rainRate);
                return 11;
            }

            var leafRate = leaves.GetComponent<ParticleSystem>().emission.rateOverTime.constant;
            if (leafRate < 1f)
            {
                Debug.LogError(label + ": leaves not emitting. rate=" + leafRate);
                return 12;
            }

            var pollenRate = pollen.GetComponent<ParticleSystem>().emission.rateOverTime.constant;
            if (expectPollen && pollenRate < 1f)
            {
                Debug.LogError(label + ": pollen not emitting in clear weather. rate=" + pollenRate);
                return 13;
            }

            if (!expectPollen && pollenRate > 0.01f)
            {
                Debug.LogError(label + ": pollen emitted during rain. rate=" + pollenRate);
                return 14;
            }

            return 0;
        }

        private static GameSaveData SaveWith(string weatherId, float timeOfDay01)
        {
            return new GameSaveData
            {
                version = 8,
                weather = new WeatherSaveData
                {
                    weatherId = weatherId,
                    timeOfDay01 = timeOfDay01
                },
                worldGrowth = new WorldGrowthSaveData
                {
                    growthSeed = 17
                }
            };
        }

        private static bool Close(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.02f
                   && Mathf.Abs(a.g - b.g) < 0.02f
                   && Mathf.Abs(a.b - b.b) < 0.02f;
        }

        private static void Cleanup()
        {
            var root = GameObject.Find(WorldAtmosphereVfx.IsolatedRootName);
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            var sun = GameObject.Find("SmokeSun");
            if (sun != null)
            {
                UnityEngine.Object.DestroyImmediate(sun);
            }

            var env = GameObject.Find("SmokeWeatherEnv");
            if (env != null)
            {
                UnityEngine.Object.DestroyImmediate(env);
            }
        }
    }
}
#endif
