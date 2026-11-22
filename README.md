# ARGUS

Нативна програма для Windows 10 і Windows 11 (x64), яка поєднує моніторинг заліза в реальному часі, інвентаризацію пристроїв, двокрокове очищення ОС за зовнішніми JSON-правилами, оптимізацію й діагностику накопичувачів та клонування живої операційної системи (`CopyOS`).

## Можливості

- **Огляд і моніторинг:** живі кругові шкали та графіки Skia (CPU, ОЗП, GPU, VRAM, температури, кулери, швидкість дисків), фільтрація серій, порогові сповіщення та експорт звітів у HTML, JSON і CSV.
- **Пристрої:** характеристики процесора, слотів ОЗП (SMBIOS), материнської плати, BIOS/UEFI, відеокарт і мережевих інтерфейсів, а також живе дерево пристроїв Windows (`SetupAPI` + `WM_DEVICECHANGE`).
- **Очищення системи:** двокроковий рушій на зовнішніх JSON-правилах (`rules/`), білий список винятків, автоматичний пропуск символічних посилань (`ReparsePoint`) і створення контрольної точки System Restore.
- **Диски й діагностика:** розпізнавання HDD / SATA SSD / NVMe, показники S.M.A.R.T., безпечна оптимізація (для SSD/NVMe — лише TRIM, для HDD — дефрагментація) та карта найбільших тек на томах.
- **Міграція ОС (`CopyOS`):** скомпільований C#-рушій клонування живої Windows на внутрішні диски (SATA / M.2 NVMe / RAID) та зовнішні USB-носії в режимах `BLOCK (VSS Volume Bitmap)` і `VSS + DISM` (GPT/UEFI або MBR/Legacy).
- **Оновлення з GitHub і цифровий підпис:** вбудована перевірка нових релізів із репозиторію `PIFAG0R0302/ARGUS-Updates` із верифікацією контрольної суми SHA-256 та відбитка сертифіката Authenticode перед запуском інсталятора.

## Технічний стек

- C# на `.NET 10 LTS`
- Avalonia UI 12 (апаратний рендеринг Skia через DirectX, без WebView2)
- Дворівнева телеметрія без драйвера `WinRing0.sys` (сумісна з Windows Defender і HVCI)

## Структура рішення

- `src/Argus.Core` — контракти, шина подій, кільцевий буфер, журнал операцій і налаштування.
- `src/Argus.Telemetry` — провайдери телеметрії (PDH, NVML, Storage IOCTL, ACPI, опційний PawnIO), сповіщення та експорт звітів.
- `src/Argus.Devices` — інвентаризація заліза, дерево пристроїв `SetupAPI` та монітор підключень.
- `src/Argus.Maintenance` — очищення ОС, оптимізація дисків, аналізатор простору, UAC, автозапуск, System Restore та автооновлення з GitHub Releases.
- `src/Argus.Migration` — C#-рушій клонування ОС (`NtfsVssCloner`, `MigrationPlanEvaluator`, `WindowsMigrationEngine`).
- `src/Argus.App` — графічний інтерфейс Avalonia UI.
- `tests/Argus.Core.Tests` і `tests/Argus.App.Tests` — набір із 87 автоматичних тестів (`xunit.v3`).
- `installer/Argus.iss` і `scripts/Build-Release.ps1` — відтворювана збірка підписаного інсталятора Inno Setup 6 і портативного архіву.

## Встановлення та збірка

Готовий інсталятор `Argus-Setup-*-win-x64.exe` та портативний архів `Argus-Portable-*-win-x64.zip` доступні в розділі [Releases](https://github.com/PIFAG0R0302/ARGUS-Updates/releases).

Для локальної збірки з вихідного коду:

```powershell
.\build.ps1 -Configuration Release -Strict -Test
.\scripts\Build-Release.ps1
```

## Ліцензія

Код поширюється на умовах ліцензії [MIT](LICENSE).
