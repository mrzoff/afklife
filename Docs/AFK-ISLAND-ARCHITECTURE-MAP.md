# AFK ISLAND — Architecture Map

**Версия карты:** 0.2
**Дата аудита:** 2026-11-23
**Ветка аудита:** `fix/stage-11g-visual-overlap-and-start-area` (56 коммитов впереди `main`, 1 позади; HEAD на момент синхронизации — `83dcfc6`)
**Принятая сборка:** 0.6.56 / versionCode 86 (это состояние `main`; ветка 11G ещё не собрана и не прошла device-pass)
**Срез проекта по Docs/SPEC-EXECUTION.md:** Unity 6 / URP, Save v8
**Статус карты:** ревизия 0.2 синхронизирована с веткой на `83dcfc6` (2026-11-23). Кодовая карта сверена с исходниками; результаты batchmode и ограничения визуальной приёмки отражены отдельно. Упоминание класса не означает, что все его сценарии прошли визуальную или device-проверку.

Карта фактических компонентов из `Assets/_Project/Scripts`, сопоставленная с действующими документами. Ветка 11G **не слита в `main`** и не меняет Save v8, бюджеты симуляции и игровые механики.

---

## 1. Слои архитектуры

| Слой | Ответственность | Основные компоненты |
|---|---|---|
| Core / Composition | Сборка зависимостей, запуск игры и продолжение сессии | `GameBootstrap` |
| Save / Data | Авторитетное состояние, JSON, миграции, копирование данных | `GameSaveData`, `SaveService`, `SaveMigrationService`, `SaveClone`, `ChunkMaskCodec`, `BiomeMaskCodec` |
| Offline orchestration | Расчёт периода отсутствия, применение мира и фиксация результата | `WorldGrowthService`, `OfflineGrowthService`, `LivingOfflineNaturePass` |
| World simulation | Расширение суши, прогресс клеток, события мира | `WorldGrowthSimulation`, `WorldGrowthConfig`, `WorldEventsSimulation` |
| World placement | Пространственный слой визуальной раскладки | `WorldScatterField`, `GrowthWorldPlacement` |
| Living ecosystem | Погода, растения, колонизация, фауна и обратная связь | `WeatherSimulation`, `PlantColonizationSimulation`, `LivingWorldSimulation`, `FaunaSimulation`, `EcosystemSimulation` |
| Settlement | Рост маски поселения и производные жители | `SettlementSimulation`, `SettlementCellQuery`, `ResidentRoster`, `ResidentPathGraph`, `ResidentDirector`, `ResidentInstance` |
| Activities / Collection | Рыбалка и единая коллекция | `FishingController`, `FishingSimulation`, `FishingCatchProgress`, `FishCatalog`, `CollectionService` |
| World presentation | Чанки, меш земли, декор, вода, поселение, атмосфера | `ChunkManager`, `ChunkTerrainMesher`, `IslandMeshVisualizer`, `HomeSettlementVisualizer`, `WaterSourceLayout`, `WaterSurfaceApplicator`, `WorldAtmosphereVfxPlayer` |
| Input / UI | Камера, жесты, экраны и отчёт возвращения | `IsometricCameraController`, `WorldPointerGesture`, UI presenters/HUD |
| Editor verification | Smoke-тесты и сборочные утилиты | `Assets/_Project/Scripts/Editor/*` |

---

## 2. Главный поток запуска и офлайн-прогресса

Проверено по коду: `GameBootstrap` связывает `SaveService`, `OfflineGrowthService`, `WorldGrowthService` и `ResidentDirector`. Основной офлайн-путь идёт через `WorldGrowthService.ApplyOfflineSession()`; `OfflineGrowthService` отвечает за рост/восстановление растений и остаётся legacy fallback при отсутствии `WorldGrowthService`.

Порядок внутри живого офлайн-прохода: погода → рост существующих растений → колонизация → рост новых растений за оставшееся время → симуляция живого мира/фауны → поселение. Поселение выполняется после обновления земли; watermark поселения защищает от повторного применения того же периода.

