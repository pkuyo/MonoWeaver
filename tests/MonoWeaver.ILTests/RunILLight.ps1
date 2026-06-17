[CmdletBinding()]
param(
    [string]$ILTestFilter,
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [string]$TrxFileName
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $scriptDir "MonoWeaver.ILTests.csproj"
$resultsDir = Join-Path $scriptDir "TestResults"
$hasExplicitFilter = $PSBoundParameters.ContainsKey("ILTestFilter") -and -not [string]::IsNullOrWhiteSpace($ILTestFilter)
if ([string]::IsNullOrWhiteSpace($TrxFileName)) {
    $TrxFileName = if ($hasExplicitFilter) { "il-light-filtered-results.trx" } else { "il-light-results.trx" }
}
$trxPath = Join-Path $resultsDir $TrxFileName

New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

$previousFilter = $env:ILTEST_FILTER
$filterWasPresent = Test-Path Env:ILTEST_FILTER
$exitCode = 0

try {
    if ($hasExplicitFilter) {
        $env:ILTEST_FILTER = $ILTestFilter
    }
    else {
        Remove-Item Env:ILTEST_FILTER -ErrorAction SilentlyContinue
    }

    $testArgs = @(
        "test",
        $projectPath,
        "--configuration", $Configuration,
        "--filter", "FullyQualifiedName~LightMethodValidityMatchesExpectedResult",
        "--results-directory", $resultsDir,
        "--logger", "trx;LogFileName=$TrxFileName",
        "--logger", "console;verbosity=minimal",
        "--nologo"
    )

    if ($NoBuild) {
        $testArgs += "--no-build"
    }

    Write-Host "Running IL light verification tests..."
    if ($hasExplicitFilter) {
        Write-Host "ILTEST_FILTER=$env:ILTEST_FILTER"
    }
    else {
        Write-Host "ILTEST_FILTER cleared; running all light cases."
    }
    Write-Host "TRX: $trxPath"

    & dotnet @testArgs
    $exitCode = $LASTEXITCODE
}
finally {
    if ($filterWasPresent) {
        $env:ILTEST_FILTER = $previousFilter
    }
    else {
        Remove-Item Env:ILTEST_FILTER -ErrorAction SilentlyContinue
    }
}

exit $exitCode
