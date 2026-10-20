#Requires -Version 5.1
<#
  Builds the RX 6600 fork into a fresh portable folder. Reads OBS without modifying it.
  Run Kairos.exe from the resulting folder; no installer or global SDK changes are needed.
#>
param(
    [string]$DotNet = 'dotnet',
    [string]$Out = 'dist\KairosRX',
    [string]$Obs = 'C:\Program Files\obs-studio'
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot))

function Full-Path([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A folder path cannot be empty.' }
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $repo $Path }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Inside([string]$Path, [string]$Root) {
    return $Path.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

$outputPath = Full-Path $Out
$obsPath = Full-Path $Obs
$distPath = Full-Path (Join-Path $repo 'dist')
$installPath = Full-Path (Join-Path $env:LOCALAPPDATA 'Kairos')

# A repo-local build belongs only in dist; the source tree and installed programs are never output folders.
if ((Inside $outputPath $repo) -and -not (Inside $outputPath $distPath)) {
    throw 'Choose an output folder inside dist or outside the source repository.'
}
if ((Inside $outputPath $obsPath) -or (Inside $outputPath $installPath)) {
    throw 'The output folder cannot be inside OBS or the installed Kairos.'
}
if ($outputPath -eq [IO.Path]::GetPathRoot($outputPath).TrimEnd('\', '/')) {
    throw 'A drive root cannot be the output folder.'
}

# Do not follow a junction or symlink into a source/install folder through an otherwise safe-looking output path.
$ancestor = $outputPath
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The output path must not pass through a junction or symlink: $ancestor"
        }
    }
    $ancestor = Split-Path -Path $ancestor -Parent
}
if (Test-Path -LiteralPath $outputPath) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) { throw 'The output path is a file.' }
    if (Get-ChildItem -LiteralPath $outputPath -Force | Select-Object -First 1) {
        throw "The output folder must be empty. Choose a fresh folder: $outputPath"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $obsPath 'bin\64bit\obs.dll') -PathType Leaf)) {
    throw "OBS runtime not found at $obsPath"
}

if ($DotNet.Contains('\') -or $DotNet.Contains('/')) { $DotNet = Full-Path $DotNet }
$dotnetCommand = Get-Command -Name $DotNet -CommandType Application -ErrorAction Stop
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& (Join-Path $PSScriptRoot 'stage-runtime.ps1') -Dest $outputPath -Obs $obsPath
if ($LASTEXITCODE -ne 0) { throw 'Staging the OBS runtime failed.' }
$obsVersion = (Get-Content -LiteralPath (Join-Path $outputPath 'libobs-version.txt') -Raw).Trim()
if ($obsVersion -ne '32.2.2') { throw "This preview was validated with OBS 32.2.2, not $obsVersion." }

& $dotnetCommand.Source publish (Join-Path $repo 'src\Kairos\Kairos.csproj') -c Release -r win-x64 `
    --self-contained true -p:Platform=x64 -p:Version=1.0.1-rx.1 -o $outputPath --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Publishing Kairos RX failed.' }

foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $outputPath
}
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination $outputPath -Recurse
Write-Output "Kairos RX is ready: $(Join-Path $outputPath 'Kairos.exe')"
