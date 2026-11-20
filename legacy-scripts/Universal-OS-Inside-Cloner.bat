@echo off
chcp 65001 >nul
title Universal OS Inside Cloner v2.0 - SATA / M.2 NVMe

set "SELF_BAT=%~f0"
if not defined ORIG_LAUNCHER_DIR set "ORIG_LAUNCHER_DIR=%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator privileges ^(UAC^)...
    powershell.exe -NoProfile -Command "Start-Process -FilePath $env:SELF_BAT -Verb RunAs"
    exit /b
)

cd /d "%SystemRoot%\System32"

if /i not "%~d0"=="%SystemDrive%" (
    copy /y "%~f0" "%SystemRoot%\Temp\InsideCloner_Host.bat" >nul
    if exist "%SystemRoot%\Temp\InsideCloner_Host.bat" (
        "%SystemRoot%\Temp\InsideCloner_Host.bat"
        exit /b
    )
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$raw = [System.IO.File]::ReadAllText($env:SELF_BAT, [System.Text.Encoding]::UTF8); $m = '#__POWERSHELL_' + 'BEGIN__'; $idx = $raw.IndexOf($m); if ($idx -lt 0) { throw 'Embedded script payload not found.' }; $ps = $raw.Substring($idx + $m.Length); $tmp = Join-Path $env:SystemRoot ('Temp\InsideCloner_' + $PID + '.ps1'); [System.IO.File]::WriteAllText($tmp, $ps, [System.Text.UTF8Encoding]::new($true)); try { & $tmp; exit $LASTEXITCODE } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }"

echo.
pause
exit /b

#__POWERSHELL_BEGIN__
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8

$LauncherDir = if ($env:ORIG_LAUNCHER_DIR) { $env:ORIG_LAUNCHER_DIR } else { "$env:SystemDrive\" }
$LogPath = $null

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    switch ($Level) {
        "ERROR" { Write-Host $line -ForegroundColor Red }
        "WARN"  { Write-Host $line -ForegroundColor Yellow }
        "OK"    { Write-Host $line -ForegroundColor Green }
        default { Write-Host $line }
    }
    if ($script:LogPath) {
        for ($r = 0; $r -lt 5; $r++) {
            try {
                [System.IO.File]::AppendAllText($script:LogPath, $line + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
                break
            } catch {
                Start-Sleep -Milliseconds 80
            }
        }
    }
}

function Get-FreeDriveLetters {
    param([int]$Count = 2)
    $used = @()
    $used += @((Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue).Name)
    $used += @(Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { $_.DriveLetter })
    $used += @([System.IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1) })
    $used += @(Get-CimInstance Win32_LogicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID } | ForEach-Object { $_.DeviceID.Substring(0, 1) })
    $usedUpper = @($used | Where-Object { $_ } | ForEach-Object { $_.ToString().Trim().ToUpper() } | Where-Object { $_ } | Select-Object -Unique)
    $candidates = @('Z','Y','X','W','V','U','T','S','R','Q','P','O','N','M','L','K','J','I','H','G')
    $free = @()
    foreach ($c in $candidates) {
        if ($usedUpper -notcontains $c) {
            $free += $c
            if ($free.Count -ge $Count) { break }
        }
    }
    if ($free.Count -lt $Count) {
        throw "Не знайдено $Count вiльних букв диска (G..Z)."
    }
    return $free
}

function Set-PartLetterSafe {
    param([int]$DiskNum, [int]$PartNum, [string]$Letter)
    try {
        Set-Partition -DiskNumber $DiskNum -PartitionNumber $PartNum -NewDriveLetter $Letter -ErrorAction Stop
    } catch {
        Add-PartitionAccessPath -DiskNumber $DiskNum -PartitionNumber $PartNum -AccessPath "${Letter}:\" -ErrorAction Stop
    }
}

$shadowObj = $null
$sysHiveLoaded = $false
$usrHiveLoaded = $false
$vssJunction = $null
$tempWorkDir = $null

