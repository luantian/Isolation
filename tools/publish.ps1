param(
    [string]$Version
)

$ErrorActionPreference = "Stop"

# ========== Config ==========
$slnRoot   = Split-Path -Parent $PSScriptRoot
$projPath  = Join-Path $slnRoot "src\IsolationLeakage.App\IsolationLeakage.App.csproj"
$publishDir = Join-Path $slnRoot "publish"
$runtime   = "win-x64"
$config    = "Release"

# Default version: yyyyMMdd
if (-not $Version) {
    $Version = Get-Date -Format "yyyyMMdd"
}

# ========== Step 1: Clean ==========
Write-Host "[1/4] Cleaning publish directory..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item "$publishDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $publishDir | Out-Null
}

# ========== Step 2: Publish ==========
Write-Host "[2/4] Publishing self-contained ($config, $runtime)..." -ForegroundColor Cyan
dotnet publish $projPath -c $config -r $runtime --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish FAILED (exit code $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

# ========== Step 3: Zip ==========
$zipName = "IsolationLeakageApp_v${Version}_${runtime}_SelfContained.zip"
$zipPath = Join-Path $slnRoot $zipName

Write-Host "[3/4] Creating $zipName ..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Push-Location $publishDir
try {
    Compress-Archive -Path ".\*" -DestinationPath $zipPath -Force
} finally {
    Pop-Location
}

# ========== Step 4: Summary ==========
$zipSize  = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
$pubSize  = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host "[4/4] Done!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "  Zip:    $zipName"
Write-Host "  Size:   $zipSize MB"
Write-Host "  Files:  $fileCount (publish dir: $pubSize MB)"
Write-Host "  Path:   $zipPath"
Write-Host "========================================" -ForegroundColor Yellow
