---
apply: ALWAYS
mode: ALL
---

# AFK Island — правила агента

## Источник правды
- Контракт: `Docs/AFK-ISLAND-MASTER-SPEC.md`
- Текущий открытый срез: `Docs/SPEC-EXECUTION.md` и актуальный ТЗ из него
- Визуальная приёмка / история пакетов: `Docs/TZ-11F-Visual-QA.md` и связанные TZ

## Перед любой правкой
1. Прочитай `Docs/SPEC-EXECUTION.md` — что открыто, какая следующая сборка / versionCode.
2. Меняй только то, что разрешено текущим пакетом ТЗ.
3. Не переоткрывай принятые пакеты без явного запроса пользователя.

## Жёсткие запреты (пока ТЗ явно не открывает)
- Не менять формат и версию сохранения (сейчас save v8), не делать миграций без контракта.
- Не переписывать симуляцию роста/погоды/колонизации/животных/рыбалки «заодно».
- Не двигать пороги LOD, mipmaps и формулу sway ветра без явного провала пакета.
- Не возвращать атмосферу на `GameSystems`; корень атмосферы — `WorldAtmosphereVfxRoot`.
- Не включать старый `UnityEngine.Input` вслепую ради BlueStacks.

## Терминал
- Shell = PowerShell. Переменные только как `$env:NAME`, никогда `%NAME%`.
- Предпочитай готовые скрипты в `.gigacode/skills/afk-device-pipeline/scripts/`.

## Стиль работы
- Минимальный diff под один пакет / одну сборку.
- После device-pass фиксируй факты (версия APK, versionCode, что проверено, logcat) в ответ и при необходимости в Docs.
- Не коммить без явной просьбы пользователя.

## Локальные пути этой машины
- Unity Editor: `D:\6000.6.5f1\Editor\Unity.exe` (проект `6000.6.5f1`)
- Проект: `D:\afk\AFKIsland`
- Android package: `com.afkisland.game`
- adb: `"$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"` (только PowerShell; не `%LOCALAPPDATA%`)
- BlueStacks: ярлык `Desktop\BlueStacks (игра).lnk` (обычный лаунчер может ломаться на кириллическом профиле)
