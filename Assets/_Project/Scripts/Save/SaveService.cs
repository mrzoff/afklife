using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.Plants;
using AfkIsland.World;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AfkIsland.Save
{
    public sealed class SaveService : MonoBehaviour
    {
        /// <summary>Serializes all save file IO (read / write / bak restore).</summary>
        public static readonly object SaveIoLock = new object();

        public static SaveService Instance { get; private set; }

        private static bool pendingFreshStart;

        [SerializeField] private PlantingSystem plantingSystem;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private string fileName = "afk_island_save.json";

        private bool isResetting;
        private bool isLoading;
        private bool blockWritesDueToNewerSave;
        private GameSaveData _authoritative;
        private float _lastSaveNowUnscaled = -999f;
        private bool _pendingSave;
        private const float SaveNowMinGapSeconds = 0.75f;

        public GameSaveData AuthoritativeSnapshot => _authoritative;

        /// <summary>
        /// Android uses copy-based commit (no File.Replace). Editor/standalone keep Replace + fallback.
        /// </summary>
        public static bool UseAndroidCopyStrategy =>
            Application.platform == RuntimePlatform.Android;

        public static bool ConsumePendingFreshStart()
        {
            if (!pendingFreshStart)
            {
                return false;
            }

            pendingFreshStart = false;
            return true;
        }

        private string SavePath => Path.Combine(Application.persistentDataPath, fileName);

        public void Configure(PlantingSystem planting, ChunkManager chunks = null)
        {
            plantingSystem = planting;
            if (chunks != null)
            {
                chunkManager = chunks;
            }
        }

        public void BeginLoad() => isLoading = true;
        public void EndLoad() => isLoading = false;

        public void SetAuthoritative(GameSaveData data)
        {
            lock (SaveIoLock)
            {
                _authoritative = data;
            }
        }

        private void Awake()
        {
            Instance = this;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
        }

        public GameSaveData Capture()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Preserve worldGrowth + living-ecosystem fields from authoritative snapshot.
            GameSaveData prior;
            lock (SaveIoLock)
            {
                prior = _authoritative != null ? SaveClone.DeepClone(_authoritative) : null;
            }

            var data = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                worldGrowth = prior?.worldGrowth ?? SaveMigrationService.CreateDefaultWorldGrowth(now),
                ecosystem = prior?.ecosystem ?? SaveMigrationService.CreateDefaultEcosystem(),
                weather = prior?.weather ?? SaveMigrationService.CreateDefaultWeather(now),
                animals = prior?.animals ?? new List<AnimalSaveData>(),
                habitats = prior?.habitats ?? new List<HabitatSaveData>(),
                collection = prior?.collection,
                fishing = prior?.fishing
            };

            if (chunkManager != null)
            {
                data.chunks = chunkManager.CaptureLogicalChunks(now);
            }
            else
            {
                data.chunks = CaptureFallbackChunks(now);
            }

            data.plants = FlattenChunkPlants(data.chunks);
            EnsureHomeGrowth(data);
            SaveMigrationService.EnsureEcosystemWeatherAnimals(data);
            SaveMigrationService.EnsureCollectionAndFishingDefaults(data);
            return data;
        }

        public bool TryWrite(GameSaveData data, out string error)
        {
            error = null;
            lock (SaveIoLock)
            {
                if (blockWritesDueToNewerSave)
                {
                    error = "newer_save_blocked";
                    Debug.LogError("[AFK Island] Refusing to overwrite a newer unsupported save.");
                    return false;
                }

                if (data == null)
                {
                    error = "null_data";
                    return false;
                }

                data.version = SaveMigrationService.CurrentVersion;
                data.lastSaveUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                data.plants ??= new List<PlantSaveData>();
                data.chunks ??= new List<ChunkSaveData>();
                if (data.worldGrowth == null)
                {
                    data.worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(data.lastSaveUtc);
                }

                SaveMigrationService.EnsureEcosystemWeatherAnimals(data);
                SaveMigrationService.EnsureCollectionAndFishingDefaults(data);

                var json = JsonUtility.ToJson(data, true);
                var directory = Path.GetDirectoryName(SavePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = SavePath + ".tmp";
                var backupPath = SavePath + ".bak";

                try
                {
                    WriteAllTextExclusive(tempPath, json);
                    if (!IsLikelyReadableSaveFile(tempPath))
                    {
                        error = "temp_unreadable";
                        Debug.LogError("[AFK Island] Temp save failed readability check; .bak preserved.");
                        TryDeleteQuiet(tempPath);
                        return false;
                    }

                    if (!TryCommitSaveFiles(
                            SavePath,
                            tempPath,
                            backupPath,
                            UseAndroidCopyStrategy,
                            out error))
                    {
                        Debug.LogError($"[AFK Island] Save commit failed: {error}. .bak preserved; authoritative unchanged.");
                        return false;
                    }

                    if (!IsLikelyReadableSaveFile(SavePath))
                    {
                        error = "primary_unreadable_after_commit";
                        Debug.LogError("[AFK Island] Primary unreadable after commit; .bak preserved; authoritative unchanged.");
                        return false;
                    }

                    _authoritative = data;
                    Debug.Log(
                        $"[AFK Island] Saved v{data.version} -> {SavePath}" +
                        (UseAndroidCopyStrategy ? " (android-copy)" : " (replace)"));
                    return true;
                }
                catch (Exception e)
                {
                    error = e.Message;
                    Debug.LogError($"[AFK Island] Save write failed: {e}. .bak preserved; authoritative unchanged.");
                    TryDeleteQuiet(tempPath);
                    return false;
                }
            }
        }

        public void Write(GameSaveData data)
        {
            TryWrite(data, out _);
        }

        public bool TryRead(out GameSaveData data)
        {
            data = null;
            lock (SaveIoLock)
            {
                var backupPath = SavePath + ".bak";
                if (!File.Exists(SavePath) && !File.Exists(backupPath))
                {
                    return false;
                }

                if (TryReadPathUnlocked(SavePath, out data, out var newerBlocked))
                {
                    return true;
                }

                if (newerBlocked)
                {
                    return false;
                }

                if (File.Exists(backupPath) &&
                    TryReadPathUnlocked(backupPath, out data, out newerBlocked))
                {
                    // Verified .bak must also rewrite primary on disk before the next TryWrite.
                    if (TryRestorePrimaryFromVerifiedBackupUnlocked(SavePath, backupPath))
                    {
                        Debug.LogWarning("[AFK Island] Primary save unreadable; restored from .bak on disk.");
                    }
                    else
                    {
                        Debug.LogWarning(
                            "[AFK Island] Primary save unreadable; loaded .bak in memory, " +
                            "but on-disk primary restore failed. Next write will avoid clobbering .bak.");
                    }

                    return true;
                }

                if (newerBlocked)
                {
                    return false;
                }

                data = null;
                return false;
            }
        }

        /// <summary>
        /// Commit temp → primary. Android/forced copy: no File.Replace (delete+move / staged rename).
        /// Desktop: File.Replace with copy fallback. Never promotes corrupt primary into .bak.
        /// Caller must hold <see cref="SaveIoLock"/> (or use from tests via public wrapper).
        /// </summary>
        public static bool TryCommitSaveFiles(
            string savePath,
            string tempPath,
            string backupPath,
            bool forceCopyStrategy,
            out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(savePath) || string.IsNullOrEmpty(tempPath))
            {
                error = "bad_paths";
                return false;
            }

            if (!File.Exists(tempPath) || !IsLikelyReadableSaveFile(tempPath))
            {
                error = "temp_missing_or_unreadable";
                return false;
            }

            var stagedPrimary = savePath + ".new";
            var stagedBak = string.IsNullOrEmpty(backupPath) ? null : backupPath + ".tmp";

            try
            {
                var primaryUsable = IsLikelyReadableSaveFile(savePath);

                if (forceCopyStrategy)
                {
                    if (primaryUsable && !string.IsNullOrEmpty(backupPath))
                    {
                        // Stage bak via unique temp, then delete+move (never File.Copy onto locked .bak).
                        File.Copy(savePath, stagedBak, overwrite: true);
                        if (!IsLikelyReadableSaveFile(stagedBak))
                        {
                            error = "bak_stage_unreadable";
                            TryDeleteQuiet(stagedBak);
                            return false;
                        }

                        if (!TryInstallViaDeleteMove(stagedBak, backupPath, out error))
                        {
                            TryDeleteQuiet(stagedBak);
                            return false;
                        }
                    }
                    else if (File.Exists(savePath) && !primaryUsable)
                    {
                        // Corrupt/empty husk must never become .bak — remove husk so move can succeed.
                        Debug.LogWarning(
                            "[AFK Island] Primary unreadable; refusing to promote it into .bak.");
                        TryDeleteWithRetries(savePath);
                    }

                    // Install primary via staged file + delete + move (no overwrite of locked target).
                    File.Copy(tempPath, stagedPrimary, overwrite: true);
                    if (!IsLikelyReadableSaveFile(stagedPrimary))
                    {
                        error = "staged_primary_unreadable";
                        TryDeleteQuiet(stagedPrimary);
                        return false;
                    }

                    if (!TryInstallViaDeleteMove(stagedPrimary, savePath, out error))
                    {
                        TryDeleteQuiet(stagedPrimary);
                        return false;
                    }

                    TryDeleteQuiet(tempPath);

                    if (!IsLikelyReadableSaveFile(savePath))
                    {
                        error = "primary_unreadable_after_move";
                        return false;
                    }

                    return true;
                }

                // Desktop / Editor path
                if (!File.Exists(savePath))
                {
                    File.Copy(tempPath, savePath, overwrite: true);
                    TryDeleteQuiet(tempPath);
                    return IsLikelyReadableSaveFile(savePath);
                }

                if (primaryUsable && !string.IsNullOrEmpty(backupPath))
                {
                    File.Copy(savePath, backupPath, overwrite: true);
                    if (!IsLikelyReadableSaveFile(backupPath))
                    {
                        error = "bak_refresh_failed";
                        return false;
                    }
                }
                else if (!primaryUsable)
                {
                    Debug.LogWarning(
                        "[AFK Island] Primary unreadable; refusing to promote it into .bak.");
                    // Prefer copy onto primary without File.Replace (would clobber bak with husk).
                    File.Copy(tempPath, savePath, overwrite: true);
                    TryDeleteQuiet(tempPath);
                    return IsLikelyReadableSaveFile(savePath);
                }

                try
                {
                    File.Replace(tempPath, savePath, backupPath);
                    TryDeleteQuiet(tempPath);
                    return IsLikelyReadableSaveFile(savePath);
                }
                catch (Exception replaceEx)
                {
                    Debug.LogWarning($"[AFK Island] File.Replace failed, copy fallback: {replaceEx.Message}");
                    File.Copy(tempPath, savePath, overwrite: true);
                    TryDeleteQuiet(tempPath);
                    return IsLikelyReadableSaveFile(savePath);
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                TryDeleteQuiet(stagedPrimary);
                if (!string.IsNullOrEmpty(stagedBak))
                {
                    TryDeleteQuiet(stagedBak);
                }

                return false;
            }
        }

        /// <summary>
        /// Copy a verified .bak over the primary save without using File.Replace(..., bak).
        /// </summary>
        public static bool TryRestorePrimaryFromVerifiedBackup(string savePath, string backupPath)
        {
            lock (SaveIoLock)
            {
                return TryRestorePrimaryFromVerifiedBackupUnlocked(savePath, backupPath);
            }
        }

        private static bool TryRestorePrimaryFromVerifiedBackupUnlocked(string savePath, string backupPath)
        {
            if (string.IsNullOrEmpty(savePath) ||
                string.IsNullOrEmpty(backupPath) ||
                !File.Exists(backupPath))
            {
                return false;
            }

            if (!IsLikelyReadableSaveFile(backupPath))
            {
                return false;
            }

            var restoreTmp = savePath + ".restore.tmp";
            try
            {
                var directory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Copy(backupPath, restoreTmp, overwrite: true);
                File.Copy(restoreTmp, savePath, overwrite: true);
                TryDeleteQuiet(restoreTmp);

                // Keep .bak untouched until a later successful write rotates it.
                return File.Exists(savePath) && IsLikelyReadableSaveFile(savePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AFK Island] Primary restore from .bak failed: {e}");
                TryDeleteQuiet(restoreTmp);
                return false;
            }
        }

        /// <summary>
        /// Cheap pre-check used to avoid promoting garbage into .bak during writes.
        /// Full migrate/parse still happens in TryReadPath.
        /// </summary>
        public static bool IsLikelyReadableSaveFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return false;
                }

                var info = new FileInfo(path);
                if (info.Length < 8)
                {
                    return false;
                }

                string json;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    json = sr.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    return false;
                }

                var trimmed = json.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                {
                    return false;
                }

                if (!trimmed.Contains("\"version\""))
                {
                    return false;
                }

                var probe = JsonUtility.FromJson<GameSaveData>(json);
                return probe != null && probe.version > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadPathUnlocked(string path, out GameSaveData data, out bool newerUnsupportedBlocked)
        {
            data = null;
            newerUnsupportedBlocked = false;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                string json;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    json = sr.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.LogError($"[AFK Island] Save file is empty: {path}");
                    return false;
                }

                data = JsonUtility.FromJson<GameSaveData>(json);
                if (data == null)
                {
                    Debug.LogError($"[AFK Island] Failed to parse save JSON: {path}");
                    return false;
                }

                if (data.version > SaveMigrationService.CurrentVersion)
                {
                    newerUnsupportedBlocked = true;
                    blockWritesDueToNewerSave = true;
                    Debug.LogError(
                        $"[AFK Island] Save v{data.version} is newer than supported " +
                        $"{SaveMigrationService.CurrentVersion}. Writes blocked.");
                    data = null;
                    return false;
                }

                data = SaveMigrationService.MigrateIfNeeded(data);
                if (data == null)
                {
                    return false;
                }

                blockWritesDueToNewerSave = false;
                _authoritative = data;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AFK Island] Save read failed ({path}): {e}");
                data = null;
                return false;
            }
        }

        public bool HasSave()
        {
            lock (SaveIoLock)
            {
                return File.Exists(SavePath) || File.Exists(SavePath + ".bak");
            }
        }

        public void ResetProgressAndReload()
        {
            if (isResetting)
            {
                return;
            }

            isResetting = true;
            try
            {
                lock (SaveIoLock)
                {
                    if (File.Exists(SavePath))
                    {
                        File.Delete(SavePath);
                    }

                    foreach (var suffix in new[] { ".tmp", ".bak", ".restore.tmp" })
                    {
                        var p = SavePath + suffix;
                        if (File.Exists(p))
                        {
                            File.Delete(p);
                        }
                    }

                    blockWritesDueToNewerSave = false;
                    _authoritative = null;
                }

                pendingFreshStart = true;
                Debug.Log("[AFK Island] Progress reset.");
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            }
            catch (Exception e)
            {
                isResetting = false;
                Debug.LogError($"[AFK Island] Reset failed: {e}");
            }
        }

        /// <summary>
        /// CIF-A3: request a save. Debounced callers set pending instead of dropping the write.
        /// Use <paramref name="force"/> for pause/quit/critical catch so the flush is not skipped.
        /// </summary>
        public void SaveNow(bool force = false)
        {
            if (isResetting || isLoading || plantingSystem == null || blockWritesDueToNewerSave)
            {
                return;
            }

            _pendingSave = true;
            var now = Time.unscaledTime;
            if (!force && now - _lastSaveNowUnscaled < SaveNowMinGapSeconds)
            {
                return;
            }

            FlushPendingSave();
        }

        private void LateUpdate()
        {
            if (!_pendingSave || isResetting || isLoading || plantingSystem == null || blockWritesDueToNewerSave)
            {
                return;
            }

            if (Time.unscaledTime - _lastSaveNowUnscaled < SaveNowMinGapSeconds)
            {
                return;
            }

            FlushPendingSave();
        }

        private void FlushPendingSave()
        {
            if (!_pendingSave || isResetting || isLoading || plantingSystem == null || blockWritesDueToNewerSave)
            {
                return;
            }

            _pendingSave = false;
            _lastSaveNowUnscaled = Time.unscaledTime;
            TryWrite(Capture(), out _);
        }

        private static bool TryInstallViaDeleteMove(string sourcePath, string destPath, out string error)
        {
            error = null;
            try
            {
                TryDeleteWithRetries(destPath);
                File.Move(sourcePath, destPath);
                return true;
            }
            catch (Exception e)
            {
                // Last resort: copy then delete source (still no File.Replace).
                try
                {
                    File.Copy(sourcePath, destPath, overwrite: true);
                    TryDeleteQuiet(sourcePath);
                    return true;
                }
                catch (Exception e2)
                {
                    error = e2.Message;
                    Debug.LogError($"[AFK Island] Install move/copy failed ({destPath}): {e} / {e2}");
                    return false;
                }
            }
        }

        private static void TryDeleteWithRetries(string path, int attempts = 6)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    File.Delete(path);
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(15 * (i + 1));
                }
            }

            TryDeleteQuiet(path);
        }

        private static void WriteAllTextExclusive(string path, string contents)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sw.Write(contents);
            sw.Flush();
            fs.Flush(true);
        }

        private static void TryDeleteQuiet(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }

        private List<ChunkSaveData> CaptureFallbackChunks(long now)
        {
            var home = new ChunkSaveData
            {
                chunkX = IslandChunk.HomeChunkX,
                chunkZ = IslandChunk.HomeChunkZ,
                isUnlocked = true,
                biomeId = "starter_island",
                randomSeed = 0,
                lastSimulatedUtc = now,
                plants = new List<PlantSaveData>()
            };

            if (plantingSystem != null)
            {
                foreach (var plot in plantingSystem.Plots)
                {
                    if (plot == null || !plot.IsOccupied || plot.CurrentPlant == null)
                    {
                        continue;
                    }

                    var plant = plot.CurrentPlant;
                    home.plants.Add(new PlantSaveData
                    {
                        instanceId = plant.InstanceId,
                        plantId = plant.Data != null ? plant.Data.plantId : "unknown",
                        plotId = plot.PlotId,
                        stage = plant.StageIndex,
                        growthProgress = plant.StageProgress,
                        posX = plant.transform.position.x,
                        posY = plant.transform.position.y,
                        posZ = plant.transform.position.z
                    });
                }
            }

            return new List<ChunkSaveData> { home };
        }

        private static void EnsureHomeGrowth(GameSaveData data)
        {
            if (data.worldGrowth == null)
            {
                data.worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(data.lastSaveUtc);
            }

            data.worldGrowth.chunks ??= new List<ChunkGrowthSaveData>();
            var hasHome = false;
            foreach (var g in data.worldGrowth.chunks)
            {
                if (g != null && g.chunkX == 0 && g.chunkZ == 0)
                {
                    hasHome = true;
                    break;
                }
            }

            if (!hasHome)
            {
                data.worldGrowth.chunks.Add(SaveMigrationService.CreateGrowthRecord(0, 0, true));
            }
        }

        private static List<PlantSaveData> FlattenChunkPlants(List<ChunkSaveData> chunks)
        {
            var list = new List<PlantSaveData>();
            if (chunks == null)
            {
                return list;
            }

            foreach (var chunk in chunks)
            {
                if (chunk?.plants != null)
                {
                    list.AddRange(chunk.plants);
                }
            }

            return list;
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                SaveNow(force: true);
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                SaveNow(force: true);
            }
        }

        private void OnApplicationQuit() => SaveNow(force: true);

#if UNITY_EDITOR
        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                SaveNow(force: true);
            }
        }
#endif
    }
}
