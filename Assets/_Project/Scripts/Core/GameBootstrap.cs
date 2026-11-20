using AfkIsland.Animals;
using AfkIsland.CameraControl;
using AfkIsland.Collection;
using AfkIsland.Fishing;
using AfkIsland.Offline;
using AfkIsland.Plants;
using AfkIsland.Save;
using AfkIsland.UI;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Core
{
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private OfflineGrowthService offlineGrowth;
        [SerializeField] private WorldGrowthService worldGrowthService;
        [SerializeField] private ReturnSummaryHud returnSummaryHud;
        [SerializeField] private ReturnReportHud returnReportHud;
        [SerializeField] private IslandExpansionFeedback islandExpansionFeedback;
        [SerializeField] private IslandMeshVisualizer islandMeshVisualizer;
        [SerializeField] private HomeSettlementVisualizer homeSettlementVisualizer;
        [SerializeField] private ResidentDirector residentDirector;
        [SerializeField] private PrototypeWorldGrowthDebug prototypeWorldGrowthDebug;
        [SerializeField] private MainMenuHud mainMenuHud;
        [SerializeField] private PlantingSystem plantingSystem;
        [SerializeField] private OnlineGrowthTicker onlineGrowthTicker;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private AnimalSpawner animalSpawner;
        [SerializeField] private DayNightWeatherVisual dayNightWeatherVisual;
        [SerializeField] private WorldVisualEnvironment worldVisualEnvironment;
        [SerializeField] private WorldAtmosphereVfx worldAtmosphereVfx;
        [SerializeField] private WaterSurfaceApplicator waterSurfaceApplicator;
        [SerializeField] private EcosystemHud ecosystemHud;
        [SerializeField] private CollectionService collectionService;
        [SerializeField] private CollectionUI collectionUI;
        [SerializeField] private FishingController fishingController;
        [SerializeField] private FishingUI fishingUI;
        [SerializeField] private FishingOceanInput fishingOceanInput;
        [SerializeField] private FishingPondInput fishingPondInput;
        [SerializeField] private FishCatalog fishCatalog;
        [SerializeField] private IsometricCameraController cameraController;

        public CollectionService Collection => collectionService;
        public CollectionUI CollectionScreen => collectionUI;
        public FishingController Fishing => fishingController;

        public void Configure(
            SaveService save,
            OfflineGrowthService offline,
            ReturnSummaryHud hud,
            MainMenuHud menu = null,
            PlantingSystem planting = null,
            OnlineGrowthTicker ticker = null,
            ChunkManager chunks = null,
            WorldGrowthService worldGrowth = null,
            IslandExpansionFeedback expansionFeedback = null,
            IslandMeshVisualizer meshVisualizer = null,
            HomeSettlementVisualizer settlementVisualizer = null,
            PrototypeWorldGrowthDebug growthDebug = null,
            ReturnReportHud reportHud = null,
            AnimalSpawner animals = null,
            DayNightWeatherVisual weatherVisual = null,
            EcosystemHud ecoHud = null,
            WorldVisualEnvironment visualEnv = null,
            WorldAtmosphereVfx atmosphere = null,
            ResidentDirector residents = null)
        {
            saveService = save;
            offlineGrowth = offline;
            returnSummaryHud = hud;
            mainMenuHud = menu;
            plantingSystem = planting;
            onlineGrowthTicker = ticker;
            chunkManager = chunks;
            worldGrowthService = worldGrowth;
            islandExpansionFeedback = expansionFeedback;
            islandMeshVisualizer = meshVisualizer;
            homeSettlementVisualizer = settlementVisualizer;
            residentDirector = residents;
            prototypeWorldGrowthDebug = growthDebug;
            returnReportHud = reportHud;
            animalSpawner = animals;
            dayNightWeatherVisual = weatherVisual;
            worldVisualEnvironment = visualEnv;
            worldAtmosphereVfx = atmosphere;
            ecosystemHud = ecoHud;
        }

        private void Awake()
        {
            plantingSystem ??= GetComponent<PlantingSystem>();
            onlineGrowthTicker ??= GetComponent<OnlineGrowthTicker>();
            saveService ??= GetComponent<SaveService>();
            offlineGrowth ??= GetComponent<OfflineGrowthService>();
            returnSummaryHud ??= GetComponent<ReturnSummaryHud>();
            returnReportHud ??= GetComponent<ReturnReportHud>() ?? gameObject.AddComponent<ReturnReportHud>();
            islandExpansionFeedback ??= GetComponent<IslandExpansionFeedback>()
                ?? gameObject.AddComponent<IslandExpansionFeedback>();
            islandMeshVisualizer ??= GetComponent<IslandMeshVisualizer>()
                ?? gameObject.AddComponent<IslandMeshVisualizer>();
            homeSettlementVisualizer ??= GetComponent<HomeSettlementVisualizer>()
                ?? gameObject.AddComponent<HomeSettlementVisualizer>();
            residentDirector ??= GetComponent<ResidentDirector>()
                ?? gameObject.AddComponent<ResidentDirector>();
            prototypeWorldGrowthDebug ??= GetComponent<PrototypeWorldGrowthDebug>()
                ?? gameObject.AddComponent<PrototypeWorldGrowthDebug>();
            chunkManager ??= GetComponent<ChunkManager>() ?? gameObject.AddComponent<ChunkManager>();
            worldGrowthService ??= GetComponent<WorldGrowthService>() ?? gameObject.AddComponent<WorldGrowthService>();
            animalSpawner ??= GetComponent<AnimalSpawner>() ?? gameObject.AddComponent<AnimalSpawner>();
            dayNightWeatherVisual ??= GetComponent<DayNightWeatherVisual>()
                ?? gameObject.AddComponent<DayNightWeatherVisual>();
            worldVisualEnvironment ??= GetComponent<WorldVisualEnvironment>()
                ?? gameObject.AddComponent<WorldVisualEnvironment>();
            // Stage 11E-1: atmosphere must live on its own root — never on GameSystems
            // (Plants/Animals are children and would pan with the camera).
            worldAtmosphereVfx = WorldAtmosphereVfx.EnsureIsolatedInstance();
            waterSurfaceApplicator ??= GetComponent<WaterSurfaceApplicator>()
                ?? gameObject.AddComponent<WaterSurfaceApplicator>();
            ecosystemHud ??= GetComponent<EcosystemHud>() ?? gameObject.AddComponent<EcosystemHud>();
            collectionService ??= GetComponent<CollectionService>()
                ?? gameObject.AddComponent<CollectionService>();
            collectionUI ??= GetComponent<CollectionUI>()
                ?? gameObject.AddComponent<CollectionUI>();
            fishingController ??= GetComponent<FishingController>()
                ?? gameObject.AddComponent<FishingController>();
            fishingUI ??= GetComponent<FishingUI>()
                ?? gameObject.AddComponent<FishingUI>();
            fishingOceanInput ??= GetComponent<FishingOceanInput>()
                ?? gameObject.AddComponent<FishingOceanInput>();
            fishingPondInput ??= GetComponent<FishingPondInput>()
                ?? gameObject.AddComponent<FishingPondInput>();
            fishCatalog ??= Resources.Load<FishCatalog>("FishCatalog");

            if (plantingSystem != null)
            {
                chunkManager.EnsureHomeChunkInScene(plantingSystem.Plots);
            }

            if (saveService != null && plantingSystem != null)
            {
                saveService.Configure(plantingSystem, chunkManager);
            }

            if (offlineGrowth != null && saveService != null && plantingSystem != null)
            {
                var catalog = plantingSystem.Catalog ?? Resources.Load<PlantCatalog>("PlantCatalog");
                offlineGrowth.Configure(
                    saveService,
                    plantingSystem,
                    1f,
                    catalog,
                    chunkManager);
            }

            var growthConfig = WorldGrowthConfig.CreateDefault();
            worldGrowthService.Configure(
                saveService,
                chunkManager,
                offlineGrowth,
                plantingSystem,
                growthConfig);

            islandExpansionFeedback?.Configure(saveService, chunkManager, worldGrowthService);
            islandMeshVisualizer?.Configure(saveService, chunkManager);
            homeSettlementVisualizer?.Configure(saveService, chunkManager);
            residentDirector?.Configure(saveService);
            prototypeWorldGrowthDebug?.Configure(
                saveService,
                chunkManager,
                islandMeshVisualizer,
                islandExpansionFeedback,
                growthConfig,
                homeSettlementVisualizer);
            animalSpawner?.Configure(saveService);
            dayNightWeatherVisual?.Configure(saveService);
            worldVisualEnvironment?.Configure(saveService);
            worldAtmosphereVfx?.Configure(Camera.main != null ? Camera.main.transform : null);
            waterSurfaceApplicator?.Apply();
            ecosystemHud?.Configure(saveService);
            collectionService?.Configure(saveService);

            fishingUI?.Configure(fishingController);
            fishingController?.Configure(saveService, fishCatalog, fishingUI, collectionService);
            var cam = Camera.main;
            var isoCam = cam != null ? cam.GetComponent<IsometricCameraController>() : null;
            cameraController = isoCam ?? cameraController;
            fishingOceanInput?.Configure(fishingController, cam, isoCam, plantingSystem);
            FishingOceanInput.EnsureOceanHitProxy();
            fishingOceanInput?.SetInputEnabled(false);
            fishingPondInput?.Configure(fishingController, cam, isoCam, plantingSystem);
            FishingPondInput.EnsurePondHitProxy();
            fishingPondInput?.SetInputEnabled(false);

            // Stage 8B: Collection HUD entry + world suppress (same bootstrap instance).
            collectionUI?.Configure(collectionService, fishCatalog);
            collectionUI?.WireGameplay(
                fishingController, fishingOceanInput, plantingSystem, cameraController, fishingPondInput);
            collectionUI?.SetPlayEntryVisible(false);

            if (onlineGrowthTicker != null && plantingSystem != null)
            {
                onlineGrowthTicker.Configure(plantingSystem);
            }

            mainMenuHud ??= GetComponent<MainMenuHud>() ?? gameObject.AddComponent<MainMenuHud>();
            mainMenuHud.Configure(saveService, this);
            SetGameplayEnabled(false);
            returnSummaryHud?.SetVisible(false);
            returnReportHud?.Hide();
            ecosystemHud?.SetVisible(false);
        }

        private void Start()
        {
            if (SaveService.ConsumePendingFreshStart())
            {
                StartFreshSession();
                return;
            }

            mainMenuHud?.Show();
        }

        public void ContinueGame()
        {
            var ok = worldGrowthService != null && worldGrowthService.ApplyOfflineSession();
            // CIF-A4: never fall back to ApplyOnStartup after a partial/failed offline pass —
            // that path used to mutate live plants before/without a clean commit.
            if (!ok && worldGrowthService == null && offlineGrowth != null)
            {
                offlineGrowth.ApplyOnStartup();
            }
            else if (!ok)
            {
                Debug.LogWarning(
                    "[AFK Island] Offline session failed; visuals stay on last authoritative save. " +
                    (worldGrowthService != null ? worldGrowthService.LastCombinedReport : string.Empty));
            }

            // Visual rebuilds read authoritative snapshot only (commit already applied plants on success).
            islandExpansionFeedback?.RefreshFromSession();
            islandMeshVisualizer?.RebuildFromSave();
            homeSettlementVisualizer?.RebuildFromSave();
            residentDirector?.RebuildFromSave();
            animalSpawner?.RestoreFromSave();
            dayNightWeatherVisual?.ApplyFromSave();
            mainMenuHud?.Hide();

            if (!ok)
            {
                SetGameplayEnabled(false);
                if (returnSummaryHud != null && worldGrowthService != null)
                {
                    returnSummaryHud.ShowText(worldGrowthService.LastCombinedReport);
                }

                return;
            }

            var report = worldGrowthService != null ? worldGrowthService.LastReturnReport : null;
            if (report != null && returnReportHud != null)
            {
                // Keep planting disabled until the player dismisses the report.
                SetGameplayEnabled(false);
                returnSummaryHud?.SetVisible(false);
                ecosystemHud?.SetVisible(false);
                collectionUI?.SetPlayEntryVisible(false);
                collectionUI?.Close();
                returnReportHud.Show(report, EnterPlay);
                return;
            }

            // Fallback text report if structured model missing.
            if (returnSummaryHud != null)
            {
                if (worldGrowthService != null)
                {
                    returnSummaryHud.ShowText(worldGrowthService.LastCombinedReport);
                }
                else if (offlineGrowth != null)
                {
                    returnSummaryHud.Configure(offlineGrowth);
                    returnSummaryHud.SetVisible(true);
                }
            }

            EnterPlay();
        }

        public void StartFreshSession()
        {
            worldGrowthService?.PrepareFreshRuntime();
            returnSummaryHud?.SetVisible(false);
            returnReportHud?.Hide();
            collectionUI?.SetPlayEntryVisible(false);
            collectionUI?.Close();
            islandExpansionFeedback?.RefreshFromSession();
            islandMeshVisualizer?.RebuildFromSave();
            homeSettlementVisualizer?.RebuildFromSave();
            residentDirector?.RebuildFromSave();
            animalSpawner?.RestoreFromSave();
            dayNightWeatherVisual?.ApplyFromSave();
            EnterPlay();
        }

        public void ConfirmNewGame()
        {
            if (saveService != null && saveService.HasSave())
            {
                saveService.ResetProgressAndReload();
                return;
            }

            StartFreshSession();
        }

        private void EnterPlay()
        {
            mainMenuHud?.Hide();
            returnReportHud?.Hide();
            animalSpawner?.RestoreFromSave();
            dayNightWeatherVisual?.ApplyFromSave();
            ecosystemHud?.SetVisible(true);
            // Stage 11E: after AFK pan/zoom into void-tree pockets, always re-center on play.
            if (cameraController == null)
            {
                var cam = Camera.main;
                cameraController = cam != null ? cam.GetComponent<IsometricCameraController>() : null;
            }

            cameraController?.ResetToIslandCenter();
            SetGameplayEnabled(true);
            collectionUI?.SetPlayEntryVisible(true);
        }

        private void SetGameplayEnabled(bool enabled)
        {
            if (plantingSystem != null)
            {
                plantingSystem.enabled = enabled;
            }

            if (onlineGrowthTicker != null)
            {
                onlineGrowthTicker.enabled = enabled;
            }

            // CIF-B1: freeze camera off-play (menu / return report), not only planting/fishing.
            if (cameraController == null)
            {
                var cam = Camera.main;
                cameraController = cam != null ? cam.GetComponent<IsometricCameraController>() : null;
            }

            if (cameraController != null)
            {
                cameraController.enabled = enabled;
            }

            fishingOceanInput?.SetInputEnabled(enabled);
            fishingPondInput?.SetInputEnabled(enabled);
            if (!enabled && fishingController != null && fishingController.IsOpen)
            {
                fishingController.RequestClose();
            }

            if (!enabled)
            {
                // Hide entry first so Close() does not restore world input while leaving play.
                collectionUI?.SetPlayEntryVisible(false);
                collectionUI?.Close();
            }
        }
    }
}
