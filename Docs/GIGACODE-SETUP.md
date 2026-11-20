# GigaCode setup (эта машина)

Сделано автоматически в репозитории / окружении:

- Плагин `gigacode.gigacode-vscode` уже установлен в VS Code
- Rules: `.gigacode/rules/afk-island-core.md`
- Skill: `.gigacode/skills/afk-device-pipeline/`
- MCP-заготовка: `.gigacode_vsc/gigacode.jsonc`
- Скрипты: connect-adb / install-apk / logcat-unity / unity-android-build
- `adb` добавлен в User PATH (`%LOCALAPPDATA%\Android\Sdk\platform-tools`)
- Unity Editor: `D:\6000.6.5f1\Editor\Unity.exe`
- Package: `com.afkisland.game`

## Что сделать вручную (1 раз)

1. Открыть папку `D:\afk\AFKIsland` в VS Code.
2. В сайдбаре GigaCode — статус Ready / Connect через GitVerse, если ещё не вошли.
3. Режим **Agent** (не только Ask). Новый чат, чтобы подхватились rules/skills.
4. GigaCode → Settings → Skills/Rules: убедиться, что project rules/skills видны и включены.
5. Approvals: для своего ПК можно ослабить автоподтверждение правок; опасные shell — по желанию вручную.
6. CLI: на личном тарифе часто недоступен. Если организация даёт CLI — поставить отдельно; иначе достаточно Agent + терминал.
7. BlueStacks: запускать ярлык **BlueStacks (игра)**, затем:
   ```powershell
   .\.gigacode\skills\afk-device-pipeline\scripts\connect-adb.ps1
   ```
8. Unattended APK уже есть:  
   `.\.gigacode\skills\afk-device-pipeline\scripts\unity-android-build.ps1`  
   → `AfkIsland.EditorTools.AndroidBuild.BuildApkBatch` → `Builds/Android/AFKIsland.apk`.

## Проверка за 2 минуты

```powershell
adb version
adb devices
D:\6000.6.5f1\Editor\Unity.exe -version
```

В чате GigaCode Agent: «прочитай SPEC-EXECUTION и опиши следующий пакет + как поставить APK скриптами».

## Чего не будет как в Cursor

Нет встроенного computer-use (клики/скрины окон). Визуальную приёмку подтверждает человек; агент закрывает код, сборку, adb install и logcat.
