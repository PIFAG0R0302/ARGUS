# ARGUS Bugfix & UX Context (v1.2.0)

Компактна довідка за результатами сесії `Argus bug fix` для швидкого старту в новому чаті.

## 1. Виправлені баги та UX-оновлення

- **CopyOS (клонування ОС, [WindowsMigrationHostOperations.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.Migration/Engine/WindowsMigrationHostOperations.cs)):**
  - `diskpart convert gpt` у Windows 10/11 авто-створює 16 МБ MSR як Розділ 1, що зсувало нумерацію і ламало `PartitionAndPrepareTargetAsync` (`Volume GUID`).
  - Розділено на `BuildDiskpartInitScript` + `BuildDiskpartLayoutScript` із видаленням авто-MSR; додано динамічний пошук `BootPartitionNumber` / `OsPartitionNumber` через `MSFT_Partition` та замінено `select partition 1` у `InstallBootloaderAsync`.
- **In-place оновлення списків ([CollectionSyncHelper.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/ViewModels/CollectionSyncHelper.cs), [ArgusTheme.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Themes/ArgusTheme.axaml)):**
  - Додано `SyncByKey` (оновлює наявні рядки, видаляє зниклі, переміщує через `Move` без скидання скролу, `SelectedItem` і `IsExpanded`) та `FormatSortHeader`.
  - Додано стилі `ListBox.argus-list` і `Button.column-sort-header`.
- **Моніторинг ([MonitoringView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/MonitoringView.axaml), [MonitoringViewModel.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/ViewModels/MonitoringViewModel.cs), [GpuProcessMetricsReader.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Services/GpuProcessMetricsReader.cs)):**
  - Прибрано «Накопичувачі», «Додаткові сенсори» та напис про рівень 1 API.
  - Зроблено згортаними «Завантаження по потоках CPU» (`IsCpuThreadsExpanded`) і картку GPU (`IsGpuDetailsExpanded`).
  - Усі 4 кругові шкали (`CPU`, `RAM`, `GPU`, `TEMP`) клікабельні й відкривають панель топ-споживачів (для GPU — PDH `GPU Engine(*)\Utilization Percentage` із фолбеком на CPU/RAM).
- **Мережа ([NetworkView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/NetworkView.axaml), [NetworkViewModel.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/ViewModels/NetworkViewModel.cs)):**
  - Прибрано підвкладки «Трафік по процесах» і «Правила фаєрволу», пошук та фільтри `Усі`/`ESTABLISHED`/`LISTENING`/`TCP`/`UDP`; залишено «Лише підозрілі» (`OnlySuspiciousConnections`).
  - З’єднання згруповано по програмах (`ConnectionAppGroupViewModel`, за замовчуванням згорнуті; при `OnlySuspiciousConnections = true` розгортаються автоматично).
- **Процеси ([ProcessesView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/ProcessesView.axaml), [ProcessesViewModel.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/ViewModels/ProcessesViewModel.cs)):**
  - Прибрано поле пошуку й верхній блок кнопок сортування; сортування лише через клікабельні заголовки стовпців зі стабільним вторинним ключем `Pid`.
  - Додано групування мультипроцесних програм (`ProcessGroupViewModel`, згорнуті за замовчуванням).
- **Пристрої та Очищення ([DevicesView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/DevicesView.axaml), [DevicesViewModel.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/ViewModels/DevicesViewModel.cs), [CleanupView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/CleanupView.axaml)):**
  - У «Пристроях» прибрано пошук і бейдж `CfgMgr32`, інтерфейси однакових пристроїв згруповано в `DeviceGroupViewModel` (згорнуті за замовчуванням).
  - В «Очищенні» прибрано UI-картки «Білий список винятків» та «Запобіжники очищення» (бекенд-захист активний).
- **Диски ([DisksView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/DisksView.axaml), [DisksViewModel.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/ViewModels/DisksViewModel.cs), [WindowsDiskSpaceAnalyzer.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.Maintenance/Disks/WindowsDiskSpaceAnalyzer.cs), [DiskDeleteSafetyPolicy.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.Maintenance/Disks/DiskDeleteSafetyPolicy.cs)):**
  - Додано drill-down по папках і файлах, навігацію («На рівень вище», «До кореня тому»), вибір чекбоксами та видалення у **Кошик Windows** (`SHFileOperationW` з `FOF_ALLOWUNDO`) з підтвердженням.
