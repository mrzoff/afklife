---
name: afk-device-pipeline
description: Используй этот skill для сборки Android APK AFK Island, установки в BlueStacks через adb, logcat и device-приёмки по ТЗ. Запускай при просьбах собрать, поставить на эмулятор, проверить logcat, сделать BlueStacks pass.
---

# AFK Island — device pipeline

## Когда применять
- Собрать APK / bump version
- Поставить сборку в BlueStacks
- Снять Unity-ошибки из logcat
- Пройти чеклист device-pass из текущего ТЗ

## Локальные факты
| Что | Значение |
|---|---|
| Unity | `D:\6000.6.5f1\Editor\Unity.exe` |
| Project | `D:\afk\AFKIsland` |
| Package | `com.afkisland.game` |
| adb | `$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe` |
| BlueStacks | ярлык `BlueStacks (игра)` на Desktop |
| Скрипты | `.gigacode/skills/afk-device-pipeline/scripts/` |

Сначала сверь актуальные `bundleVersion` / `AndroidBundleVersionCode` в `ProjectSettings/ProjectSettings.asset` и `Docs/SPEC-EXECUTION.md`.

## Алгоритм
1. Прочитай открытый пакет ТЗ и запреты.
2. Внеси минимальный код под пакет.
3. При необходимости подними versionName / versionCode ровно на одну сборку.
4. Собери APK: `scripts/unity-android-build.ps1` → метод `AfkIsland.EditorTools.AndroidBuild.BuildApkBatch` → `Builds/Android/AFKIsland.apk`.
5. Подними BlueStacks через ярлык «BlueStacks (игра)».
6. `scripts/connect-adb.ps1` → дождись device.
7. `scripts/install-apk.ps1 -ApkPath Builds/Android/AFKIsland.apk` (`adb install -r`, без wipe данных).
8. `scripts/logcat-unity.ps1` — ищи `Exception`, `Error`, целевые строки из ТЗ.
9. Чеклист ТЗ: что агент может закрыть логами/командами — закрывай; визуал без computer-use опиши пользователю (pan/zoom `=` `-`, скрин).
10. Вердикт pass/fail + факты (версия, versionCode, растения/save если смотрели).

## Shell: только PowerShell-синтаксис
Терминал VS Code здесь — **PowerShell**. Нельзя писать `%LOCALAPPDATA%` / `%USERPROFILE%` (это cmd; PowerShell падает с «Не удалось загрузить модуль»).

Правильно:
```powershell
& "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" devices
# или после PATH:
adb devices
.\.gigacode\skills\afk-device-pipeline\scripts\connect-adb.ps1
.\.gigacode\skills\afk-device-pipeline\scripts\install-apk.ps1 -ApkPath 'D:\afk\AFKIsland\Builds\Android\AFKIsland.apk'
```

## Не делать
- Не удалять данные приложения без явной просьбы.
- Не смешивать несколько пакетов в одной сборке.
- Не обещать «сам кликнул по экрану» — у GigaCode нет Cursor computer-use; визуал подтверждает пользователь или отдельный инструмент.
- Не использовать `%VAR%` в командах агента.