Критическая граница: симуляция работает с данными сохранения/рабочей копией; визуальные представления восстанавливаются из зафиксированного состояния. При неудачной записи watermark не считается применённым.

---

## 3. Сохранение и модель данных

Основные файлы:

- `Save/GameSaveData.cs` — корневая модель сохранения и вложенные модели.
- `Save/SaveService.cs` — чтение/запись, authoritative snapshot, блокировка IO, временный файл и резервная копия.
- `Save/SaveMigrationService.cs` — создание дефолтов и миграции.
- `Save/SaveClone.cs` — глубокое копирование рабочего состояния.
- `Save/ChunkMaskCodec.cs` — упаковка клеточных масок, `GridSize`, `CellCount`, `IndexToLocal`.
- `Save/BiomeMaskCodec.cs` — маска биомов.

В `GameSaveData` присутствуют данные растений, чанков, расширения мира, экосистемы, погоды, животных, habitats, коллекции и рыбалки. Рост поселения хранится как маска в записи чанка расширения мира, а не как отдельный список GameObject-домов. Жители в сейв не пишутся (Stage 13).

Текущий контракт: **Save v8**. Ветка 11G новых полей и миграций не добавляет: стабильность визуальной раскладки решается чтением существующих `chunks[].randomSeed` и `growthSeed`, без изменения схемы.

---

## 4. Мир, чанки и расширение суши

| Компонент | Роль |
|---|---|
| `WorldGrowthConfig` | Параметры роста мира |
| `WorldGrowthSimulation` | Расчёт роста и событий на рабочей копии данных |
| `WorldGrowthService` | Оркестрация офлайн-сессии, объединение результатов, commit и обновление мира |
| `IslandChunk` | Представление чанка (`DefaultSizeMeters`, `HomeChunkX/Z`) |
| `ChunkManager` | Логическое состояние чанков и загруженные чанки |
| `GrowthWorldPlacement` | Перевод координат мира/чанка/клетки, `VisualYBase`, `CellCenterWorld`, `CellWorldPosition`, `CellLocalOnChunk` |
| `ChunkTerrainMesher` | Геометрия поверхности из масок |
| `IslandMeshVisualizer` | Меши чанков, клеточный декор и world-space scatter деревьев |
| `IslandPrototypeBuilder` | Авторская стартовая сцена/базовый остров |

Координатный контракт из Stage 11G-A: чанк 16×16 м, сетка 16×16, клетка 1×1 м; маска земли остаётся источником истины. Позиция объекта — производный пространственный слой.

Изменения ветки 11G:

- `ChunkTerrainMesher.SampleVisualHeight(worldX, worldZ, sampleWorldStage, sampleWorldBiome, chunkSize)` — read-only выборка того же SDF/domain-warp поля высот, что использует `Build`, с возвратом мировой Y (включая `GrowthWorldPlacement.VisualYBase`). Нужна для посадки корней на фактический рельеф вместо константной высоты стадии. `MesherRevision = 43`.
- `GrowthWorldPlacement.IsInsideAuthoredHomeFootprint` переведён с прямоугольника на эллипс (полуоси `AuthoredHomeHalfX = 7.25`, `AuthoredHomeHalfZ = 5.75`), чтобы дикие растения не попадали в океанские углы органической площадки.
- `IslandPrototypeBuilder` заменяет прямоугольные кубы `BaseRock`, `MidPlateau`, `SettlementYard`, `ShoreSand_N`, `ShoreSand_S` на процедурные органические меши (64 сегмента, нерегулярный эллипс, наклонный боковой пояс). Верхние плоскости остаются плоскими, трансформы не двигаются, поэтому координаты поселения, пруда и грядок валидны. `MeshCollider` ставится всем платформам, кроме `SettlementYard` (как и раньше). Апгрейд применяется и к уже существующим authored-сценам при запуске, и к новым островам.
- Новый `World/WorldScatterField.cs` — статический генератор world-space точек: детерминированная решётка кандидатов + хеш-джиттер от seed и целочисленных координат, непрерывный value-noise для модуляции плотности, фильтр локального минимума (blue-noise) с настраиваемым минимальным шагом, halo-выборка вокруг границ запроса (результат не зависит от того, какой чанк запросил), опциональные `blockedPoints`. Мутабельного состояния по чанкам и `Update` нет. Константы: `DefaultTreeMinimumSpacing = 3.5`, `DefaultCandidateSpacing = 2.2`, джиттер 0.44 шага кандидатов.

