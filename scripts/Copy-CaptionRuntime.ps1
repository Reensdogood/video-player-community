param([Parameter(Mandatory = $true)][string]$Destination)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$RuntimeSource = Join-Path $Root "artifacts\cc-runtime\ohmyplayer-cc"
$Model = if ($env:OHMYPLAYER_WHISPER_MODEL) { $env:OHMYPLAYER_WHISPER_MODEL } else { Join-Path $env:USERPROFILE ".cache\whisper\small.pt" }
$FfmpegCommand = Get-Command ffmpeg -ErrorAction Stop
$FfmpegItem = Get-Item -LiteralPath $FfmpegCommand.Source -Force
$Ffmpeg = if ($FfmpegItem.LinkType -eq "SymbolicLink") { $FfmpegItem.Target } else { $FfmpegItem.FullName }

if (-not (Test-Path -LiteralPath (Join-Path $RuntimeSource "ohmyplayer-cc.exe"))) {
    throw "The offline caption runtime is missing. Build it into artifacts\cc-runtime\ohmyplayer-cc first."
}
if (-not (Test-Path -LiteralPath $Model)) { throw "Whisper small model was not found: $Model" }
if (-not (Test-Path -LiteralPath $Ffmpeg)) { throw "FFmpeg was not found: $Ffmpeg" }

$Target = Join-Path $Destination "Tools\cc-runtime"
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Get-ChildItem -LiteralPath $RuntimeSource -Force | Copy-Item -Destination $Target -Recurse -Force
Copy-Item -LiteralPath $Model -Destination (Join-Path $Target "small.pt") -Force
Copy-Item -LiteralPath $Ffmpeg -Destination (Join-Path $Target "ffmpeg.exe") -Force
Write-Host "Bundled offline caption runtime, Whisper model, and FFmpeg into $Target"
