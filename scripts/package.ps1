param(
    [Parameter(Mandatory = $false)]
    [string]$JellyfinPluginsDir,

    [Parameter(Mandatory = $false)]
    [string]$OutDir = "artifacts",

    [Parameter(Mandatory = $false)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "Jellyfin.Plugin.JellySpot\Jellyfin.Plugin.JellySpot.csproj"
$outBuild = Join-Path $root "Jellyfin.Plugin.JellySpot\bin\Release\net9.0"
$stage = Join-Path $root ".package-stage"
$meta = Join-Path $root "Jellyfin.Plugin.JellySpot\meta.json"

if (-not $Version) {
    $Version = (Get-Content $meta | ConvertFrom-Json).version
}

dotnet build $project -c Release `
    "-p:Version=$Version" `
    "-p:AssemblyVersion=$Version" `
    "-p:FileVersion=$Version"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage, (Join-Path $root $OutDir) | Out-Null

$dlls = @(
    "Jellyfin.Plugin.JellySpot.dll",
    "YoutubeExplode.dll",
    "YoutubeExplode.Converter.dll",
    "TagLibSharp.dll",
    "FuzzySharp.dll",
    "AngleSharp.dll",
    "CliWrap.dll"
)
foreach ($dll in $dlls) {
    $src = Join-Path $outBuild $dll
    if (Test-Path $src) { Copy-Item $src $stage -Force }
}
Copy-Item $meta (Join-Path $stage "meta.json") -Force

$thumbCandidates = @(
    (Join-Path $root "Jellyfin.Plugin.JellySpot\thumb.png"),
    (Join-Path $root "assets\thumb.png")
)
foreach ($thumb in $thumbCandidates) {
    if (Test-Path $thumb) {
        Copy-Item $thumb (Join-Path $stage "thumb.png") -Force
        break
    }
}

$metaObj = Get-Content (Join-Path $stage "meta.json") | ConvertFrom-Json
$metaObj.version = $Version
$metaObj.imagePath = "thumb.png"
$metaObj | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $stage "meta.json")

$zipPath = Join-Path $root "$OutDir\JellySpot_$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force

$hash = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
Write-Host "Created $zipPath"
Write-Host "MD5 $hash"

if ($JellyfinPluginsDir) {
    $dest = Join-Path $JellyfinPluginsDir "JellySpot"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Expand-Archive -Path $zipPath -DestinationPath $dest -Force
    Write-Host "Installed to $dest"
}
