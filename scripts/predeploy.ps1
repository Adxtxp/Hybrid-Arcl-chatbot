# Pre-deploy gate: build, unit tests, then the golden Q&A regression set against a live API.
# Exits non-zero if any step fails, so a deploy script/pipeline can refuse to ship on it.
#
#   powershell -File scripts\predeploy.ps1
#   powershell -File scripts\predeploy.ps1 -MinRouteAccuracy 0.95 -MinRecall 0.8
#   powershell -File scripts\predeploy.ps1 -ApiBase https://staging.example.org -SkipServerStart
#
# Needs the same environment the API itself needs (ARCL_DB_CONNECTION, GEMINI_API_KEY,
# ARCL_ADMIN_API_KEY — or user-secrets), because the golden set runs real questions end to end:
# real retrieval, real Gemini calls (~14 questions, a few cents). That's also why it isn't in
# GitHub Actions CI (.github/workflows/ci.yml runs build + unit tests only): CI has neither the
# database nor the key. Run this on the machine you deploy from, or against staging.

param(
    [string] $ApiBase = "http://localhost:5000",
    [double] $MinRouteAccuracy = 0.9,
    [double] $MinRecall = 0.7,
    [switch] $SkipServerStart
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

function Step($name, [scriptblock] $action) {
    Write-Host ""
    Write-Host "==> $name" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)." }
}

$server = $null
try {
    Step "Build" {
        dotnet build (Join-Path $root "api\ArclChatbot.Api.csproj") -c Release --nologo -v quiet
        if ($LASTEXITCODE -eq 0) { dotnet build (Join-Path $root "eval\EvalRunner.csproj") -c Release --nologo -v quiet }
    }

    Step "Unit tests" {
        dotnet test (Join-Path $root "tests\Arcl.Api.Tests\Arcl.Api.Tests.csproj") -c Release --nologo -v quiet
    }

    Step "Copy frontend into api/wwwroot" {
        Copy-Item (Join-Path $root "frontend\*") (Join-Path $root "api\wwwroot\") -Include *.html, *.js, *.css -Force
        $global:LASTEXITCODE = 0
    }

    if (-not $SkipServerStart) {
        Write-Host ""
        Write-Host "==> Starting API on $ApiBase" -ForegroundColor Cyan
        $server = Start-Process dotnet -PassThru -NoNewWindow `
            -ArgumentList @("run", "--no-build", "-c", "Release", "--project", (Join-Path $root "api\ArclChatbot.Api.csproj"), "--urls", $ApiBase) `
            -RedirectStandardOutput (Join-Path $env:TEMP "arcl-predeploy-api.log") `
            -RedirectStandardError (Join-Path $env:TEMP "arcl-predeploy-api.err.log")

        $deadline = (Get-Date).AddSeconds(90)
        $up = $false
        while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
            try {
                if ((Invoke-WebRequest "$ApiBase/api/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $up = $true; break }
            } catch { Start-Sleep -Milliseconds 750 }
        }
        if (-not $up) { throw "API didn't become healthy within 90s — see $env:TEMP\arcl-predeploy-api*.log" }
    }

    Step "Golden Q&A regression set (route accuracy >= $MinRouteAccuracy, recall >= $MinRecall)" {
        dotnet run --no-build -c Release --project (Join-Path $root "eval\EvalRunner.csproj") -- `
            --api-base $ApiBase --min-route-accuracy $MinRouteAccuracy --min-recall $MinRecall
    }

    Write-Host ""
    Write-Host "PRE-DEPLOY GATE PASSED" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ""
    Write-Host "PRE-DEPLOY GATE FAILED: $_" -ForegroundColor Red
    exit 1
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