try {
    Clear-Host
    Write-Host "+============================================================================+" -ForegroundColor Cyan
    Write-Host "|             UNIVERSAL OS INSIDE CLONER  v2.0  ::  SATA / M.2 NVMe          |" -ForegroundColor Cyan
    Write-Host "|        Мiграцiя живоi Windows 10/11 на внутрiшнiй SATA або M.2 SSD         |" -ForegroundColor Cyan
    Write-Host "+============================================================================+" -ForegroundColor Cyan
    Write-Host ""

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw "Потрiбнi права адмiнiстратора."
    }

    # -------------------------------------------------------------------------
    # 1. Inspect Current Live OS
    # -------------------------------------------------------------------------
    $sysDrive = $env:SystemDrive.TrimEnd(':').ToUpper()
    $sysRoot  = "$($sysDrive):\"
    $osInfo   = Get-CimInstance Win32_OperatingSystem
    $srcPart  = Get-Partition -DriveLetter $sysDrive -ErrorAction Stop
    $srcDisk  = Get-Disk -Number $srcPart.DiskNumber -ErrorAction Stop
    $srcVol   = Get-Volume -DriveLetter $sysDrive -ErrorAction Stop

    $srcPartSize  = [int64]$srcPart.Size
    $srcUsedBytes = [int64]($srcVol.Size - $srcVol.SizeRemaining)
    $is64Bit      = [Environment]::Is64BitOperatingSystem
    $isUefiHost   = ($env:firmware_type -eq 'UEFI')

    $bitlockerStatus = "FullyDecrypted"
    if (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) {
        try {
            $bl = Get-BitLockerVolume -MountPoint "$($sysDrive):" -ErrorAction SilentlyContinue
            if ($bl) { $bitlockerStatus = $bl.VolumeStatus.ToString() }
        } catch {}
    }

    Write-Host "  [ Джерело ОС ]" -ForegroundColor White
    Write-Host ("    Система    : {0} ({1})" -f $osInfo.Caption.Trim(), $(if ($is64Bit) { "x64" } else { "x86" }))
    Write-Host ("    Диск       : {0}:  (Disk {1} - {2}, {3})" -f $sysDrive, $srcDisk.Number, $srcDisk.FriendlyName.Trim(), $srcDisk.BusType)
    Write-Host ("    Розмiр     : {0:N2} GiB  |  Зайнято: {1:N2} GiB" -f ($srcPartSize / 1GB), ($srcUsedBytes / 1GB))
    Write-Host ("    BitLocker  : {0}  |  BIOS ПК: {1}" -f $bitlockerStatus, $(if ($isUefiHost) { "UEFI" } else { "Legacy BIOS" }))
    Write-Host ""

    # -------------------------------------------------------------------------
    # 2. Discover & Select Target Internal Motherboard Disk (SATA / NVMe / RAID)
    # -------------------------------------------------------------------------
    $internalBusTypes = @('SATA', 'NVMe', 'RAID', 'ATA', 'SAS', 'SCSI')
    $selectedDisk = $null
    $selectedUsedDataGB = 0.0

    while (-not $selectedDisk) {
        $internalDisks = @(Get-Disk | Where-Object {
            $_.BusType -in $internalBusTypes -and
            -not $_.IsBoot -and
            -not $_.IsSystem -and
            $_.Number -ne $srcDisk.Number -and
            $_.Size -gt 0
        } | Sort-Object Number)

        if ($internalDisks.Count -eq 0) {
            Write-Host "  [!] Iншi внутрiшнi диски (SATA / M.2 NVMe) не знайденi!" -ForegroundColor Yellow
            Write-Host "      (Зовнiшнi USB-диски приховано; для USB використовуйте Outside-Cloner.bat)" -ForegroundColor DarkGray
            $ans = Read-Host "  Пiдключiть внутрiшнiй SSD або натиснiть [Enter] для оновлення, чи [Q] для виходу"
            if ($ans -match '^[Qq]') { exit 0 }
            Update-HostStorageCache
            Start-Sleep -Seconds 1
            continue
        }

        Write-Host "  [ Доступнi внутрiшнi диски (крiм поточного диска з ОС) ]" -ForegroundColor Green
        $diskUsedList = @()
        for ($i = 0; $i -lt $internalDisks.Count; $i++) {
            $d = $internalDisks[$i]
            $parts = @(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue)
            $volSummary = @()
            $totalUsedOnDisk = 0L
            foreach ($p in $parts) {
                if ($p.DriveLetter) {
                    $v = Get-Volume -DriveLetter $p.DriveLetter -ErrorAction SilentlyContinue
                    if ($v) {
                        $usedG = [math]::Round(($v.Size - $v.SizeRemaining) / 1GB, 1)
                        $totalUsedOnDisk += [int64]($v.Size - $v.SizeRemaining)
                        $lbl = if ($v.FileSystemLabel) { "$($v.FileSystemLabel), " } else { "" }
                        $volSummary += "$($p.DriveLetter): (${lbl}зайнято ${usedG} GiB)"
                    } else {
                        $volSummary += "$($p.DriveLetter):"
                    }
                }
            }
            $diskUsedList += [math]::Round($totalUsedOnDisk / 1GB, 2)
            $lettersText = if ($volSummary.Count -gt 0) { $volSummary -join '; ' } elseif ($parts.Count -eq 0) { "Новий / Нерозмiчений" } else { "без букв ($($parts.Count) розд.)" }
            $phys = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq "$($d.Number)" } | Select-Object -First 1
            $media = if ($phys -and $phys.MediaType -ne 'Unspecified') { $phys.MediaType } else { "Disk" }

            $color = if ($totalUsedOnDisk -gt 1GB) { "Yellow" } else { "Cyan" }
            Write-Host ("    [{0}] Disk {1} | {2} | {3:N2} GiB | {4} ({5}) | {6} | {7}" -f `
                ($i + 1), $d.Number, $d.FriendlyName.Trim(), ($d.Size / 1GB), $d.BusType, $media, $d.PartitionStyle, $lettersText) -ForegroundColor $color
        }
        Write-Host "    [R] Оновити список     [Q] Вихiд" -ForegroundColor DarkGray
        Write-Host ""

        $choice = (Read-Host "  Оберiть номер цiльового внутрiшнього диска (1..$($internalDisks.Count))").Trim()
        if ($choice -match '^[Qq]$') { exit 0 }
        if ($choice -match '^[Rr]$') {
            Update-HostStorageCache
            Start-Sleep -Seconds 1
            Write-Host ""
            continue
        }
        [int]$idxNum = 0
        if ([int]::TryParse($choice, [ref]$idxNum) -and $idxNum -ge 1 -and $idxNum -le $internalDisks.Count) {
            $selectedDisk = $internalDisks[$idxNum - 1]
            $selectedUsedDataGB = $diskUsedList[$idxNum - 1]
        } else {
            Write-Host "  [!] Невiрний вибiр, спробуйте ще раз." -ForegroundColor Red
            Write-Host ""
        }
    }

    $diskNum = [int]$selectedDisk.Number
    $targetSize = [int64]$selectedDisk.Size

    $targetDiskLetters = @(Get-Partition -DiskNumber $diskNum -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { $_.DriveLetter.ToString().ToUpper() })
    if ($targetDiskLetters -contains $sysDrive) {
        throw "КРИТИЧНА ПОМИЛКА БЕЗПЕКИ: Обраний диск мiстить активний системний роздiл $($sysDrive):!"
    }

    # -------------------------------------------------------------------------
    # 3. Determine Clone Method (Block Clone vs DISM Shrink-to-Fit)
    # -------------------------------------------------------------------------
    $sameSectorSize = ($srcDisk.LogicalSectorSize -eq $selectedDisk.LogicalSectorSize)
    $fitsBlockClone = ($targetSize -ge ($srcPartSize + 150MB))
    $bitlockerClean = ($bitlockerStatus -eq 'FullyDecrypted')

    $cloneMode = $null
    $tempCaptureDrive = $null

    if ($sameSectorSize -and $fitsBlockClone -and $bitlockerClean) {
        $cloneMode = "BLOCK"
        Write-Host ""
        Write-Host "  [+] Режим: Швидке двобуферне VSS-клонування (1-в-1 + розширення до повного розмiру SSD)." -ForegroundColor Green
    } else {
        $minNeeded = $srcUsedBytes + 12GB
        if ($targetSize -lt $minNeeded) {
            throw ("Недостатньо мiсця на цiльовому диску! На {0}: зайнято {1:N2} GiB (потрiбно хоча б {2:N2} GiB), а обраний диск мае {3:N2} GiB." -f `
                $sysDrive, ($srcUsedBytes / 1GB), ($minNeeded / 1GB), ($targetSize / 1GB))
        }

        $neededWimSpace = [int64]($srcUsedBytes * 0.78) + 4GB
        $candidateVol = Get-Volume | Where-Object {
            $_.DriveType -eq 'Fixed' -and
            $_.FileSystem -eq 'NTFS' -and
            $_.DriveLetter -and
            ($targetDiskLetters -notcontains $_.DriveLetter.ToString().ToUpper()) -and
            $_.SizeRemaining -ge $neededWimSpace
        } | Sort-Object SizeRemaining -Descending | Select-Object -First 1

        if (-not $candidateVol) {
            $reason = if (-not $fitsBlockClone) { "Роздiл $($sysDrive): ($([math]::Round($srcPartSize/1GB,1)) GiB) бiльший за новий диск ($([math]::Round($targetSize/1GB,1)) GiB)" }
                      elseif (-not $bitlockerClean) { "На $($sysDrive): увiмкнено BitLocker ($bitlockerStatus)" }
                      else { "Рiзний розмiр секторiв ($($srcDisk.LogicalSectorSize) vs $($selectedDisk.LogicalSectorSize))" }
            throw "$reason, i на iнших дисках недостатньо вiльного мiсця ($([math]::Round($neededWimSpace/1GB,1)) GiB) для тимчасового WIM-образу."
        }

        $cloneMode = "DISM"
        $tempCaptureDrive = $candidateVol.DriveLetter
        Write-Host ""
        Write-Host "  [+] Режим: Адаптивне VSS + DISM клонування (перенесення на менший/iнший за геометрiею SSD)." -ForegroundColor Yellow
        Write-Host ("      Тимчасовий буфер: {0}: (вiльно {1:N2} GiB)" -f $tempCaptureDrive, ($candidateVol.SizeRemaining / 1GB)) -ForegroundColor Gray
    }

    # -------------------------------------------------------------------------
    # 4. Boot Mode & Optional UEFI Priority + Redirected Folder Option
    # -------------------------------------------------------------------------
    Write-Host ""
    Write-Host "  [ Стиль роздiлiв цiльового диска ]" -ForegroundColor White
    Write-Host ("    [1] UEFI / GPT        - стандарт для Windows 10/11 i нових M.2/SATA SSD $(if ($isUefiHost) { '(Рекомендовано)' } else { '' })") -ForegroundColor Cyan
    Write-Host ("    [2] Legacy BIOS / MBR - для старих материнських плат без UEFI $(if (-not $isUefiHost) { '(Поточний режим ПК)' } else { '' })") -ForegroundColor Gray
    $defaultBoot = if ($isUefiHost) { "1" } else { "2" }
    $bmChoice = (Read-Host "  Ваш вибiр [Enter = $defaultBoot]").Trim()
    if (-not $bmChoice) { $bmChoice = $defaultBoot }
    $bootStyle = if ($bmChoice -eq '2') { "MBR" } else { "GPT" }

    $registerFwFirst = $false
    if ($bootStyle -eq "GPT" -and $isUefiHost) {
        $fwAns = (Read-Host "  Зробити новий SSD першим у черзi завантаження UEFI цього ПК автоматично? [Y/N, Enter = N]").Trim()
        if ($fwAns -match '^[YyТт]') { $registerFwFirst = $true }
    }

    $copyRedirectedDesktop = $false
    $redAns = (Read-Host "  Якщо Робочий стiл перенесено на iнший диск (напр. D:), скопiювати його на новий SSD? [Y/N, Enter = N]").Trim()
    if ($redAns -match '^[YyТт]') { $copyRedirectedDesktop = $true }

    # -------------------------------------------------------------------------
    # 5. Strong Safety Confirmation
    # -------------------------------------------------------------------------
    Write-Host ""
    Write-Host "+----------------------------------------------------------------------------+" -ForegroundColor Red
    Write-Host "|  УВАГА: УСI ДАНI НА ВНУТРIШНЬОМУ ДИСКУ БУДУТЬ ПОВНIСТЮ ЗНИЩЕНI!            |" -ForegroundColor Red
    Write-Host ("|  Цiль: Disk {0} ({1}, {2:N2} GiB, {3})" -f $diskNum, $selectedDisk.FriendlyName.Trim(), ($targetSize / 1GB), $selectedDisk.BusType).PadRight(77) + "|" -ForegroundColor Red
    if ($selectedUsedDataGB -gt 0.5) {
        Write-Host ("|  [!] НА ДИСКУ ЗАРАЗ ЗАЙНЯТО ~{0:N2} GiB ({1})!" -f $selectedUsedDataGB, ($targetDiskLetters -join ', ')).PadRight(77) + "|" -ForegroundColor Red
    }
    Write-Host ("|  Джерело ({0}:) НЕ буде змiнено." -f $sysDrive).PadRight(77) + "|" -ForegroundColor Green
    Write-Host ("|  Режим: {0}  |  Розмiтка: {1}  |  UEFI-прiоритет: {2}" -f $cloneMode, $bootStyle, $(if ($registerFwFirst) { "Так" } else { "Нi" })).PadRight(77) + "|" -ForegroundColor White
    Write-Host "+----------------------------------------------------------------------------+" -ForegroundColor Red
    $confirm = (Read-Host "  Щоб пiдтвердити очищення Disk $diskNum, введiть точне слово YES").Trim()
    if ($confirm -cne 'YES') {
        Write-Host "  Операцiю скасовано (потрiбно ввести YES великими лiтерами)." -ForegroundColor Yellow
        exit 0
    }

    # -------------------------------------------------------------------------
    # 6. Prepare Log Path & Dynamic Free Drive Letters
    # -------------------------------------------------------------------------
    $launcherDrive = if ($LauncherDir -match '^([A-Za-z]):') { $Matches[1].ToUpper() } else { $sysDrive }
    $logBaseDir = if ($targetDiskLetters -contains $launcherDrive) {
        $d = Join-Path "$($sysDrive):\" "UniversalClone_Logs"
        if (-not (Test-Path $d)) { New-Item -Path $d -ItemType Directory -Force | Out-Null }
        $d
    } else {
        $LauncherDir
    }
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $script:LogPath = Join-Path $logBaseDir "UniversalInsideClone_$stamp.log"
    [System.IO.File]::WriteAllText($script:LogPath, "", [System.Text.Encoding]::UTF8)

    Write-Log "=== Universal OS Inside Cloner v2.0 (SATA / NVMe) Started ==="
    Write-Log ("Source: {0}: (Disk {1}, {2}, {3:N2} GiB used / {4:N2} GiB) -> Target: Disk {5} ({6}, {7}, {8:N2} GiB, {9}, {10})" -f `
        $sysDrive, $srcDisk.Number, $srcDisk.BusType, ($srcUsedBytes / 1GB), ($srcPartSize / 1GB), $diskNum, $selectedDisk.FriendlyName.Trim(), $selectedDisk.BusType, ($targetSize / 1GB), $cloneMode, $bootStyle)

    $freeLetters = Get-FreeDriveLetters -Count 2
    $bootLetter = $freeLetters[0]
    $osLetter   = $freeLetters[1]
    Write-Log "Allocated temporary drive letters: Boot=${bootLetter}:, OS=${osLetter}:"

    # -------------------------------------------------------------------------
    # 7. Create VSS Snapshot of Live OS Drive
    # -------------------------------------------------------------------------
    Write-Log "Creating Volume Shadow Copy (VSS) snapshot of $sysRoot..."
    $vssService = Get-Service -Name vss -ErrorAction SilentlyContinue
    if ($vssService -and $vssService.Status -ne 'Running') { Start-Service -Name vss -ErrorAction Stop }

    $vssClass = [WMICLASS]"root\cimv2:Win32_ShadowCopy"
    $vssCreate = $vssClass.Create($sysRoot, "ClientAccessible")
    if ($vssCreate.ReturnValue -ne 0) {
        throw "Не вдалося створити VSS-знiмок для $sysRoot (код $($vssCreate.ReturnValue))."
    }

    $shadowId = $vssCreate.ShadowID
    $shadowObj = Get-CimInstance Win32_ShadowCopy | Where-Object { $_.ID -eq $shadowId }
    if (-not $shadowObj) { throw "VSS-знiмок $shadowId не знайдено через CIM." }
    $sourceDevice = $shadowObj.DeviceObject.TrimEnd('\')
    Write-Log "VSS Snapshot ready: $shadowId ($sourceDevice)"

    # -------------------------------------------------------------------------
    # 8. Partition Target Internal Disk
    # -------------------------------------------------------------------------
    Write-Log "Clearing Disk $diskNum and initializing $bootStyle layout..."
    if ($selectedDisk.IsOffline) { Set-Disk -Number $diskNum -IsOffline $false }
    if ($selectedDisk.IsReadOnly) { Set-Disk -Number $diskNum -IsReadOnly $false }

    Get-Partition -DiskNumber $diskNum -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object {
        Remove-PartitionAccessPath -DiskNumber $diskNum -PartitionNumber $_.PartitionNumber -AccessPath "$($_.DriveLetter):\" -ErrorAction SilentlyContinue
    }
    Clear-Disk -Number $diskNum -RemoveData -RemoveOEM -Confirm:$false
    Initialize-Disk -Number $diskNum -PartitionStyle $bootStyle -Confirm:$false
    Start-Sleep -Seconds 1
    Get-Partition -DiskNumber $diskNum -ErrorAction SilentlyContinue | Remove-Partition -Confirm:$false -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1

    $osPart = $null
    $bootPart = $null

    if ($bootStyle -eq "GPT") {
        Write-Log "Creating Partition 1 (100 MB FAT32 EFI System Partition -> ${bootLetter}:)..."
        $bootPart = New-Partition -DiskNumber $diskNum -Size 104857600 -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
        Format-Volume -Partition $bootPart -FileSystem FAT32 -NewFileSystemLabel "ESP_SYS" -Confirm:$false | Out-Null
        Set-PartLetterSafe -DiskNum $diskNum -PartNum $bootPart.PartitionNumber -Letter $bootLetter
        Start-Sleep -Seconds 1

        Write-Log "Creating Partition 2 (16 MB MSR)..."
        New-Partition -DiskNumber $diskNum -Size 16777216 -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null

        if ($cloneMode -eq "BLOCK") {
            Write-Log "Creating Partition 3 (OS Target >= $srcPartSize bytes)..."
            $osPart = New-Partition -DiskNumber $diskNum -Size $srcPartSize -Alignment $selectedDisk.LogicalSectorSize -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
            if ([int64]$osPart.Size -lt $srcPartSize) {
                Remove-Partition -DiskNumber $diskNum -PartitionNumber $osPart.PartitionNumber -Confirm:$false
                $osPart = New-Partition -DiskNumber $diskNum -Size ($srcPartSize + 2097152) -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
            }
        } else {
            Write-Log "Creating Partition 3 (OS Target - full remaining capacity -> ${osLetter}:)..."
            $osPart = New-Partition -DiskNumber $diskNum -UseMaximumSize -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
            Format-Volume -Partition $osPart -FileSystem NTFS -NewFileSystemLabel "WindowsSSD" -Confirm:$false | Out-Null
            Set-PartLetterSafe -DiskNum $diskNum -PartNum $osPart.PartitionNumber -Letter $osLetter
        }
    } else {
        Write-Log "Creating MBR Partition 1 (100 MB Active NTFS -> ${bootLetter}:)..."
        $bootPart = New-Partition -DiskNumber $diskNum -Size 104857600 -MbrType IFS -IsActive
        Format-Volume -Partition $bootPart -FileSystem NTFS -NewFileSystemLabel "BOOT_SYS" -Confirm:$false | Out-Null
        Set-PartLetterSafe -DiskNum $diskNum -PartNum $bootPart.PartitionNumber -Letter $bootLetter
        Start-Sleep -Seconds 1

        if ($cloneMode -eq "BLOCK") {
            Write-Log "Creating MBR Partition 2 (OS Target >= $srcPartSize bytes)..."
            $osPart = New-Partition -DiskNumber $diskNum -Size $srcPartSize -Alignment $selectedDisk.LogicalSectorSize -MbrType IFS
            if ([int64]$osPart.Size -lt $srcPartSize) {
                Remove-Partition -DiskNumber $diskNum -PartitionNumber $osPart.PartitionNumber -Confirm:$false
                $osPart = New-Partition -DiskNumber $diskNum -Size ($srcPartSize + 2097152) -MbrType IFS
            }
        } else {
            Write-Log "Creating MBR Partition 2 (OS Target - full remaining capacity -> ${osLetter}:)..."
            $osPart = New-Partition -DiskNumber $diskNum -UseMaximumSize -MbrType IFS
            Format-Volume -Partition $osPart -FileSystem NTFS -NewFileSystemLabel "WindowsSSD" -Confirm:$false | Out-Null
            Set-PartLetterSafe -DiskNum $diskNum -PartNum $osPart.PartitionNumber -Letter $osLetter
        }
    }

    # -------------------------------------------------------------------------
    # 9. Clone OS Data (Mode 1: Double-Buffered Block VSS, or Mode 2: DISM)
    # -------------------------------------------------------------------------
    if ($cloneMode -eq "BLOCK") {
        if ($osPart.DriveLetter) {
            Remove-PartitionAccessPath -DiskNumber $diskNum -PartitionNumber $osPart.PartitionNumber -AccessPath "$($osPart.DriveLetter):\" -ErrorAction SilentlyContinue
        }

        $targetVolumePath = $null
        for ($i = 0; $i -lt 20; $i++) {
            $osPart = Get-Partition -DiskNumber $diskNum -PartitionNumber $osPart.PartitionNumber
            $volPath = $osPart.AccessPaths | Where-Object { $_ -like '\\?\Volume{*' } | Select-Object -First 1
            if ($volPath) {
                $targetVolumePath = $volPath.TrimEnd('\')
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $targetVolumePath) { throw "Не вдалося отримати Volume GUID для цiльового роздiлу." }

        Write-Log "Compiling NtfsVssCloner (double-buffered pipeline) and starting internal SSD clone..."
        $csharpCode = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static class NtfsVssCloner
{
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    const uint FSCTL_LOCK_VOLUME = 0x00090018;
    const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;

    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RESERVE = 0x2000;
    const uint MEM_RELEASE = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(
        SafeFileHandle hFile, long liDistanceToMove,
        out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(
        SafeFileHandle hFile, IntPtr lpBuffer,
        int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(
        SafeFileHandle hFile, IntPtr lpBuffer,
        int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceW(
        string lpRootPathName, out uint lpSectorsPerCluster,
        out uint lpBytesPerSector, out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll")]
    static extern void RtlZeroMemory(IntPtr dst, UIntPtr length);

    public struct Chunk { public long Offset; public int Length; }

    struct Slot { public long Offset; public int Length; public bool HasData; }

    static void LogFileOnly(string logPath, string msg)
    {
        if (String.IsNullOrEmpty(logPath)) return;
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [CLONE] " + msg;
        for (int i = 0; i < 5; i++)
        {
            try { File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); break; }
            catch { Thread.Sleep(50); }
        }
    }

    static void LogBoth(string logPath, string msg)
    {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [CLONE] " + msg;
        Console.WriteLine(line);
        LogFileOnly(logPath, msg);
    }

    static void RenderProgressBar(int pct, long bytesCopied, long totalBytesToCopy, double speedMBs, TimeSpan eta)
    {
        int bars = pct / 5;
        string bar = new string('#', bars) + new string('.', 20 - bars);
        string text = String.Format("  [{0}] {1,3}% | {2,6:N2} / {3,6:N2} GiB | {4,5:N1} MB/s | ETA {5:hh\\:mm\\:ss}",
            bar, pct,
            bytesCopied / (1024.0 * 1024.0 * 1024.0),
            totalBytesToCopy / (1024.0 * 1024.0 * 1024.0),
            speedMBs, eta);
        if (!Console.IsOutputRedirected)
        {
            Console.Write("\r" + text + "   ");
            if (bytesCopied >= totalBytesToCopy) Console.WriteLine();
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    public static void CopyChunks(SafeFileHandle hSource, SafeFileHandle hTarget, List<Chunk> chunks,
        long partitionSize, int sectorSize, long totalBytesToCopy, string logPath)
    {
        const int MAX_IO = 16 * 1024 * 1024;
        IntPtr[] bufs = new IntPtr[2];
        bufs[0] = VirtualAlloc(IntPtr.Zero, (UIntPtr)MAX_IO, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        bufs[1] = VirtualAlloc(IntPtr.Zero, (UIntPtr)MAX_IO, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (bufs[0] == IntPtr.Zero || bufs[1] == IntPtr.Zero)
        {
            if (bufs[0] != IntPtr.Zero) VirtualFree(bufs[0], UIntPtr.Zero, MEM_RELEASE);
            if (bufs[1] != IntPtr.Zero) VirtualFree(bufs[1], UIntPtr.Zero, MEM_RELEASE);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAlloc failed for double-buffer I/O.");
        }

        object sync = new object();
        Slot[] slots = new Slot[2];
        bool readerDone = false;
        Exception workerErr = null;
        Thread readerThread = null;

        try
        {
            readerThread = new Thread(() =>
            {
                int readSlot = 0;
                try
                {
                    foreach (Chunk c in chunks)
                    {
                        lock (sync)
                        {
                            while (slots[readSlot].HasData && workerErr == null)
                                Monitor.Wait(sync);
                            if (workerErr != null) break;
                        }

                        long newPos;
                        if (!SetFilePointerEx(hSource, c.Offset, out newPos, 0))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetFilePointerEx failed on source at " + c.Offset);

                        int totalRead = 0;
                        while (totalRead < c.Length)
                        {
                            int br;
                            if (!ReadFile(hSource, IntPtr.Add(bufs[readSlot], totalRead), c.Length - totalRead, out br, IntPtr.Zero))
                            {
                                int err = Marshal.GetLastWin32Error();
                                if (c.Offset + totalRead >= partitionSize - sectorSize * 8) break;
                                throw new Win32Exception(err, "ReadFile failed on source at offset " + (c.Offset + totalRead));
                            }
                            if (br == 0)
                            {
                                if (c.Offset + totalRead >= partitionSize - sectorSize * 8) break;
                                throw new EndOfStreamException("Unexpected EOF on source at offset " + (c.Offset + totalRead));
                            }
                            totalRead += br;
                        }

                        if (totalRead < c.Length)
                            RtlZeroMemory(IntPtr.Add(bufs[readSlot], totalRead), (UIntPtr)(c.Length - totalRead));

                        lock (sync)
                        {
                            slots[readSlot].Offset = c.Offset;
                            slots[readSlot].Length = c.Length;
                            slots[readSlot].HasData = true;
                            Monitor.PulseAll(sync);
                        }
                        readSlot = 1 - readSlot;
                    }
                }
                catch (Exception ex)
                {
                    lock (sync)
                    {
                        if (workerErr == null) workerErr = ex;
                        Monitor.PulseAll(sync);
                    }
                }
                finally
                {
                    lock (sync)
                    {
                        readerDone = true;
                        Monitor.PulseAll(sync);
                    }
                }
            });
            readerThread.IsBackground = true;
            readerThread.Start();

            Stopwatch sw = Stopwatch.StartNew();
            long bytesCopied = 0;
            int writeSlot = 0;
            int nextLogPct = 5;
            double lastUiSec = -10.0;

            try
            {
                while (true)
                {
                    long offset;
                    int len;
                    lock (sync)
                    {
                        while (!slots[writeSlot].HasData && !readerDone && workerErr == null)
                            Monitor.Wait(sync);
                        if (workerErr != null) throw workerErr;
                        if (!slots[writeSlot].HasData && readerDone) break;
                        offset = slots[writeSlot].Offset;
                        len = slots[writeSlot].Length;
                    }

                    long newPos;
                    if (!SetFilePointerEx(hTarget, offset, out newPos, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetFilePointerEx failed on target at " + offset);

                    int totalWritten = 0;
                    while (totalWritten < len)
                    {
                        int bw;
                        if (!WriteFile(hTarget, IntPtr.Add(bufs[writeSlot], totalWritten), len - totalWritten, out bw, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile failed on target at offset " + (offset + totalWritten));
                        if (bw == 0)
                            throw new IOException("WriteFile wrote 0 bytes at offset " + (offset + totalWritten));
                        totalWritten += bw;
                    }

                    lock (sync)
                    {
                        slots[writeSlot].HasData = false;
                        Monitor.PulseAll(sync);
                    }
                    writeSlot = 1 - writeSlot;

                    bytesCopied += len;
                    int pct = (int)((bytesCopied * 100L) / totalBytesToCopy);
                    double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    double speedMBs = (bytesCopied / (1024.0 * 1024.0)) / elapsedSec;
                    double etaSec = (totalBytesToCopy - bytesCopied) / Math.Max(1.0, bytesCopied / elapsedSec);
                    TimeSpan etaTs = TimeSpan.FromSeconds(etaSec);

                    if ((elapsedSec - lastUiSec) >= 1.0 || pct >= nextLogPct || bytesCopied >= totalBytesToCopy)
                    {
                        RenderProgressBar(pct, bytesCopied, totalBytesToCopy, speedMBs, etaTs);
                        lastUiSec = elapsedSec;
                    }
                    if (pct >= nextLogPct || bytesCopied >= totalBytesToCopy)
                    {
                        LogFileOnly(logPath, String.Format("Progress: {0}% ({1:N2} / {2:N2} GiB) | Speed: {3:N1} MB/s | ETA: {4:hh\\:mm\\:ss}",
                            pct, bytesCopied / (1024.0 * 1024.0 * 1024.0), totalBytesToCopy / (1024.0 * 1024.0 * 1024.0), speedMBs, etaTs));
                        while (nextLogPct <= pct) nextLogPct += 5;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    if (workerErr == null) workerErr = ex;
                    Monitor.PulseAll(sync);
                }
                throw;
            }
            finally
            {
                if (readerThread != null) readerThread.Join();
            }

            long pos;
            if (!SetFilePointerEx(hTarget, 0, out pos, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Seek to sector 0 failed.");
            int vbrRead;
            if (!ReadFile(hTarget, bufs[0], sectorSize, out vbrRead, IntPtr.Zero) || vbrRead != sectorSize)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read sector 0 failed.");

            byte[] oem = new byte[8];
            Marshal.Copy(IntPtr.Add(bufs[0], 3), oem, 0, 8);
            string oemStr = Encoding.ASCII.GetString(oem);
            if (oemStr != "NTFS    ")
                throw new InvalidOperationException("Cloned sector 0 does not contain 'NTFS    ' signature.");

            long ntfsTotalSectors = Marshal.ReadInt64(bufs[0], 0x28);
            long backupVbrOffset = ntfsTotalSectors * (long)sectorSize;
            if (backupVbrOffset <= 0 || backupVbrOffset + sectorSize > partitionSize)
                backupVbrOffset = partitionSize - sectorSize;

            byte oldByte = Marshal.ReadByte(bufs[0], 0x4B);
            byte newByte = unchecked((byte)(oldByte + 1));
            Marshal.WriteByte(bufs[0], 0x4B, newByte);
            LogBoth(logPath, String.Format("Updated NTFS Volume Serial byte 0x4B: 0x{0:X2} -> 0x{1:X2} (Backup VBR offset={2})",
                oldByte, newByte, backupVbrOffset));

            int vbrWritten;
            if (!SetFilePointerEx(hTarget, 0, out pos, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Seek to sector 0 for write failed.");
            if (!WriteFile(hTarget, bufs[0], sectorSize, out vbrWritten, IntPtr.Zero) || vbrWritten != sectorSize)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Write updated sector 0 failed.");

            if (!SetFilePointerEx(hTarget, backupVbrOffset, out pos, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Seek to backup boot sector failed.");
            if (!WriteFile(hTarget, bufs[0], sectorSize, out vbrWritten, IntPtr.Zero) || vbrWritten != sectorSize)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Write updated backup boot sector failed.");

            FlushFileBuffers(hTarget);
            LogBoth(logPath, String.Format("Block clone completed in {0:hh\\:mm\\:ss}.", sw.Elapsed));
        }
        finally
        {
            if (readerThread != null && readerThread.IsAlive) readerThread.Join();
            VirtualFree(bufs[0], UIntPtr.Zero, MEM_RELEASE);
            VirtualFree(bufs[1], UIntPtr.Zero, MEM_RELEASE);
        }
    }

    public static void CloneVolume(string sourceRootPath, string sourceDevicePath, string targetVolumePath, long partitionSize, string logPath)
    {
        uint sectorsPerCluster, bytesPerSector, freeClusters, totalClustersFromApi;
        if (!GetDiskFreeSpaceW(sourceRootPath, out sectorsPerCluster, out bytesPerSector, out freeClusters, out totalClustersFromApi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDiskFreeSpaceW failed on " + sourceRootPath);

        long bytesPerCluster = (long)sectorsPerCluster * (long)bytesPerSector;
        int sectorSize = (int)bytesPerSector;
        LogBoth(logPath, String.Format("NTFS Geometry: BytesPerSector={0}, SectorsPerCluster={1}, BytesPerCluster={2}",
            bytesPerSector, sectorsPerCluster, bytesPerCluster));

        using (SafeFileHandle hSource = CreateFileW(sourceDevicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero))
        {
            if (hSource.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW failed for source VSS: " + sourceDevicePath);

            int dummy;
            DeviceIoControl(hSource, FSCTL_ALLOW_EXTENDED_DASD_IO, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);

            long estClusters = partitionSize / bytesPerCluster;
            int bitmapBufSize = (int)Math.Max(16 * 1024 * 1024 + 64, (estClusters / 8) + 1048576);
            IntPtr inBuf = Marshal.AllocHGlobal(8);
            IntPtr outBuf = VirtualAlloc(IntPtr.Zero, (UIntPtr)bitmapBufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (outBuf == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAlloc failed for bitmap buffer.");

            byte[] bitmap;
            long bitmapSizeClusters;
            try
            {
                Marshal.WriteInt64(inBuf, 0, 0L);
                int bytesReturned;
                if (!DeviceIoControl(hSource, FSCTL_GET_VOLUME_BITMAP, inBuf, 8, outBuf, bitmapBufSize, out bytesReturned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_VOLUME_BITMAP failed.");

                long startingLcn = Marshal.ReadInt64(outBuf, 0);
                bitmapSizeClusters = Marshal.ReadInt64(outBuf, 8);
                if (startingLcn != 0) throw new InvalidOperationException("Unexpected StartingLcn=" + startingLcn);

                int rawBitmapBytes = bytesReturned - 16;
                bitmap = new byte[rawBitmapBytes];
                Marshal.Copy(IntPtr.Add(outBuf, 16), bitmap, 0, rawBitmapBytes);
                LogBoth(logPath, String.Format("Volume bitmap read: {0:N0} clusters ({1:N2} MB bitmap).",
                    bitmapSizeClusters, rawBitmapBytes / (1024.0 * 1024.0)));
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                VirtualFree(outBuf, UIntPtr.Zero, MEM_RELEASE);
            }

            const long BLOCK_SIZE = 1048576;
            int totalBlocks = (int)((partitionSize + BLOCK_SIZE - 1) / BLOCK_SIZE);
            bool[] active = new bool[totalBlocks];
            int clustersPerBlock = (int)(BLOCK_SIZE / bytesPerCluster);
            int bytesPerBlockBitmap = clustersPerBlock / 8;

            for (int b = 0; b < totalBlocks; b++)
            {
                long startCluster = (long)b * clustersPerBlock;
                long endCluster = Math.Min(startCluster + clustersPerBlock, bitmapSizeClusters);
                if (startCluster >= bitmapSizeClusters) break;

                if (clustersPerBlock >= 8 && (clustersPerBlock & 7) == 0 && endCluster - startCluster == clustersPerBlock && (startCluster & 7) == 0)
                {
                    int startByteIdx = (int)(startCluster >> 3);
                    if (startByteIdx + bytesPerBlockBitmap <= bitmap.Length)
                    {
                        for (int k = 0; k < bytesPerBlockBitmap; k++)
                        {
                            if (bitmap[startByteIdx + k] != 0) { active[b] = true; break; }
                        }
                        continue;
                    }
                }
                for (long c = startCluster; c < endCluster; c++)
                {
                    if ((bitmap[c >> 3] & (1 << (int)(c & 7))) != 0) { active[b] = true; break; }
                }
            }

            for (int b = 0; b < Math.Min(8, totalBlocks); b++) active[b] = true;
            for (int b = Math.Max(0, totalBlocks - 8); b < totalBlocks; b++) active[b] = true;

            bool[] bridged = (bool[])active.Clone();
            for (int b = 1; b < totalBlocks - 1; b++)
            {
                if (!active[b] && active[b - 1] && active[b + 1]) bridged[b] = true;
            }
            active = bridged;

            List<Chunk> chunks = new List<Chunk>();
            long totalBytesToCopy = 0;
            int idx = 0;
            while (idx < totalBlocks)
            {
                if (!active[idx]) { idx++; continue; }
                int runStart = idx;
                while (idx < totalBlocks && active[idx] && (idx - runStart) < 16) idx++;
                long offset = (long)runStart * BLOCK_SIZE;
                long endOffset = Math.Min((long)idx * BLOCK_SIZE, partitionSize);
                int len = (int)(endOffset - offset);
                chunks.Add(new Chunk { Offset = offset, Length = len });
                totalBytesToCopy += len;
            }

            LogBoth(logPath, String.Format("Block map ready: {0:N0} chunks, {1:N2} GiB to copy (out of {2:N2} GiB partition).",
                chunks.Count, totalBytesToCopy / (1024.0 * 1024.0 * 1024.0), partitionSize / (1024.0 * 1024.0 * 1024.0)));

            using (SafeFileHandle hTarget = CreateFileW(targetVolumePath, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero))
            {
                if (hTarget.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW failed for target volume: " + targetVolumePath);

                bool locked = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (DeviceIoControl(hTarget, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero))
                    {
                        locked = true;
                        break;
                    }
                    Thread.Sleep(500);
                }
                if (!locked)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_LOCK_VOLUME failed on target volume.");

                DeviceIoControl(hTarget, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);
                DeviceIoControl(hTarget, FSCTL_ALLOW_EXTENDED_DASD_IO, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);

                CopyChunks(hSource, hTarget, chunks, partitionSize, sectorSize, totalBytesToCopy, logPath);

                DeviceIoControl(hTarget, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);
                DeviceIoControl(hTarget, FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);
            }
        }
    }
}
'@
        Add-Type -TypeDefinition $csharpCode -Language CSharp
        [NtfsVssCloner]::CloneVolume($sysRoot, $sourceDevice, $targetVolumePath, $srcPartSize, $script:LogPath)

        Write-Log "Removing temporary VSS snapshot $shadowId..."
        $shadowObj | Remove-CimInstance
        $shadowObj = $null

        Write-Log "Mounting cloned partition as ${osLetter}:, running chkdsk /f /x, and extending to full SSD capacity..."
        Update-HostStorageCache
        Start-Sleep -Seconds 2
        Set-PartLetterSafe -DiskNum $diskNum -PartNum $osPart.PartitionNumber -Letter $osLetter

        $mounted = $false
        for ($i = 0; $i -lt 30; $i++) {
            if (Test-Path "${osLetter}:\Windows") { $mounted = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $mounted) { throw "Не вдалося змонтувати клонований роздiл як ${osLetter}:\" }

        $ErrorActionPreference = 'Continue'
        $chkdskOut = & "$env:SystemRoot\System32\chkdsk.exe" "${osLetter}:" /f /x 2>&1
        $chkdskExit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        Write-Log "chkdsk exited with code $chkdskExit"
        if ($chkdskExit -ge 3) {
            Write-Log ($chkdskOut -join "`n") "ERROR"
            throw "chkdsk ${osLetter}: /f /x завершився з помилкою $chkdskExit."
        }

        $supported = Get-PartitionSupportedSize -DiskNumber $diskNum -PartitionNumber $osPart.PartitionNumber
        $maxSize = [int64]$supported.SizeMax
        if ($maxSize -gt [int64]$osPart.Size) {
            Write-Log ("Extending OS partition to {0:N2} GiB..." -f ($maxSize / 1GB))
            Resize-Partition -DiskNumber $diskNum -PartitionNumber $osPart.PartitionNumber -Size $maxSize
        }
        Set-Volume -DriveLetter $osLetter -NewFileSystemLabel "WindowsSSD"

        $oVol = Get-Volume -DriveLetter $osLetter
        if ([int64]$oVol.Size -lt ($maxSize - 50MB)) {
            $extScript = [System.IO.Path]::GetTempFileName()
            @("select volume $osLetter", "extend filesystem") | Set-Content -Path $extScript -Encoding ASCII
            $ErrorActionPreference = 'Continue'
            & "$env:SystemRoot\System32\diskpart.exe" /s $extScript | Out-Null
            $ErrorActionPreference = 'Stop'
            Remove-Item $extScript -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        # MODE 2: VSS + DISM Shrink-to-Fit
        $tempWorkDir = "$($tempCaptureDrive):\_WinInsideClone_Temp_$PID"
        $scratchDir  = Join-Path $tempWorkDir "Scratch"
        $wimPath     = Join-Path $tempWorkDir "install.wim"
        $wimCfg      = Join-Path $tempWorkDir "WimScript.ini"
        $vssJunction = "$($sysDrive):\_WinInsideClone_VSS_$PID"

        New-Item -Path $scratchDir -ItemType Directory -Force | Out-Null

        $wimScriptBody = @"
[ExclusionList]
\pagefile.sys
\hiberfil.sys
\swapfile.sys
\`$Recycle.Bin
\System Volume Information
\_WinInsideClone_Temp_*
\_WinInsideClone_VSS_*
"@
        [System.IO.File]::WriteAllText($wimCfg, $wimScriptBody, [System.Text.Encoding]::ASCII)

        Write-Log "Creating temporary VSS junction $vssJunction -> $sourceDevice\..."
        $ErrorActionPreference = 'Continue'
        & cmd.exe /c "mklink /d `"$vssJunction`" `"$sourceDevice\`"" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $ErrorActionPreference = 'Stop'
            throw "Не вдалося створити тимчасовий symlink на VSS ($vssJunction)."
        }

        Write-Log "Capturing live OS snapshot to $wimPath via DISM (/Compress:fast)..."
        & "$env:SystemRoot\System32\dism.exe" /Capture-Image /ImageFile:"$wimPath" /CaptureDir:"$vssJunction" /Name:"WindowsSSD" /Compress:fast /ConfigFile:"$wimCfg" /ScratchDir:"$scratchDir"
        if ($LASTEXITCODE -ne 0) {
            $ErrorActionPreference = 'Stop'
            throw "DISM /Capture-Image завершився з помилкою $LASTEXITCODE."
        }

        Write-Log "Applying WIM image onto ${osLetter}:\ via DISM..."
        & "$env:SystemRoot\System32\dism.exe" /Apply-Image /ImageFile:"$wimPath" /Index:1 /ApplyDir:"${osLetter}:\" /ScratchDir:"$scratchDir"
        if ($LASTEXITCODE -ne 0) {
            $ErrorActionPreference = 'Stop'
            throw "DISM /Apply-Image завершився з помилкою $LASTEXITCODE."
        }
        $ErrorActionPreference = 'Stop'

        & cmd.exe /c "rmdir `"$vssJunction`"" 2>&1 | Out-Null
        $vssJunction = $null
        Remove-Item -Path $tempWorkDir -Recurse -Force -ErrorAction SilentlyContinue
        $tempWorkDir = $null
        $shadowObj | Remove-CimInstance
        $shadowObj = $null
    }

    $oVol = Get-Volume -DriveLetter $osLetter
    Write-Log ("Target OS Volume {0}: ready -> Size={1:N2} GiB, Free={2:N2} GiB" -f $osLetter, ($oVol.Size / 1GB), ($oVol.SizeRemaining / 1GB)) "OK"

    # -------------------------------------------------------------------------
    # 10. Optional Redirected Desktop Copy (only if user opted in)
    # -------------------------------------------------------------------------
    if ($copyRedirectedDesktop) {
        Write-Log "Checking user profiles for redirected Desktop..."
        $userDirs = Get-ChildItem "${osLetter}:\Users" -Directory -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -notin @('Public', 'Default', 'Default User', 'All Users') -and (Test-Path (Join-Path $_.FullName "NTUSER.DAT"))
        }
        $ErrorActionPreference = 'Continue'
        foreach ($uDir in $userDirs) {
            $uName = $uDir.Name
            $ntuser = Join-Path $uDir.FullName "NTUSER.DAT"
            & "$env:SystemRoot\System32\reg.exe" load HKLM\CLONE_USR "$ntuser" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { continue }
            $usrHiveLoaded = $true

            $usfKey = "HKLM:\CLONE_USR\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
            if (Test-Path $usfKey) {
                $props = Get-ItemProperty $usfKey -ErrorAction SilentlyContinue
                $rawDesk = $props.Desktop
                $expanded = if ($rawDesk) { $rawDesk -replace '%USERPROFILE%', "$($sysDrive):\Users\$uName" } else { $null }
                if ($expanded -and $expanded -match '^([A-Za-z]):\\' -and $Matches[1].ToUpper() -ne $sysDrive -and (Test-Path $expanded)) {
                    $dstDesktop = Join-Path $uDir.FullName "Desktop"
                    if (-not (Test-Path $dstDesktop)) { New-Item -Path $dstDesktop -ItemType Directory -Force | Out-Null }
                    Write-Log "Copying redirected Desktop '$expanded' -> '$dstDesktop'..."
                    & "$env:SystemRoot\System32\robocopy.exe" "$expanded" "$dstDesktop" /E /COPY:DAT /XJ /R:1 /W:1 /MT:8 /NP /NFL /NDL | Out-Null
                    & "$env:SystemRoot\System32\reg.exe" add "HKLM\CLONE_USR\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders" /v Desktop /t REG_EXPAND_SZ /d '%USERPROFILE%\Desktop' /f 2>&1 | Out-Null
                    & "$env:SystemRoot\System32\reg.exe" add "HKLM\CLONE_USR\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders" /v Desktop /t REG_SZ /d "$($sysDrive):\Users\$uName\Desktop" /f 2>&1 | Out-Null
                }
            }
            $props = $null
            [GC]::Collect(); [GC]::WaitForPendingFinalizers()
            & "$env:SystemRoot\System32\reg.exe" unload HKLM\CLONE_USR 2>&1 | Out-Null
            $usrHiveLoaded = $false
        }
        $ErrorActionPreference = 'Stop'
    }

    # -------------------------------------------------------------------------
    # 11. Offline SYSTEM Registry Updates (MountedDevices + SATA/NVMe Boot Drivers)
    # -------------------------------------------------------------------------
    Write-Log "Configuring offline SYSTEM registry hive on ${osLetter}: for internal SATA/M.2 NVMe boot..."
    $ErrorActionPreference = 'Continue'
    & "$env:SystemRoot\System32\reg.exe" load HKLM\CLONE_SYS "${osLetter}:\Windows\System32\config\SYSTEM" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $ErrorActionPreference = 'Stop'
        throw "Не вдалося завантажити офлайн-реестр SYSTEM."
    }
    $sysHiveLoaded = $true

    $osPart = Get-Partition -DiskNumber $diskNum -PartitionNumber $osPart.PartitionNumber
    & "$env:SystemRoot\System32\reg.exe" delete "HKLM\CLONE_SYS\MountedDevices" /va /f 2>&1 | Out-Null
    if ($bootStyle -eq "GPT") {
        $targetPartGuid = [Guid]$osPart.Guid
        $idBytes = [System.Text.Encoding]::ASCII.GetBytes("DMIO:ID:") + $targetPartGuid.ToByteArray()
    } else {
        $mbrDisk = Get-Disk -Number $diskNum
        $idBytes = [BitConverter]::GetBytes([uint32]$mbrDisk.Signature) + [BitConverter]::GetBytes([int64]$osPart.Offset)
    }
    $idHex = ($idBytes | ForEach-Object { "{0:X2}" -f $_ }) -join ''
    & "$env:SystemRoot\System32\reg.exe" add "HKLM\CLONE_SYS\MountedDevices" /v "\DosDevices\${sysDrive}:" /t REG_BINARY /d $idHex /f 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не вдалося записати MountedDevices \DosDevices\${sysDrive}:" }
    Write-Log "Bound offline MountedDevices \DosDevices\${sysDrive}: -> $idHex"

    $hwKeys = & "$env:SystemRoot\System32\reg.exe" query "HKLM\CLONE_SYS\HardwareConfig" 2>&1 | Where-Object { $_ -match '^HKEY_LOCAL_MACHINE\\CLONE_SYS\\HardwareConfig\\' }
    foreach ($hk in $hwKeys) {
        $subName = ($hk.Trim() -split '\\')[-1]
        & "$env:SystemRoot\System32\reg.exe" add "HKLM\CLONE_SYS\HardwareConfig\$subName" /v BootDriverFlags /t REG_DWORD /d 28 /f 2>&1 | Out-Null
    }

    $csQuery = & "$env:SystemRoot\System32\reg.exe" query "HKLM\CLONE_SYS" 2>&1 | Where-Object { $_ -match '\\ControlSet\d+$' }
    $storageDrivers = @(
        'stornvme','storahci','pciide','intelide','amd_sata','amd_xata','amdxata',
        'iaStorV','iaStorA','iaStorAC','iaStorAVC','iaStorVD','nvraid','lsi_sas',
        'pci','acpi','partmgr','volmgr','disk'
    )
    foreach ($csPath in $csQuery) {
        $csName = ($csPath.Trim() -split '\\')[-1]
        & "$env:SystemRoot\System32\reg.exe" add "HKLM\CLONE_SYS\$csName\Control" /v BootDriverFlags /t REG_DWORD /d 28 /f 2>&1 | Out-Null
        foreach ($drv in $storageDrivers) {
            & "$env:SystemRoot\System32\reg.exe" query "HKLM\CLONE_SYS\$csName\Services\$drv" > $null 2>&1
            if ($LASTEXITCODE -eq 0) {
                & "$env:SystemRoot\System32\reg.exe" add "HKLM\CLONE_SYS\$csName\Services\$drv" /v Start /t REG_DWORD /d 0 /f 2>&1 | Out-Null
                & "$env:SystemRoot\System32\reg.exe" delete "HKLM\CLONE_SYS\$csName\Services\$drv\StartOverride" /f > $null 2>&1
            }
        }
        Write-Log "Enabled internal SATA/NVMe/RAID boot-start drivers (Start=0) in $csName."
    }

    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    & "$env:SystemRoot\System32\reg.exe" unload HKLM\CLONE_SYS 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не вдалося вивантажити HKLM\CLONE_SYS." }
    $sysHiveLoaded = $false
    $ErrorActionPreference = 'Stop'

    # -------------------------------------------------------------------------
    # 12. Install Bootloader (UEFI/GPT or Legacy BIOS/MBR) & Optional Firmware Sync
    # -------------------------------------------------------------------------
    Write-Log "Installing $bootStyle bootloader onto ${bootLetter}:..."
    $ErrorActionPreference = 'Continue'
    if ($bootStyle -eq "GPT") {
        $bcdHelp = (& "$env:SystemRoot\System32\bcdboot.exe" /? 2>&1) -join "`n"
        $bcdArgsSafe = @("${osLetter}:\Windows", "/s", "${bootLetter}:", "/f", "UEFI")
        if ($bcdHelp -match '/nofirmwaresync') { $bcdArgsSafe += "/nofirmwaresync" }
        $bcdOut = & "$env:SystemRoot\System32\bcdboot.exe" @bcdArgsSafe 2>&1
        if ($LASTEXITCODE -ne 0) { throw "bcdboot (UEFI) помилка: $($bcdOut -join ' ')" }

        if (-not (Test-Path "${bootLetter}:\EFI\Boot\bootx64.efi") -and (Test-Path "${bootLetter}:\EFI\Microsoft\Boot\bootmgfw.efi")) {
            New-Item -Path "${bootLetter}:\EFI\Boot" -ItemType Directory -Force | Out-Null
            Copy-Item "${bootLetter}:\EFI\Microsoft\Boot\bootmgfw.efi" "${bootLetter}:\EFI\Boot\bootx64.efi" -Force
        }

        $bcdStore = "${bootLetter}:\EFI\Microsoft\Boot\BCD"
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" description "Windows (New Internal SSD)" 2>&1 | Out-Null
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" bootstatuspolicy IgnoreAllFailures 2>&1 | Out-Null
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" recoveryenabled No 2>&1 | Out-Null
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{bootmgr}" default "{default}" 2>&1 | Out-Null
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{bootmgr}" displayorder "{default}" 2>&1 | Out-Null

        # Convert Partition 1 to official EFI System Partition
        $dpEsp = [System.IO.Path]::GetTempFileName()
        @("select disk $diskNum", "select partition 1", "set id=c12a7328-f81f-11d2-ba4b-00a0c93ec93b override") | Set-Content -Path $dpEsp -Encoding ASCII
        & "$env:SystemRoot\System32\diskpart.exe" /s $dpEsp 2>&1 | Out-Null
        Remove-Item $dpEsp -Force -ErrorAction SilentlyContinue

        if ($registerFwFirst) {
            # Re-ensure drive letter and run bcdboot without /nofirmwaresync now that ESP GUID is active
            $dpReg = [System.IO.Path]::GetTempFileName()
            @("select disk $diskNum", "select partition 1", "assign letter=$bootLetter") | Set-Content -Path $dpReg -Encoding ASCII
            & "$env:SystemRoot\System32\diskpart.exe" /s $dpReg 2>&1 | Out-Null
            Remove-Item $dpReg -Force -ErrorAction SilentlyContinue

            & "$env:SystemRoot\System32\bcdboot.exe" "${osLetter}:\Windows" /s "${bootLetter}:" /f UEFI 2>&1 | Out-Null
            & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" description "Windows (New Internal SSD)" 2>&1 | Out-Null
            & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" bootstatuspolicy IgnoreAllFailures 2>&1 | Out-Null
            & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" recoveryenabled No 2>&1 | Out-Null
            Write-Log "Registered new internal SSD in host UEFI firmware boot manager."
        }

        $dpPost = [System.IO.Path]::GetTempFileName()
        @("select disk $diskNum", "select partition 1", "remove letter=$bootLetter") | Set-Content -Path $dpPost -Encoding ASCII
        & "$env:SystemRoot\System32\diskpart.exe" /s $dpPost 2>&1 | Out-Null
        Remove-Item $dpPost -Force -ErrorAction SilentlyContinue
    } else {
        $bcdOut = & "$env:SystemRoot\System32\bcdboot.exe" "${osLetter}:\Windows" /s "${bootLetter}:" /f BIOS 2>&1
        if ($LASTEXITCODE -ne 0) { throw "bcdboot (BIOS) помилка: $($bcdOut -join ' ')" }
        & "$env:SystemRoot\System32\bootsect.exe" /nt60 "${bootLetter}:" /mbr 2>&1 | Out-Null

        $bcdStore = "${bootLetter}:\Boot\BCD"
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" description "Windows (New Internal SSD)" 2>&1 | Out-Null
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" bootstatuspolicy IgnoreAllFailures 2>&1 | Out-Null
        & "$env:SystemRoot\System32\bcdedit.exe" /store "$bcdStore" /set "{default}" recoveryenabled No 2>&1 | Out-Null

        $dpPost = [System.IO.Path]::GetTempFileName()
        @("select disk $diskNum", "select partition 1", "active", "remove letter=$bootLetter") | Set-Content -Path $dpPost -Encoding ASCII
        & "$env:SystemRoot\System32\diskpart.exe" /s $dpPost 2>&1 | Out-Null
        Remove-Item $dpPost -Force -ErrorAction SilentlyContinue
    }
    $ErrorActionPreference = 'Stop'

    Write-Log "=== INTERNAL SSD CLONE COMPLETED SUCCESSFULLY ===" "OK"
    Copy-Item -Path $script:LogPath -Destination "${osLetter}:\UniversalInsideClone.log" -Force -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "+============================================================================+" -ForegroundColor Green
    Write-Host "|  [OK] КЛОНУВАННЯ НА ВНУТРIШНIЙ ДИСК УСПIШНО ЗАВЕРШЕНО!                     |" -ForegroundColor Green
    Write-Host ("|  Диск {0} ({1}, {2}) готовий до запуску ({3})." -f $diskNum, $selectedDisk.FriendlyName.Trim(), $selectedDisk.BusType, $bootStyle).PadRight(77) + "|" -ForegroundColor White
    Write-Host ("|  Лог операцii: {0}" -f $script:LogPath).PadRight(77) + "|" -ForegroundColor Gray
    Write-Host "|  Що далi:                                                                  |" -ForegroundColor Yellow
    Write-Host "|    1. Можете вимкнути ПК, вийняти старий менший SSD i залишити новий,      |" -ForegroundColor Yellow
    Write-Host "|       АБО обрати новий SSD у BIOS / Boot Menu (F12 / F11 / F8).            |" -ForegroundColor Yellow
    Write-Host "|    2. Пiсля завантаження з нового диска вiн сам стане диском C:.           |" -ForegroundColor Yellow
    Write-Host "+============================================================================+" -ForegroundColor Green
    exit 0
}
catch {
    Write-Log ("FATAL ERROR: " + ($_ | Out-String)) "ERROR"
    exit 1
}
finally {
    if ($sysHiveLoaded) {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & "$env:SystemRoot\System32\reg.exe" unload HKLM\CLONE_SYS 2>&1 | Out-Null
    }
    if ($usrHiveLoaded) {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & "$env:SystemRoot\System32\reg.exe" unload HKLM\CLONE_USR 2>&1 | Out-Null
    }
    if ($vssJunction -and (Test-Path $vssJunction)) {
        & cmd.exe /c "rmdir `"$vssJunction`"" 2>&1 | Out-Null
    }
    if ($tempWorkDir -and (Test-Path $tempWorkDir)) {
        Remove-Item -Path $tempWorkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($shadowObj) {
        try { $shadowObj | Remove-CimInstance -ErrorAction SilentlyContinue } catch {}
    }
    if ($env:SELF_BAT -and ($env:SELF_BAT -like "*_Host.bat")) {
        Remove-Item $env:SELF_BAT -Force -ErrorAction SilentlyContinue
    }
}
