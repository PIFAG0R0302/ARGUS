# ARGUS — Контекст проєкту та технічні правила (`v1.1.1`)

Цей документ є компактним стартовим контекстом для нового чату для проєкту **ARGUS** (`e:\Antigravity projects\ARGUS`) — нативній програмі для Windows 10 (від 22H2) / Windows 11 x64 на `.NET 10 LTS` + `Avalonia UI 12.1.6` (Skia/DirectX).

---

## 1. Архітектура та зони відповідальності

- **`src/Argus.Core`** — спільні контракти, моделі, конфігурація (`ArgusSettings`, `ArgusPaths`), шина подій (`IArgusEventBus`), потокобезпечний `RingBuffer<T>`, журнал операцій (`IOperationJournal`), завантажувач правил очищення (`ICleanerRuleLoader`), політика критичних процесів/служб (`CriticalProcessProtection`, `CriticalServiceProtection`), правила іменування Firewall (`ArgusFirewallNaming`) та евристика мережі (`ConnectionHeuristicAnalyzer`).
- **`src/Argus.Telemetry`** — жива телеметрія без сторонніх ядерних драйверів за замовчуванням (`Win32 PDH`, `NVML` + `WDDM`, `IOCTL`/`WMI`, опційний підписаний `PawnIO`), оцінювачі алертів (`TelemetryAlertEvaluator`, `DiskHealthAlertEvaluator` із cooldown 15 хв) та експорт звітів (HTML/JSON/CSV).
- **`src/Argus.Devices`** — інвентаризація заліза (CPU/кеш, SMBIOS RAM, плата/BIOS/UEFI, GPU/VRAM, мережеві адаптери), дерево пристроїв `SetupAPI`/`CfgMgr32` та моніторинг підключення через `WM_DEVICECHANGE`.
- **`src/Argus.Maintenance`** — обслуговування та системні сервіси:
  - двокрокове очищення ОС за JSON-правилами (`WindowsCleanerService`, тека `rules/`);
  - діагностика дисків, оптимізація (`WindowsDiskOptimizer`: TRIM для SSD/NVMe, дефрагментація для HDD) й аналіз зайнятого місця (`WindowsDiskSpaceAnalyzer`);
  - живий інспектор і менеджер процесів (`WindowsProcessMonitorService`: `OpenProcess`, `GetProcessTimes`, `GetProcessIoCounters`, `QueryFullProcessImageNameW`);
  - автозапуск і служби (`WindowsStartupAndServicesManager`: `HKCU`/`HKLM` Run + `StartupApproved`, теки `Startup`, `Task Scheduler` COM, служби через `advapi32.dll`);
  - мережа та брандмауер (`WindowsNetworkManagerService`: `iphlpapi.dll`, фоновий PTR-кеш, `HNetCfg.FwPolicy2` COM, `WindowsProcessNetworkTrafficService` із нативним Win32 ETW `advapi32.dll` за UAC або оцінкою сокетів/I/O без UAC);
  - UAC-прошарок (`WindowsPrivilegeService`) та безшумний in-place автоапдейтер з GitHub (`GitHubAppUpdateService`).
- **`src/Argus.Migration`** — скомпільований C#-рушій живого клонування ОС `CopyOS` (`NtfsVssCloner`, `MigrationPlanEvaluator`, `WindowsMigrationEngine`) без PowerShell/`Add-Type`.
- **`src/Argus.App`** — Avalonia MVVM-інтерфейс, Skia-віджети (`TelemetryArcGauge`, `TelemetryLineChart`), синхронізатор списків без стрибків (`CollectionSyncHelper`), сервіс режимів (`AppUiModeService`), нативні сповіщення трею (`WindowsTrayNotificationService`).
- **Тести (`tests/Argus.Core.Tests`, `tests/Argus.App.Tests`)** — 173 модульні та інтеграційні тести (`xunit.v3`).

---

## 2. Поточна реалізована функціональність (`v1.1.1`)

- **Гібридний інтерфейс та навігація:**
  - Ліва панель згрупована за категоріями: **МОНІТОРИНГ** (`Огляд`, `Моніторинг`, `Мережа`, `Пристрої`), **ОБСЛУГОВУВАННЯ** (`Очищення`, `Диски`, `Міграція ОС`), **СИСТЕМА** (`Процеси`, `Автозапуск і служби`, `Журнал записів`) + нижній блок `Налаштування`.
  - Базовий режим за замовчуванням (`ExpertModeEnabled = false`) + синхронізований тумблер «Експертний режим» у вкладках із збереженням у `settings.json`.
- **UAC на вимогу (`asInvoker`):**
  - Бінарники збираються з `ArgusRequireAdmin=false` (`asInvoker`).
  - Читання метрик і діагностика працюють без прав адміністратора; біля дій, що змінюють ОС, показано контекстну плашку «Підвищити права (UAC)» із перезапуском на ту саму вкладку (`--section`).
