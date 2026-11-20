<#
.SYNOPSIS
    Prepares a clean 2-disk Hyper-V or VirtualBox test VM for live CopyOS migration testing (BLOCK / DISM, GPT / MBR).

.DESCRIPTION
    Creates a secondary blank virtual disk (VHDX for Hyper-V or VDI for VirtualBox) and attaches it to an existing
    Windows 10/11 test VM, or prints exact creation steps if no hypervisor is installed on the current host.
    Never modifies physical host disks.

.EXAMPLE
    .\scripts\Prepare-CopyOS-VmTest.ps1 -Hypervisor HyperV -VmName "Argus-CopyOS-Win11" -TargetDiskSizeGB 128 -Firmware UEFI
    .\scripts\Prepare-CopyOS-VmTest.ps1 -Hypervisor VirtualBox -VmName "Argus-CopyOS-Win10" -TargetDiskSizeGB 80 -Firmware BIOS
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Auto', 'HyperV', 'VirtualBox')]
    [string]$Hypervisor = 'Auto',

    [string]$VmName = 'Argus-CopyOS-Test',

    [ValidateRange(32, 2048)]
    [int]$TargetDiskSizeGB = 128,

    [ValidateSet('UEFI', 'BIOS')]
    [string]$Firmware = 'UEFI',

    [string]$DiskOutputDir = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($DiskOutputDir)) {
    $DiskOutputDir = Join-Path $scriptDir '..\vm-tests'
}

function Resolve-Hypervisor {
    param([string]$Requested)

    $hasHyperV = $null -ne (Get-Service vmms -ErrorAction SilentlyContinue)
    $vboxCmd = Get-Command VBoxManage.exe -ErrorAction SilentlyContinue
    $hasVBox = $null -ne $vboxCmd

    if ($Requested -eq 'HyperV') {
        if (-not $hasHyperV) { throw 'Службу Hyper-V (vmms) не встановлено на цьому ПК. Увімкніть Hyper-V або використайте VirtualBox.' }
        return 'HyperV'
    }

    if ($Requested -eq 'VirtualBox') {
        if (-not $hasVBox) { throw 'Утиліту VBoxManage.exe не знайдено у PATH. Встановіть Oracle VirtualBox і повторіть спробу.' }
        return 'VirtualBox'
    }

    if ($hasHyperV) { return 'HyperV' }
    if ($hasVBox) { return 'VirtualBox' }

    throw 'На поточному ПК не встановлено ні Hyper-V (vmms), ні VirtualBox (VBoxManage.exe). Встановіть один із гіпервізорів для живого тестування завантаження клонованої ОС у ВМ.'
}

$selected = Resolve-Hypervisor -Requested $Hypervisor
$resolvedOutDir = [System.IO.Path]::GetFullPath($DiskOutputDir)
if (-not (Test-Path $resolvedOutDir)) {
    New-Item -ItemType Directory -Path $resolvedOutDir -Force | Out-Null
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if ($selected -eq 'HyperV') {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'Для керування Hyper-V запустіть PowerShell від імені адміністратора.'
    }

    $vhdxPath = Join-Path $resolvedOutDir "$VmName-Target-$Firmware-$stamp.vhdx"
    $sizeBytes = [uint64]$TargetDiskSizeGB * 1GB

    if ($PSCmdlet.ShouldProcess($vhdxPath, "Створити чистий динамічний VHDX ($TargetDiskSizeGB ГБ) і підключити до ВМ '$VmName'")) {
        New-VHD -Path $vhdxPath -SizeBytes $sizeBytes -Dynamic | Out-Null

        $vm = Get-VM -Name $VmName -ErrorAction SilentlyContinue
        if ($null -eq $vm) {
            Write-Host "Створено чистий цільовий VHDX: $vhdxPath" -ForegroundColor Green
            Write-Host "ВМ '$VmName' ще не існує. Створіть ВМ Hyper-V Generation $(if ($Firmware -eq 'UEFI') { 2 } else { 1 }), встановіть Windows 10/11 і підключіть цей VHDX як другий диск." -ForegroundColor Yellow
            return
        }

        Add-VMHardDiskDrive -VMName $VmName -Path $vhdxPath
        Write-Host "Цільовий VHDX ($TargetDiskSizeGB ГБ) підключено до ВМ '$VmName': $vhdxPath" -ForegroundColor Green
        Write-Host "Кроки перевірки CopyOS у ВМ:" -ForegroundColor Cyan
        Write-Host "  1. Запустіть ВМ і відкрийте Argus.exe -> Міграція ОС."
        Write-Host "  2. Виконайте клонування на новий цільовий диск ($Firmware, режим BLOCK або DISM)."
        Write-Host "  3. Вимкніть ВМ, відключіть первинний диск Windows і завантажте ВМ лише з клонованого VHDX."
    }
}
elseif ($selected -eq 'VirtualBox') {
    $vdiPath = Join-Path $resolvedOutDir "$VmName-Target-$Firmware-$stamp.vdi"
    $sizeMB = $TargetDiskSizeGB * 1024

    if ($PSCmdlet.ShouldProcess($vdiPath, "Створити чистий VDI ($TargetDiskSizeGB ГБ) для VirtualBox ВМ '$VmName'")) {
        & VBoxManage.exe createmedium disk --filename $vdiPath --size $sizeMB --format VDI
        if ($LASTEXITCODE -ne 0) { throw "VBoxManage createmedium завершився з кодом $LASTEXITCODE." }

        Write-Host "Створено цільовий VDI ($TargetDiskSizeGB ГБ): $vdiPath" -ForegroundColor Green
        Write-Host "Щоб приєднати диск до SATA-контролера ВМ '$VmName', виконайте:" -ForegroundColor Cyan
        Write-Host "  VBoxManage storageattach `"$VmName`" --storagectl `"SATA`" --port 1 --device 0 --type hdd --medium `"$vdiPath`""
    }
}
