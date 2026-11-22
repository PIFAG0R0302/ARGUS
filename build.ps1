param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Run,
    [switch]$Test,
    [switch]$PublishPortable,
    [switch]$PublishRootExe,
    [switch]$RequireAdmin,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

function Resolve-DotNet10 {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
        'C:\Program Files\dotnet'
    )

    foreach ($dir in $candidates) {
        $exe = Join-Path $dir 'dotnet.exe'
        if (Test-Path $exe) {
            $sdks = & $exe --list-sdks 2>$null
            if ($sdks -match '^10\.') {
                return @{ Dir = $dir; Exe = $exe }
            }
        }
    }

    throw "Не знайдено .NET 10 SDK. Встановіть .NET 10 SDK і повторіть спробу."
}

$resolved = Resolve-DotNet10
$dotnet = $resolved.Exe
$env:DOTNET_ROOT = $resolved.Dir
$env:PATH = "$($resolved.Dir);$env:PATH"

$sln = Join-Path $PSScriptRoot 'Argus.sln'
$buildArgs = @('build', $sln, '-c', $Configuration)
if ($Strict) {
    $buildArgs += '/p:TreatWarningsAsErrors=true'
}

& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Test) {
    & $dotnet test --solution $sln --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($PublishPortable) {
    $win64Obj = Join-Path $PSScriptRoot 'src\Argus.App\obj\Release\net10.0-windows\win-x64'
    if (Test-Path $win64Obj) { Remove-Item -Recurse -Force $win64Obj }
    $outDir = Join-Path $PSScriptRoot 'publish\win-x64'
    $appProj = Join-Path $PSScriptRoot 'src\Argus.App\Argus.App.csproj'
    & $dotnet publish $appProj -c Release -r win-x64 --self-contained true -p:ArgusRequireAdmin=false -o $outDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Portable build ready: $outDir" -ForegroundColor Cyan
}

if ($PublishRootExe) {
    $win64Obj = Join-Path $PSScriptRoot 'src\Argus.App\obj\Release\net10.0-windows\win-x64'
    if (Test-Path $win64Obj) { Remove-Item -Recurse -Force $win64Obj }
    $stageDir = Join-Path $PSScriptRoot 'publish\single-file'
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    $requireAdminFlag = if ($RequireAdmin.IsPresent) { 'true' } else { 'false' }
    $appProj = Join-Path $PSScriptRoot 'src\Argus.App\Argus.App.csproj'
    & $dotnet publish $appProj -c Release -r win-x64 --self-contained true `
        -p:ArgusRequireAdmin=$requireAdminFlag `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $stageDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $srcExe = Join-Path $stageDir 'Argus.exe'
    $dstExe = Join-Path $PSScriptRoot 'Argus.exe'
    Copy-Item -Path $srcExe -Destination $dstExe -Force
    Remove-Item -Recurse -Force $stageDir
    Write-Host "Root executable ready: $dstExe (ArgusRequireAdmin=$requireAdminFlag)" -ForegroundColor Cyan
}

if ($Run) {
    $appProj = Join-Path $PSScriptRoot 'src\Argus.App\Argus.App.csproj'
    & $dotnet run --project $appProj -c $Configuration --no-build
}
