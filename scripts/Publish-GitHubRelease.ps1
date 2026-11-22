<#
.SYNOPSIS
    Publishes the current ARGUS version from Directory.Build.props and ArgusSetup\ artifacts to GitHub Releases.

.EXAMPLE
    .\scripts\Publish-GitHubRelease.ps1
#>
[CmdletBinding()]
param(
    [string]$Repository = 'PIFAG0R0302/ARGUS-Updates',
    [string]$SourceRepository = 'PIFAG0R0302/ARGUS',
    [string]$SetupDir = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if ([string]::IsNullOrWhiteSpace($SetupDir)) {
    $SetupDir = Join-Path $root 'ArgusSetup'
}

$gitDir = Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd'
$ghDir = 'C:\Program Files\GitHub CLI'
$env:PATH = "$gitDir;$ghDir;$env:PATH"

[xml]$propsXml = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = $propsXml.Project.PropertyGroup.Version.Trim()
$tag = "v$version"

Set-Location $root

# Ensure git user config exists
$userName = git config user.name
if ([string]::IsNullOrWhiteSpace($userName)) {
    git config user.name "PIFAG0R0302"
    git config user.email "PIFAG0R0302@users.noreply.github.com"
}

$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh auth setup-git --hostname github.com 2>$null | Out-Null

git add -A 2>$null
$status = git status --porcelain
if (-not [string]::IsNullOrWhiteSpace($status)) {
    git commit -m "Release ARGUS $tag"
}

# Create or connect remote source repository (Private)
$remotes = git remote
if ($remotes -notcontains 'origin') {
    $repoExists = $false
    gh repo view $SourceRepository --json name 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $repoExists = $true }

    if ($repoExists) {
        git remote add origin "https://github.com/$SourceRepository.git"
        git branch -M main
        git push -u origin main
    } else {
        gh repo create $SourceRepository --private --description "ARGUS — нативний моніторинг, очищення, діагностика дисків і клонування Windows (CopyOS) на .NET 10 + Avalonia/Skia" --source=. --remote=origin --push
        if ($LASTEXITCODE -ne 0) { throw "gh repo create failed with exit code $LASTEXITCODE" }
    }
} else {
    git branch -M main
    git push -u origin main
}

# Ensure public releases-only repository exists (without source code)
$relRepoExists = $false
gh repo view $Repository --json name 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $relRepoExists = $true }
if (-not $relRepoExists) {
    gh repo create $Repository --public --description "ARGUS official releases & auto-update distribution (Windows x64)"
    if ($LASTEXITCODE -ne 0) { throw "gh repo create ($Repository) failed with exit code $LASTEXITCODE" }
}
$ErrorActionPreference = $prevEap

$setupExe    = Join-Path $SetupDir "Argus-Setup-$version-win-x64.exe"
$portableZip = Join-Path $SetupDir "Argus-Portable-$version-win-x64.zip"
$shaSums     = Join-Path $SetupDir 'SHA256SUMS.txt'
$cerFile     = Join-Path $SetupDir 'Argus-CodeSigning.cer'
$guideFile   = Join-Path $SetupDir 'USER-GUIDE.md'
$certCmdFile = Join-Path $SetupDir 'Install-Certificate.cmd'

foreach ($f in @($setupExe, $portableZip, $shaSums, $cerFile, $guideFile)) {
    if (-not (Test-Path $f)) { throw "Відсутній артефакт релізу: $f" }
}

$releaseAssets = [System.Collections.Generic.List[string]]::new()
$releaseAssets.AddRange([string[]]@($setupExe, $portableZip, $shaSums, $cerFile, $guideFile))
if (Test-Path $certCmdFile) {
    $releaseAssets.Add($certCmdFile)
}

$notes = @"
## ARGUS $tag (Windows 10 / 11 x64)

Нативна програма для моніторингу заліза в реальному часі, інвентаризації пристроїв, двокрокового очищення ОС за JSON-правилами, оптимізації накопичувачів (TRIM / дефрагментація, S.M.A.R.T.) та клонування живої Windows (`CopyOS`).

### Файли релізу
- **`Argus-Setup-$version-win-x64.exe`** — інсталятор Windows x64 (Inno Setup 6, автономний `.NET 10`, підписаний сертифікатом Authenticode).
- **`Argus-Portable-$version-win-x64.zip`** — портативний архів без встановлення.
- **`SHA256SUMS.txt`** — контрольні суми SHA-256 для автоматичної та ручної перевірки цілісності.
- **`Argus-CodeSigning.cer`** — публічний сертифікат підпису коду (`9D1B22482AC32CEA02F398527F9409FCA09EA78D`).
- **`USER-GUIDE.md`** — інструкція користувача.
"@

$notesFile = Join-Path $env:TEMP "argus_release_notes_$version.md"
[System.IO.File]::WriteAllText($notesFile, $notes, [System.Text.UTF8Encoding]::new($false))

# Check if release already exists
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$relExists = $false
gh release view $tag --repo $Repository 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $relExists = $true }

if ($relExists) {
    gh release upload $tag @releaseAssets --clobber --repo $Repository
    gh release edit $tag --title "ARGUS $tag" --notes-file $notesFile --repo $Repository
} else {
    gh release create $tag @releaseAssets --title "ARGUS $tag" --notes-file $notesFile --repo $Repository
}
$relExit = $LASTEXITCODE
$ErrorActionPreference = $prevEap
if ($relExit -ne 0) { throw "gh release failed with exit code $relExit" }

Remove-Item $notesFile -Force -ErrorAction SilentlyContinue
Write-Host "RELEASE_PUBLISHED: https://github.com/$Repository/releases/tag/$tag" -ForegroundColor Green
