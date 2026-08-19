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
Write-Host "[1/6] Cleaning publish directory..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item "$publishDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $publishDir | Out-Null
}

# ========== Step 2: Publish ==========
Write-Host "[2/6] Publishing self-contained ($config, $runtime)..." -ForegroundColor Cyan
dotnet publish $projPath -c $config -r $runtime --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish FAILED (exit code $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

# ========== Step 3: Ensure config files ==========
Write-Host "[3/6] Verifying config files..." -ForegroundColor Cyan
$configFiles = @("appsettings.json", "plc-registers.json")
foreach ($cf in $configFiles) {
    $target = Join-Path $publishDir $cf
    if (-not (Test-Path $target)) {
        $source = Join-Path $slnRoot "src\IsolationLeakage.App\$cf"
        if (Test-Path $source) {
            Copy-Item $source $target -Force
            Write-Host "  Copied $cf to publish directory" -ForegroundColor Yellow
        } else {
            Write-Host "  WARNING: $cf not found!" -ForegroundColor Red
        }
    }
}

# ========== Step 4: Sanitize config for customer delivery ==========
Write-Host "[4/6] Sanitizing appsettings.json (removing real credentials)..." -ForegroundColor Cyan
$customerConfig = Join-Path $publishDir "appsettings.json"
if (Test-Path $customerConfig) {
    $json = Get-Content $customerConfig -Raw -Encoding UTF8 | ConvertFrom-Json

    # Clear connection strings - customer needs to fill in their own
    if ($json.ConnectionStrings) {
        $json.ConnectionStrings.DefaultConnection = "Server=YOUR_SERVER\SQLINSTANCE;Database=IsolationLeakageDb;User Id=YOUR_USER;Password=YOUR_PASSWORD;Connect Timeout=10;Trust Server Certificate=True;"
        $json.ConnectionStrings.SecondaryConnection = ""
    }

    # Disable failover by default (customer can enable if needed)
    if ($json.Failover) {
        $json.Failover.Enabled = $false
    }

    $newJson = $json | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($customerConfig, $newJson, [System.Text.UTF8Encoding]::new($true))
    Write-Host "  OK - Connection strings cleared" -ForegroundColor Green
}

# ========== Step 5: Copy customer files ==========
$customerFiles = @("客户须知.txt", "Setup-Client.ps1")
foreach ($cf in $customerFiles) {
    $src = Join-Path $PSScriptRoot $cf
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $publishDir $cf) -Force
    }
}
Write-Host "[5/6] Copied customer files (客户须知.txt, Setup-Client.ps1)" -ForegroundColor Green

# ========== Step 6: Zip ==========
$zipName = "IsolationLeakageApp_v${Version}_${runtime}_SelfContained.zip"
$zipPath = Join-Path $slnRoot $zipName

Write-Host "[6/6] Creating $zipName ..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Push-Location $publishDir
try {
    Compress-Archive -Path ".\*" -DestinationPath $zipPath -Force
} finally {
    Pop-Location
}

# ========== Summary ==========
$zipSize  = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
$pubSize  = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host "Done!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "  Zip:    $zipName"
Write-Host "  Size:   $zipSize MB"
Write-Host "  Files:  $fileCount (publish dir: $pubSize MB)"
Write-Host "  Path:   $zipPath"
Write-Host "========================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "Customer delivery config files:" -ForegroundColor Cyan
Write-Host "  appsettings.json    - DB connection, failover, logging (credentials cleared)" -ForegroundColor White
Write-Host "  plc-registers.json  - PLC communication settings" -ForegroundColor White
Write-Host ""
Write-Host "Customer needs to:" -ForegroundColor Cyan
Write-Host "  1. Edit appsettings.json - set DB server IP, user, password" -ForegroundColor White
Write-Host "  2. Edit plc-registers.json - set PLC IP, port, registers" -ForegroundColor White
Write-Host "  3. Run Setup-Client.ps1 for guided DB setup (optional)" -ForegroundColor White
Write-Host ""