---

## 5. Растения и колонизация

- `Plants/PlantCatalog.cs`, `PlantData.cs` — каталог и определения растений.
- `PlantInstance.cs`, `PlantPlot.cs`, `PlantingSystem.cs` — runtime-объекты и ручная посадка.
- `OnlineGrowthTicker.cs` — онлайн-обновление роста.
- `OfflineGrowthService.cs` — восстановление растений и офлайн-рост.
- `PlantColonizationSimulation.cs` — появление диких растений на доступных клетках.

Контракт: существующие координаты растений в сейве не переписываются; `plotId` и `instanceId` — идентификаторы/ключи владения и дедупликации.

Изменение ветки 11G: у `PlantPlot` появился serialized `showAvailableMarker` (по умолчанию `false`). `PlotMarker` создаётся, но его `Renderer` выключен, а `BoxCollider` грядки остаётся активным — тап-посадка работает, зелёные подложки не рисуются. Материал маркера теперь создаётся только при включённой опции.

---

## 6. Экосистема, животные и погода

- `WeatherSimulation.cs` — расчёт погоды за период отсутствия.
- `DayNightWeatherVisual.cs`, `WorldVisualEnvironment.cs` — визуальное представление среды.
- `LivingWorldSimulation.cs` — общий проход живого мира после погоды.
- `FaunaSimulation.cs` — симуляция фауны.
- `FaunaNatureFeedback.cs` — обратная связь между результатами фауны и природой.
- `Animals/AnimalData.cs`, `AnimalInstance.cs`, `AnimalSpawner.cs`, `AnimalVisualMotion.cs` — данные и визуальные экземпляры животных.
- `EcosystemSimulation.cs` — отдельная экосистемная логика; её точные связи с основным офлайн-проходом требуют дополнительной проверки.

Изменение ветки 11G (только визуал птиц):

- `AnimalData.modelForwardYawOffset` — поправка поворота от +Z корня к авторскому forward модели; в `AnimalInstance` применяется при инициализации и при повороте к цели.
- Ассеты `AnimalData_bird.asset` в `Resources/Animals` и `ScriptableObjects/Animals`: `visualScale 0.25`, `moveSpeed 0.42`, `idleSeconds 3–6.5`, `wanderRadius 1.7`, `modelForwardYawOffset 180`.
- Численность фауны и офлайн-симуляция фауны не менялись.

---

## 7. Поселение и жители

Поселение как данные:

- `SettlementSimulation.cs` обновляет клеточную маску поселения. Стадии маски: пусто, расчистка, тропа, камень, хижина. Бюджеты 14 / 6 / 2, `AbsoluteMaxHutsPerSession = 6`, `CellsPerHut = 28` — без изменений.
- `SettlementCellQuery.cs` — запросы к маске: `IsHut`, `IsHut(save, chunkX, chunkZ, cellIndex)`, `TryWorldToCell`, `IsHutAtWorldXZ`, `TryParseWildCell`, а также новые read-only footprint-запросы: `IsHutFootprintBlocked(save, worldX, worldZ, minimumCenterSpacing)` (учитывает и authored starter hut) и `IsHutVisualSuppressed(...)` (детерминированно оставляет видимой хижину с наименьшим мировым ключом Z/X в кластере перекрытия). Константы: `DefaultHutCenterSpacing = 3.2`, `DefaultTreeHutClearance = 2.2`.
- `HomeSettlementVisualizer.cs` строит дома/тропы/камни из сохранённой маски, скрывает подавленные перекрывающиеся хижины и не учитывает их в затухании authored-домов.

Жители как производный runtime-слой: `ResidentRoster` (жители из клеток-хижин), `ResidentPathGraph` (маршруты по тропам/камням), `ResidentDirector` (создание и управление экземплярами), `ResidentInstance` (отдельный житель, без собственного `Update`).