- **Фаза 1 та Фаза 2 (дорожня карта):**
  - **Процеси:** PID, CPU %, RAM, Disk I/O (`R/W`), шлях до `.exe`, сортування, пошук, приховування критичних системних процесів у базовому режимі (`CriticalProcessProtection`), завершення процесу з підтвердженням.
  - **Автозапуск і служби:** перегляд і перемикання точок автозапуску через `StartupApproved` / `Task Scheduler`, керування службами (Старт/Стоп/Перезапуск/тип запуску) із жорстким блокуванням критичних служб ОС (`CriticalServiceProtection`).
  - **Мережа (v1 + v2 + евристика):** активні TCP/UDP-з'єднання, підвкладка «Трафік по процесах» (нативний Win32 ETW за UAC / сокетно-I/O оцінка без UAC), підвкладка «Правила Firewall» із жорстким префіксом `ARGUS_`, стабілізація списків через `CollectionSyncHelper`, евристичний аналізатор (`Підозріле` / `Увага`, фільтр «Лише підозрілі», гнучке блокування `.exe` або `ARGUS_Block_IP_*`, завершення процесу).
- **Опційний системний трей:** вимкнений за замовчуванням (`EnableSystemTray = false`). Коли увімкнено, надсилає сповіщення через `Shell_NotifyIconW` **лише** при перегріві CPU/GPU, критично малому місці на диску (`DiskFreeSpaceAlertPercent`, за замовчуванням `10%`) та погіршенні S.M.A.R.T. (`EnableSmartDegradationAlert`).
- **Репозиторії та версія:**
  - Приватний репозиторій коду: `PIFAG0R0302/ARGUS` (гілка `main`, коміт `188a421`, тег `v1.1.1`).
  - Публічний репозиторій релізів (автооновлення): `PIFAG0R0302/ARGUS-Updates` (актуальний реліз `v1.1.1`).

---

## 3. Збірка, публікація та технічні обмеження

- **Інструментарій на хості:** `.NET 10 SDK` (`%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`), `git` (`%LOCALAPPDATA%\Programs\Git\cmd\git.exe`), `gh` (`C:\Program Files\GitHub CLI\gh.exe`), Inno Setup 6 (`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`).
- **Скрипти:**
  - `.\build.ps1 -Configuration Debug|Release -Strict -Test [-PublishPortable]` — строга збірка (`TreatWarningsAsErrors=true`) і прогін тестів.
  - `.\scripts\Build-Release.ps1` — бере версію з `Directory.Build.props`, ганяє `Debug` і `Release` тести, публікує `win-x64` single-file (`ArgusRequireAdmin=false`), підписує Authenticode (сертифікат `9D1B22482AC32CEA02F398527F9409FCA09EA78D` у `Cert:\CurrentUser\My`), збирає інсталятор `installer\Argus.iss` та `Argus-Portable-<ver>-win-x64.zip`, генерує `SHA256SUMS.txt` і перевіряє `ArgusSetup\` через Windows Defender (`MpCmdRun.exe`).
  - `.\scripts\Publish-GitHubRelease.ps1` — комітить, пушить у приватний `PIFAG0R0302/ARGUS` і створює реліз у публічному `PIFAG0R0302/ARGUS-Updates`.
- **Критичні обмеження (не ламати!):**
  - **Defender / HVCI safety:** жодного `WinRing0.sys`, UPX, обфускації чи `Add-Type` у runtime. Нативні виклики — лише через типізований P/Invoke (`LibraryImport`/`DllImport`) або чистий C#.
  - **Версійність:** при піднятті `<Version>` у `Directory.Build.props` оновлювати `Assert.Equal("v...", vm.VersionBadge)` у `tests/Argus.App.Tests/ViewModelTests.cs`.
  - **Avalonia UI:** увімкнено `AvaloniaUseCompiledBindingsByDefault=true` — завжди задавати `x:DataType`. Для живих таблиць із автооновленням використовувати `CollectionSyncHelper` (in-place sync замість `Clear()`).
  - **Безпека автоапдейтера:** обов'язкова перевірка SHA-256 (`SHA256SUMS.txt`) і відбитка підпису `9D1B22482AC32CEA02F398527F9409FCA09EA78D` перед запуском `apply-update.cmd`; якщо тека встановлення захищена від запису (наприклад, `Program Files`), апдейтер запитує UAC тільки на етапі патчингу.
  - **Політика коду:** українська мова інтерфейсу (звертання на «ти» в діалозі), перевірка української прози/документів через `humanizer-ua` (`lint.py` без BOM).

---

## 4. Поточний статус і наступні кроки

- **Статус:** реліз `v1.1.1` зібрано, протестовано (**173/173** тести зелені, Defender exit `0`), закомічено в `PIFAG0R0302/ARGUS` й опубліковано в `PIFAG0R0302/ARGUS-Updates`.
- **Можливі наступні кроки (на вибір користувача):**
  1. Продовження дорожньої карти (наступні модулі в згрупованих категоріях, наприклад, розширена аналітика або планувальник обслуговування).
  2. Комерційний / SignPath сертифікат для зняття попереджень SmartScreen на нових ПК.
  3. Тестування живого оновлення на встановленій копії до `v1.1.1`.
