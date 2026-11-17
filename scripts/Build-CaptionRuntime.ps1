$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Python = (Get-Command python -ErrorAction Stop).Source
$Work = Join-Path $Root "artifacts\cc-build"
$Dist = Join-Path $Root "artifacts\cc-runtime"
$Venv = Join-Path $Work "venv"
$Ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if (-not $Ffmpeg) { throw "FFmpeg is required on the build machine; install it and retry." }
New-Item -ItemType Directory -Force -Path $Work, $Dist | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $Venv "Scripts\python.exe"))) {
    & $Python -m venv $Venv
    if ($LASTEXITCODE -ne 0) { throw "Could not create the caption build environment." }
}
$VenvPython = Join-Path $Venv "Scripts\python.exe"
& $VenvPython -m pip install --disable-pip-version-check --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "Could not update pip in the isolated build environment." }
& $VenvPython -m pip install --disable-pip-version-check --index-url https://download.pytorch.org/whl/cpu torch==2.11.0
if ($LASTEXITCODE -ne 0) { throw "Could not install the CPU-only PyTorch runtime." }
& $VenvPython -m pip install --disable-pip-version-check openai-whisper==20250625 pyinstaller==6.19.0
if ($LASTEXITCODE -ne 0) { throw "Could not install Whisper build dependencies." }
& $VenvPython -c "import whisper; whisper.load_model('small', device='cpu')"
if ($LASTEXITCODE -ne 0) { throw "Could not download or validate the Whisper small model." }
$Model = Join-Path $env:USERPROFILE ".cache\whisper\small.pt"
if (-not (Test-Path -LiteralPath $Model)) { throw "Whisper did not create its small model cache: $Model" }
$env:OHMYPLAYER_WHISPER_MODEL = $Model
& $VenvPython -m PyInstaller --noconfirm --onedir --name ohmyplayer-cc --distpath $Dist --workpath $Work --specpath $Work --collect-all whisper --collect-all tiktoken --collect-all torch (Join-Path $Root "VideoPlayer.Wpf\Tools\transcribe_cc.py")
if ($LASTEXITCODE -ne 0) { throw "Could not package the offline caption executable." }
Write-Host "Caption engine is ready. Run the Windows installer or personal package build to include the model and FFmpeg."