- **Автозапуск і Служби ([StartupAndServicesView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/StartupAndServicesView.axaml), [StartupAndServicesViewModel.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/ViewModels/StartupAndServicesViewModel.cs)):**
  - Прибрано пошук, додано швидкі фільтри-пігулки (за джерелом/станом/типом запуску) та клікабельні заголовки сортування.
- **Масштабування UI ([ArgusSettings.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.Core/Configuration/ArgusSettings.cs), [SettingsView.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/Views/SettingsView.axaml), [MainWindow.axaml](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/MainWindow.axaml), [MainWindow.axaml.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.App/MainWindow.axaml.cs)):**
  - `UiScalePercent` (`80..150`, `LayoutTransformControl` + `ScaleTransform`), слайдер/пресети в Налаштуваннях, гарячі клавіші `Ctrl + '+'`/`'-'`/`0` та `Ctrl + коліщатко миші`.
- **Реліз, автооновлення та сертифікат:**
  - Автооновлення вказує на `PIFAG0R0302/ARGUS-Updates` ([ArgusSettings.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.Core/Configuration/ArgusSettings.cs) автоматично мігрує старий `PIFAG0R0302/ARGUS`).
  - Додано [Install-Certificate.cmd](file:///e:/Antigravity%20projects/ARGUS/scripts/Install-Certificate.cmd) та авто-імпорт/розблокування в [Argus.iss](file:///e:/Antigravity%20projects/ARGUS/installer/Argus.iss); для SignPath Foundation створено [CODE_SIGNING_POLICY.md](file:///e:/Antigravity%20projects/ARGUS/CODE_SIGNING_POLICY.md), [SECURITY.md](file:///e:/Antigravity%20projects/ARGUS/SECURITY.md), [release-signpath.yml](file:///e:/Antigravity%20projects/ARGUS/docs/release-signpath.yml).

## 2. Зафіксовані правила, запобіжники та патерни

- **Pre-Implementation Gate ([AGENTS.md](file:///e:/Antigravity%20projects/ARGUS/AGENTS.md) і глобальний `~/.gemini/config/AGENTS.md`):** перед нетривіальним фіксом/фічею ставити 5–10 уточнень через `ask_question` і чекати підтвердження. Скіли `grill-me`, `grilling`, `brainstorming` встановлені локально в `.agents/skills/` та глобально.
- **Стабільність UI:** усі живі списки оновлювати виключно через `CollectionSyncHelper.SyncByKey` (ніколи `Clear()` + `Add()` на таймері).
- **Зворотна сумісність:** не ламати наявні публічні властивості VM чи інтерфейси контрактів (використовувати default interface methods або опційні параметри).
- **Захист видалення ([DiskDeleteSafetyPolicy.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.Maintenance/Disks/DiskDeleteSafetyPolicy.cs)):** блокувати корені томів, `Windows`, `Program Files`, `ProgramData`, `Users` root/`Default`/`Public`, `System Volume Information`, `$Recycle.Bin`, папку `ARGUS`, UNC-шляхи та `ReparsePoint`.
- **Інструменти середовища:** `git` і `dotnet` не в базовому `PATH` — підключати `$env:LOCALAPPDATA\Programs\Git\cmd` та `$env:LOCALAPPDATA\Microsoft\dotnet`.

## 3. Стан тестів і відкриті питання

- **Поточна версія:** `1.2.0` (`Directory.Build.props`), кореневий `Argus.exe` синхронізовано, реліз `v1.2.0` опубліковано на `PIFAG0R0302/ARGUS-Updates` ([RELEASE-NOTES-1.2.0.md](file:///e:/Antigravity%20projects/ARGUS/docs/RELEASE-NOTES-1.2.0.md)), коміт `bd5f6a9` у `PIFAG0R0302/ARGUS` (`PUBLIC`).
- **Тести:** `Debug` і `Release` (`-Strict`) — **0 помилок, 0 попереджень, 222/222 тестів пройдено**.
- **Відкрите питання (SignPath Foundation / Windows 11 SAC):**
  - Заявка на `https://signpath.org/apply` на етапі подання/розгляду користувачем.
  - Після отримання `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_API_TOKEN` і нового відбитка сертифіката потрібно: (1) перенести `docs/release-signpath.yml` у `.github/workflows/release-signpath.yml` (потрібен `workflow` scope у `gh`), (2) оновити `DefaultTrustedSignerThumbprint` у [ArgusSettings.cs](file:///e:/Antigravity%20projects/ARGUS/src/Argus.Core/Configuration/ArgusSettings.cs#L8).
- **Нюанс Git:** у `ArgusSetup/` є локальний `ArgusSetup.rar` (вже додано в `.gitignore`).