Изменения ветки 11G:

- `SettlementSimulation` проверяет кандидата в хижины через `IsHutFootprintBlocked` дважды: при планировании и непосредственно перед записью маски. Новые хижины ближе 3.2 м к существующей или к authored starter hut не создаются. Старые перекрытия в сейве не переписываются.
- `IslandMeshVisualizer` кеширует центры хижин на один rebuild и отсекает по ним кусты/камни (1.75 м) и высокие деревья (`DefaultTreeHutClearance` 2.2 м).

Stage 13 закрыт на 0.6.56 / versionCode 86; Stage 12 — на 0.6.50. Не переоткрывать без отдельного решения.

---

## 8. Вода и визуальная среда

- `WaterSourceLayout.cs` — авторская раскладка источника воды/пруда (`IsInsideReserveXZ`, `BlocksTrunk`, `TrunkRadius`).
- `WaterSurfaceApplicator.cs` — визуальное применение поверхности воды.
- `WorldAtmosphereVfx.cs` — атмосферные визуальные эффекты.
- `VegetationWindApplicator.cs` — визуальный ветер для растительности.
- `ChunkTerrainMesher.cs`, `IslandMeshVisualizer.cs` — земля и декор.

Ограничения: не менять пороги LOD и формулу sway без провала соответствующего smoke/device-чеклиста; не создавать вторую систему воды. Ветка 11G воду не меняет (`WaterSurfaceApplicator` затронут только заменой устаревшего API поиска объектов).

---

## 9. Рыбалка и коллекция

Рыбалка: `FishCatalog.cs` / `FishData.cs`, `FishingController.cs` (FSM `Closed → Ready → Wait → Bite → Result`), `FishingSimulation.cs`, `FishingOceanInput.cs` / `FishingPondInput.cs`, `FishingCatchProgress.cs`, `FishingUI.cs`. Успешный улов регистрируется один раз; Miss/Cancel/EmptyPool не открывают запись в коллекцию.

Коллекция: `CollectionService.cs` — единый канонический сервис открытий, `CollectionUI.cs` — отображение. Параллельные счётчики в отдельных UI не создавать.

---

## 10. Управление и интерфейс

- `Camera/IsometricCameraController.cs` — изометрическая камера.
- `Input/WorldPointerGesture.cs` — жесты/указатель мира.
- `Input/AndroidMouseScroll.cs` — прокрутка/мышиный ввод для Android/эмулятора.
- `UI/MainMenuHud.cs`, `EcosystemHud.cs`, `ReturnReportHud.cs`, `ReturnSummaryHud.cs`, `ReturnReportEventsPresenter.cs`, `IslandExpansionFeedback.cs`, `CollectionUI.cs` — экраны, HUD и отчёт возвращения.

Точные зависимости UI от сервисов и правила блокировки игрового ввода требуют отдельного прохода по UI-коду.

---

## 11. Проверки и smoke

В `Assets/_Project/Scripts/Editor` есть smoke-проверки для сохранения/масок и офлайн-прохода, поселения и жителей, рыбалки/пруда/коллекции, растительности/ветра/визуальной среды, жестов/камеры и сборки Android.

Ключевые файлы: `LivingOfflineSmoke.cs`, `SettlementAuditSmoke.cs`, `ResidentSmoke.cs`, `CollectionServiceSmoke.cs`, `FishingControllerSmoke.cs`, `FishingSimulationSmoke.cs`, `FishingPondInputSmoke.cs`, `VegetationWindMaterialsSmoke.cs`, `WorldAtmosphereRootSmoke.cs`.

Добавлено веткой 11G:

| Меню | Файл | Что проверяет |
|---|---|---|
| `AFK Island/Smoke Organic Tree Placement (11G-B)` | `WorldScatterFieldSmoke.cs` (есть `RunBatch`) | детерминизм, независимость от порядка запросов чанков, равенство разбитого запроса цельному, минимальный шаг, попадание в границы, исключение blocked-точек, нулевая плотность |
| `AFK Island/Smoke Stage 11G Visual Fixes` | `Stage11GVisualFixSmoke.cs` | отсечение footprint хижины через границу чанка, детерминированный видимый победитель перекрытия, исключение authored starter hut, масштаб/движение/forward-поправка птицы, скрытый маркер при активном коллайдере грядки, вершины/индексы органических платформ |

