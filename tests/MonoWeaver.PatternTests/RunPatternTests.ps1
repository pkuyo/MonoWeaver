[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$TestFilter,
    [switch]$NoBuild,
    [string]$TrxFileName = "pattern-tests.trx"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $scriptDir "MonoWeaver.PatternTests.csproj"
$resultsDir = Join-Path $scriptDir "TestResults"
$trxPath = Join-Path $resultsDir $TrxFileName

New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

$testArgs = @(
    "test",
    $projectPath,
    "--configuration", $Configuration,
    "--results-directory", $resultsDir,
    "--logger", "trx;LogFileName=$TrxFileName",
    "--logger", "console;verbosity=minimal",
    "--nologo"
)

if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    $testArgs += @("--filter", $TestFilter)
}
if ($NoBuild) {
    $testArgs += "--no-build"
}

Write-Host "Running Pattern tests..."
Write-Host "TRX: $trxPath"
if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    Write-Host "Filter: $TestFilter"
}

& dotnet @testArgs
exit $LASTEXITCODE
