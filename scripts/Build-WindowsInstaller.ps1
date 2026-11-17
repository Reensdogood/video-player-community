param(
    [string]$Version = "0.1.13"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "VideoPlayer.Wpf\VideoPlayer.Wpf.csproj"
$PublishDir = Join-Path $Root "artifacts\windows-installer\publish"
$ReleaseDir = Join-Path $Root "artifacts\windows-installer\$Version"
$ToolDir = Join-Path $Root "artifacts\tools"
$VpkExe = Join-Path $ToolDir "vpk.exe"
$VpkVersion = "1.2.161"

if (-not $Version -match '^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$') {
    throw "Version must be semantic versioning (for example 0.1.0)."
}

New-Item -ItemType Directory -Force -Path $PublishDir, $ReleaseDir, $ToolDir | Out-Null
$ReleaseRoot = [System.IO.Path]::GetFullPath((Join-Path $Root "artifacts\windows-installer"))
$ResolvedReleaseDir = [System.IO.Path]::GetFullPath($ReleaseDir)
if (-not $ResolvedReleaseDir.StartsWith($ReleaseRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an output path outside artifacts\windows-installer."
}
Get-ChildItem -LiteralPath $ResolvedReleaseDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
if (-not (Test-Path -LiteralPath $VpkExe)) {
    dotnet tool install --tool-path $ToolDir vpk --version $VpkVersion
    if ($LASTEXITCODE -ne 0) { throw "Could not install the pinned Velopack CLI." }
}

dotnet publish $Project -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Windows publish failed." }
& (Join-Path $PSScriptRoot "Copy-CaptionRuntime.ps1") -Destination $PublishDir

$MsiVersion = "$Version.0"
& $VpkExe pack `
    --packId "funnet.ohmyplayer" `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe "ohmyplayer.exe" `
    --packTitle "다봐 (ohmyplayer)" `
    --packAuthors "beondiueyow" `
    --icon (Join-Path $Root "VideoPlayer.Wpf\Assets\ohmyplayer.ico") `
    --runtime "win-x64" `
    --channel "win" `
    --outputDir $ReleaseDir `
    --msi `
    --msiVersion $MsiVersion `
    --instLocation "PerUser"
if ($LASTEXITCODE -ne 0) { throw "Windows installer packaging failed." }

# Keep the existing package ID so current installations can still receive updates;
# use the requested brand for files users see and download.
Get-Item -LiteralPath (Join-Path $ReleaseDir "funnet.ohmyplayer-win-Setup.exe") | Rename-Item -NewName "beondiueyow-ohmyplayer-win-Setup.exe"
Get-Item -LiteralPath (Join-Path $ReleaseDir "funnet.ohmyplayer-win.msi") | Rename-Item -NewName "beondiueyow-ohmyplayer-win.msi"
Get-Item -LiteralPath (Join-Path $ReleaseDir "funnet.ohmyplayer-win-Portable.zip") | Rename-Item -NewName "beondiueyow-ohmyplayer-win-Portable.zip"
$OldPackageName = "funnet.ohmyplayer-$Version-full.nupkg"
$NewPackageName = "beondiueyow-ohmyplayer-$Version-full.nupkg"
Get-Item -LiteralPath (Join-Path $ReleaseDir $OldPackageName) | Rename-Item -NewName $NewPackageName

$ReleaseIndexPath = Join-Path $ReleaseDir "RELEASES"
$ReleaseIndex = (Get-Content -LiteralPath $ReleaseIndexPath -Raw).Replace($OldPackageName, $NewPackageName)
Set-Content -LiteralPath $ReleaseIndexPath -Value $ReleaseIndex -NoNewline -Encoding utf8

$ReleaseManifestPath = Join-Path $ReleaseDir "releases.win.json"
$ReleaseManifest = Get-Content -LiteralPath $ReleaseManifestPath -Raw | ConvertFrom-Json
foreach ($asset in $ReleaseManifest.Assets) { if ($asset.FileName -eq $OldPackageName) { $asset.FileName = $NewPackageName } }
Set-Content -LiteralPath $ReleaseManifestPath -Value (ConvertTo-Json -InputObject $ReleaseManifest -Depth 8) -Encoding utf8

$AssetManifestPath = Join-Path $ReleaseDir "assets.win.json"
$AssetManifest = Get-Content -LiteralPath $AssetManifestPath -Raw | ConvertFrom-Json
$VisibleNames = @{
    "funnet.ohmyplayer-win.msi" = "beondiueyow-ohmyplayer-win.msi"
    "funnet.ohmyplayer-win-Setup.exe" = "beondiueyow-ohmyplayer-win-Setup.exe"
    "funnet.ohmyplayer-win-Portable.zip" = "beondiueyow-ohmyplayer-win-Portable.zip"
    $OldPackageName = $NewPackageName
}
foreach ($asset in $AssetManifest) { if ($VisibleNames.ContainsKey($asset.RelativeFileName)) { $asset.RelativeFileName = $VisibleNames[$asset.RelativeFileName] } }
Set-Content -LiteralPath $AssetManifestPath -Value (ConvertTo-Json -InputObject @($AssetManifest) -Depth 8) -Encoding utf8

$ChecksumLines = Get-ChildItem -LiteralPath $ReleaseDir -File |
    Where-Object { $_.Name -match '(-Setup\.exe|\.msi|Portable\.zip|-full\.nupkg|releases\.win\.json)$' } |
    ForEach-Object {
        $Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$Hash  $($_.Name)"
    }
Set-Content -LiteralPath (Join-Path $ReleaseDir "SHA256SUMS.txt") -Value $ChecksumLines -Encoding utf8

Write-Host "Installer artifacts are ready in: $ReleaseDir"