Наличие smoke-файла не равно подтверждённому прохождению на текущем HEAD: на момент карты 0.2 прогоны в Unity **не выполнялись**.

---

## 12. Текущий статус и открытый фронт

По `Docs/SPEC-EXECUTION.md` (состояние `main`):

- сборка 0.6.56 / versionCode 86, сохранение v8;
- Stage 12 — аудит поселения и device-pass закрыты на 0.6.50;
- Stage 13 — Resident AI закрыт на 0.6.56;
- Stage 11G-A — аудит и контракт органической генерации, код не менялся;
- темп симуляции не менять; `TZ-FAST-PROGRESSION.md` не выполняется;
- пороги LOD и формулу sway не трогать.

Фактическое состояние ветки `fix/stage-11g-visual-overlap-and-start-area`:

- 11G-B (органические высокие деревья через `WorldScatterField`) — код и smoke написаны, приёмка не пройдена;
- visual fixes (птицы, footprint хижин, органическая стартовая площадка, маркеры посадки, эллиптический footprint дома) — код и smoke написаны, приёмка не пройдена;
- в отчётах `Docs/Stage-11G-B-Implementation-Report.md` и `Docs/Stage-11G-Visual-Fixes-Implementation-Report.md` статус `UNITY COMPILE / EDITOR SMOKE / DEVICE PASS PENDING`;
- `main` не изменён, merge не выполнялся, сборка 0.6.57 ещё не собрана.

Оставшиеся задачи приёмки:

1. Компиляция Unity 6000.6.5f1 и прогон обоих новых smoke-меню плюс регресс Stage 12/13.
2. Проверка на эталонном сейве: 120 растений без изменений координат; нет деревьев на берегу, воде, `WaterSource` и клетках хижин; корни в пределах 2 см от рельефа; нет швов и полос плотности; раскладка стабильна после рестарта и после офлайна.
3. Замер `RebuildDecor`/scatter на 20 чанках и мобильная производительность.
4. Play Mode визуальный осмотр птиц, силуэта стартовой площадки и коллизии пруда.
5. Сборка 0.6.57 / versionCode 87 и device-pass, затем merge в `main`.

---

## 13. Что ещё нужно проверить для карты 1.0

- Полный call graph `GameBootstrap → WorldGrowthService → Save commit → восстановление визуала`.
- Поля `GameSaveData` и соответствие каждого поля системе-владельцу.
- Все зависимости `ChunkManager` от Save и visualizers, включая жизненный цикл загруженных чанков.
- Точные связи `EcosystemSimulation`, `WorldEventsSimulation`, `WeatherSimulation` и отчёта возвращения.
- Контракт ручной посадки относительно масок земли, воды, поселения и офлайн-колонизации.
- Полный поток рыбалки: Ocean/Pond input → Controller → Simulation → CatchProgress → Collection.
- UI/input ownership: какие окна блокируют камеру, world taps и мини-игры.
- Проверки производительности и ограничения по количеству чанков/GameObject.
- После приёмки 11G-B: перенос кустов и камней на world-space scatter (11G-C) и судьба клеточного пути `RebuildDecor`.

---

## Источники

- `Docs/AFK-ISLAND-MASTER-SPEC.md`
- `Docs/SPEC-EXECUTION.md`
- `Docs/TZ-11E-2-Vegetation.md`
- `Docs/TZ-11G-Organic-World.md`
- `Docs/TZ-12-Settlement-Audit.md`
- `Docs/TZ-13-Resident-AI.md`
- `Docs/Stage-11G-B-Implementation-Report.md`
- `Docs/Stage-11G-Visual-Fixes-Implementation-Report.md`
- Исходники `Assets/_Project/Scripts/**` перечисленных выше систем, ветка `fix/stage-11g-visual-overlap-and-start-area`.
