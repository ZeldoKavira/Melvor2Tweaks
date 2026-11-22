# Builds Melvor2Tweaks and packages one zip per platform into dist\, then (with -Publish) tags the commit,
# pushes and creates the GitHub release. Builds run locally because the mod compiles against the game's own
# assemblies, which can't be shipped in the repo or to CI.
#
#   powershell -File tools\release.ps1              # build + package only
#   powershell -File tools\release.ps1 -Publish     # ... and publish release v<Plugin.Version>
#
# Zip layout (unpacks over the folder that holds the game's data_GamesByMalcs.Melvor2_* folder):
#   mods/Melvor2Loader.dll, 0Harmony.dll, Melvor2Tweaks.dll, THIRD-PARTY-NOTICES.txt
#   mods/patcher/Melvor2Patcher[.exe|-arm64|-x64], mods/patcher/patch-version.txt

param([switch]$Publish, [string]$Notes = '')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }   # set DOTNET_EXE if dotnet isn't on PATH
$gh = if (Test-Path 'C:\Program Files\GitHub CLI\gh.exe') { 'C:\Program Files\GitHub CLI\gh.exe' } else { 'gh' }
Set-Location $repoRoot
[Environment]::CurrentDirectory = $repoRoot   # .NET calls below resolve relative paths against this, not the PowerShell location

$version = [regex]::Match((Get-Content 'src\Melvor2Tweaks\Plugin.cs' -Raw), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
$patchVersion = [regex]::Match((Get-Content 'src\Melvor2Patcher\Program.cs' -Raw), 'PatchVersion\s*=\s*(\d+)').Groups[1].Value
if (-not $version -or -not $patchVersion) { throw 'Could not read the version numbers.' }
Write-Host "Melvor2Tweaks $version (patch $patchVersion)"

$dist = Join-Path $repoRoot 'dist'
$build = Join-Path $dist 'build'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $build | Out-Null

function Invoke-Dotnet { & $dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed" } }

Invoke-Dotnet build src\Melvor2Loader -c Release -o "$build\loader" --nologo -v q
Invoke-Dotnet build src\Melvor2Tweaks -c Release -o "$build\tweaks" --nologo -v q
$patchers = @{ 'win-x64' = 'Melvor2Patcher.exe'; 'linux-x64' = 'Melvor2Patcher'; 'osx-arm64' = 'Melvor2Patcher-arm64'; 'osx-x64' = 'Melvor2Patcher-x64' }
foreach ($rid in $patchers.Keys) { Invoke-Dotnet publish src\Melvor2Patcher -c Release -r $rid -o "$build\patcher-$rid" --nologo -v q }

$platforms = @{ windows = @('win-x64'); linux = @('linux-x64'); macos = @('osx-arm64', 'osx-x64') }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

foreach ($platform in $platforms.Keys) {
    $zipPath = Join-Path $dist "Melvor2Tweaks-$version-$platform.zip"
    # Entries are added by hand so names use '/' (Compress-Archive on Windows PowerShell writes '\').
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        function Add($source, $entryName) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, $entryName, 'Optimal')
        }
        Add 'THIRD-PARTY-NOTICES.txt' 'mods/THIRD-PARTY-NOTICES.txt'
        Add "$build\loader\Melvor2Loader.dll" 'mods/Melvor2Loader.dll'
        Add "$build\loader\0Harmony.dll" 'mods/0Harmony.dll'
        Add "$build\tweaks\Melvor2Tweaks.dll" 'mods/Melvor2Tweaks.dll'
        $pv = Join-Path $build 'patch-version.txt'
        [IO.File]::WriteAllText($pv, "$patchVersion`n")
        Add $pv 'mods/patcher/patch-version.txt'
        foreach ($rid in $platforms[$platform]) {
            $binary = if ($rid -like 'win-*') { 'Melvor2Patcher.exe' } else { 'Melvor2Patcher' }
            Add (Join-Path "$build\patcher-$rid" $binary) "mods/patcher/$($patchers[$rid])"
        }
    }
    finally { $zip.Dispose() }
    Write-Host ("  {0}  {1:N1} MB" -f (Split-Path $zipPath -Leaf), ((Get-Item $zipPath).Length / 1MB))
}

if (-not $Publish) { Write-Host "Packaged into $dist (not published; add -Publish)."; return }

$tag = "v$version"
if (git status --porcelain) { throw 'Commit your changes first.' }
& git tag -a $tag -m "Melvor2Tweaks $version"
& git push origin HEAD
& git push origin $tag
$notesFile = Join-Path $build 'notes.md'
$body = if ($Notes) { $Notes } else { "Melvor2Tweaks $version" }
$body += "`n`n**Install / update:** see the [README](https://github.com/ZeldoKavira/Melvor2Tweaks#install). Existing installs update themselves."
[IO.File]::WriteAllText($notesFile, $body)
& $gh release create $tag (Get-ChildItem $dist -Filter '*.zip').FullName --title "Melvor2Tweaks $version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "Published $tag"
