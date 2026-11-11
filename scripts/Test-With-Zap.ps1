[CmdletBinding()]
param(
    [string]$Configuration = 'Release-review',
    [string]$Filter = '',
    [switch]$InstalledZapOnly,
    [string]$ResultsDirectory = ''
)

$ErrorActionPreference = 'Stop'
if ($InstalledZapOnly) {
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { throw 'Use either -InstalledZapOnly or -Filter.' }
    $Filter = 'FullyQualifiedName~InstalledZap|FullyQualifiedName~StoppingInstalledPlaywright'
}
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRuntimeFile = Join-Path $taskRoot '.cache\zap\runtime.json'
if (-not (Test-Path -LiteralPath $taskRuntimeFile -PathType Leaf)) {
    throw "ZAP test runtime is not configured: $taskRuntimeFile"
}
$taskRuntime = Get-Content -LiteralPath $taskRuntimeFile -Raw | ConvertFrom-Json
$taskJar = [string]$taskRuntime.ZapJarPath
if (-not [IO.Path]::IsPathRooted($taskJar)) { $taskJar = Join-Path $taskRoot $taskJar }
$taskJava = [string]$taskRuntime.JavaPath
foreach ($taskExecutable in @($taskJar, $taskJava)) {
    if (-not (Test-Path -LiteralPath $taskExecutable -PathType Leaf)) {
        throw "Required ZAP test file is missing: $taskExecutable"
    }
}
$taskDotnet = Join-Path $taskRoot '.cache\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet -PathType Leaf)) {
    $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $taskRoot ('temp\zap-tests\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$taskScratch = Join-Path $ResultsDirectory 'scratch'
$taskArtifacts = Join-Path $ResultsDirectory 'artifacts'
New-Item -ItemType Directory -Force -Path $taskScratch, $taskArtifacts | Out-Null

# Process-only settings; no user/system environment or engagement is changed.
$taskNames = @('SCOPEPILOT_TEST_ZAP_JAR', 'SCOPEPILOT_TEST_JAVA', 'SCOPEPILOT_TEST_ZAP_ARTIFACTS', 'TEMP', 'TMP')
$taskPrevious = @{}
foreach ($taskName in $taskNames) { $taskPrevious[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process') }
try {
    $env:SCOPEPILOT_TEST_ZAP_JAR = [IO.Path]::GetFullPath($taskJar)
    $env:SCOPEPILOT_TEST_JAVA = [IO.Path]::GetFullPath($taskJava)
    $env:SCOPEPILOT_TEST_ZAP_ARTIFACTS = $taskArtifacts
    $env:TEMP = $taskScratch
    $env:TMP = $taskScratch
    $taskArguments = @('test', (Join-Path $taskRoot 'Tests\ScopePilot.Tests.csproj'), '-c', $Configuration,
        '--no-restore', '--results-directory', $ResultsDirectory, '--logger', 'trx;LogFileName=results.trx')
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $taskArguments += @('--filter', $Filter) }
    $taskArguments += @('--', 'xUnit.ParallelizeTestCollections=false')
    Write-Host "ZAP: $taskJar"
    Write-Host "Java: $taskJava"
    # Windows PowerShell wraps native stderr as ErrorRecord. A test failure must
    # finish writing its TRX and logs before we propagate the native exit code.
    $ErrorActionPreference = 'Continue'
    & $taskDotnet @taskArguments 2>&1 | ForEach-Object { $_.ToString() } | Tee-Object -FilePath (Join-Path $ResultsDirectory 'test-console.log')
    $taskExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Write-Host "Test results: $ResultsDirectory"
}
finally {
    foreach ($taskName in $taskNames) { [Environment]::SetEnvironmentVariable($taskName, $taskPrevious[$taskName], 'Process') }
}
exit $taskExitCode
