# Melvor2Tweaks installer for Windows.
#
#   Install or update:  irm https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.ps1 | iex
#   Uninstall:          & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.ps1))) -Uninstall
#   Game elsewhere:     ... -GameDir "D:\Games\Melvor Idle 2"
#
# Finds Melvor Idle 2 in your Steam libraries, downloads the latest release from GitHub, copies it into the
# game folder and patches the game once. After that the mod keeps itself up to date and re-applies its
# hook by itself if a game update undoes it. Your settings live in %APPDATA%\Games By Malcs\Melvor Idle 2\Melvor2Tweaks
# and are kept on uninstall.

param(
    [string]$GameDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$Repo = 'ZeldoKavira/Melvor2Tweaks'
$AppId = '3218350'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Say($text) { Write-Host "Melvor2Tweaks: $text" }
function Fail($text) { Write-Host "Melvor2Tweaks: $text" -ForegroundColor Red; throw $text }

function Find-SteamLibraries {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') { if ($item.$name) { $roots += ($item.$name -replace '/', '\') } }
        } catch { }
    }
    $roots += "${env:ProgramFiles(x86)}\Steam"
    $libs = @()
    foreach ($root in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path $root)) { continue }
        $libs += $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
        }
    }
    $libs | Select-Object -Unique
}

function Find-Game {
    foreach ($lib in Find-SteamLibraries) {
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path $manifest)) { continue }
        $m = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s+"([^"]+)"')
        $dir = Join-Path $lib ("steamapps\common\" + $(if ($m.Success) { $m.Groups[1].Value } else { 'Melvor Idle 2' }))
        if (Test-Path $dir) { return $dir }
    }
    $null
}

# The folder holding the game's data_GamesByMalcs.Melvor2_* folder: mods\ and override.cfg go here.
function Find-Root($dir) {
    $data = Get-ChildItem -Path $dir -Directory -Filter 'data_GamesByMalcs.Melvor2_*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'GamesByMalcs.Melvor2.dll') } | Select-Object -First 1
    if ($data) { $data.Parent.FullName } else { $null }
}

# Earlier test builds registered a startup script in override.cfg; remove that line if present.
function Remove-OurOverride($root) {
    $cfg = Join-Path $root 'override.cfg'
    if (-not (Test-Path $cfg)) { return }
    $lines = Get-Content $cfg | Where-Object { $_ -notmatch '^\s*Melvor2TweaksBoot=' }
    $meaningful = $lines | Where-Object { $_.Trim() -ne '' -and $_.Trim() -ne '[autoload]' }
    if (-not $meaningful) { Remove-Item $cfg -Force } else { Set-Content -Path $cfg -Value $lines -Encoding UTF8 }
}

# ---------------------------------------------------------------------------------------------------
if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir -or -not (Test-Path $GameDir)) { Fail "couldn't find Melvor Idle 2 in your Steam libraries. Run again with -GameDir `"<game folder>`"." }
$root = Find-Root $GameDir
if (-not $root) { Fail "no data_GamesByMalcs.Melvor2_* folder in $GameDir - is that the Melvor Idle 2 folder?" }
Say "game found at $root"

if (Get-Process -Name 'Melvor2' -ErrorAction SilentlyContinue) { Fail 'close Melvor Idle 2 first, then run this again.' }

$mods = Join-Path $root 'mods'
$patcher = Join-Path $mods 'patcher\Melvor2Patcher.exe'

if ($Uninstall) {
    if (Test-Path $patcher) { & $patcher $root --restore | ForEach-Object { Say $_ } }
    foreach ($f in 'Melvor2Loader.dll', 'Melvor2Loader.pdb', 'Melvor2Loader.log', '0Harmony.dll', 'Melvor2Tweaks.dll', 'Melvor2Tweaks.pdb',
                   'melvor2_boot.gd', 'melvor2_boot.log', 'patch.stamp', 'THIRD-PARTY-NOTICES.txt') {
        Remove-Item (Join-Path $mods $f) -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem $mods -Filter '*.old*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $mods 'patcher') -Recurse -Force -ErrorAction SilentlyContinue
    if ((Test-Path $mods) -and -not (Get-ChildItem $mods)) { Remove-Item $mods -Force }
    Remove-OurOverride $root
    Say 'uninstalled. (Your settings in %APPDATA%\Games By Malcs\Melvor Idle 2\Melvor2Tweaks were kept.)'
    return
}

$platform = 'windows'
Say 'looking up the latest release...'
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'Melvor2Tweaks-Installer'; 'Accept' = 'application/vnd.github+json' }
$asset = $release.assets | Where-Object { $_.name -like "*-$platform.zip" } | Select-Object -First 1
if (-not $asset) { Fail "release $($release.tag_name) has no $platform download." }

$temp = Join-Path ([IO.Path]::GetTempPath()) ("Melvor2Tweaks-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zip = Join-Path $temp $asset.name
    Say "downloading $($asset.name) ($($release.tag_name))..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'Melvor2Tweaks-Installer' }
    $out = Join-Path $temp 'files'
    Expand-Archive -Path $zip -DestinationPath $out -Force
    Get-ChildItem $out -Recurse -File | Unblock-File

    New-Item -ItemType Directory -Path $mods -Force | Out-Null
    Get-ChildItem $out -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($out.Length + 1)
        $dest = Join-Path $root $rel
        New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
        Copy-Item $_.FullName $dest -Force
    }
    Remove-OurOverride $root
    Remove-Item (Join-Path $mods 'melvor2_boot.gd') -Force -ErrorAction SilentlyContinue

    Say 'patching the game...'
    & $patcher $root | ForEach-Object { Say $_ }
    if ($LASTEXITCODE -ne 0) { Fail "the patcher failed (exit $LASTEXITCODE)." }
    Say "installed $($release.tag_name). Start the game from Steam; the mod's buttons are in the top bar."
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
