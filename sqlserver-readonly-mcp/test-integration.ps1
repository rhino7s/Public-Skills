[CmdletBinding()]
param(
    [string]$TargetDatabase,

    [string]$TargetObject,

    [string]$SearchDatabase,

    [string]$Config = (Join-Path $PSScriptRoot 'appsettings.local.json'),

    [string]$TestSettings = (Join-Path $PSScriptRoot 'integration.local.json'),

    [switch]$IncludeJobs,

    [switch]$RequireReference
)

$ErrorActionPreference = 'Stop'

$localSettings = if (Test-Path -LiteralPath $TestSettings -PathType Leaf) {
    Get-Content -LiteralPath $TestSettings -Raw | ConvertFrom-Json
}
else {
    $null
}
$referenceSettings = $localSettings.findObjectReferences
$detailsSettings = $localSettings.getObjectDetails

if ([string]::IsNullOrWhiteSpace($TargetDatabase)) {
    $TargetDatabase = [string]$referenceSettings.targetDatabase
}

if ([string]::IsNullOrWhiteSpace($TargetObject)) {
    $TargetObject = [string]$referenceSettings.targetObject
}

if ([string]::IsNullOrWhiteSpace($SearchDatabase)) {
    $SearchDatabase = [string]$referenceSettings.searchDatabase
}

if ([string]::IsNullOrWhiteSpace($SearchDatabase)) {
    $SearchDatabase = $TargetDatabase
}

$hasReferenceCase = -not [string]::IsNullOrWhiteSpace($TargetDatabase) -and
    -not [string]::IsNullOrWhiteSpace($TargetObject)
$hasDetailsCase = -not [string]::IsNullOrWhiteSpace([string]$detailsSettings.database) -and
    -not [string]::IsNullOrWhiteSpace([string]$detailsSettings.objectName) -and
    -not [string]::IsNullOrWhiteSpace([string]$detailsSettings.definitionSearch)
if (-not $hasReferenceCase -and -not $hasDetailsCase) {
    throw 'integration.local.json 至少需要配置 findObjectReferences 或 getObjectDetails 一个独立测试案例。'
}

$configPath = (Resolve-Path -LiteralPath $Config -ErrorAction Stop).Path
$testProject = Join-Path $PSScriptRoot 'tests/SqlServerReadonlyMcp.Tests/SqlServerReadonlyMcp.Tests.csproj'
$actualIncludeJobs = $IncludeJobs.IsPresent -or $referenceSettings.includeJobs -eq $true
$actualRequireReference = $RequireReference.IsPresent -or $referenceSettings.requireReference -eq $true
$variables = [ordered]@{
    SQLSERVER_MCP_INTEGRATION_CONFIG = $configPath
    SQLSERVER_MCP_INTEGRATION_TARGET_DATABASE = $TargetDatabase
    SQLSERVER_MCP_INTEGRATION_TARGET_OBJECT = $TargetObject
    SQLSERVER_MCP_INTEGRATION_SEARCH_DATABASE = $SearchDatabase
    SQLSERVER_MCP_INTEGRATION_INCLUDE_JOBS = $actualIncludeJobs.ToString()
    SQLSERVER_MCP_INTEGRATION_REQUIRE_REFERENCE = $actualRequireReference.ToString()
    SQLSERVER_MCP_INTEGRATION_DETAILS_DATABASE = [string]$detailsSettings.database
    SQLSERVER_MCP_INTEGRATION_DETAILS_OBJECT = [string]$detailsSettings.objectName
    SQLSERVER_MCP_INTEGRATION_DETAILS_SEARCH = [string]$detailsSettings.definitionSearch
}
$previousValues = @{}

try {
    foreach ($entry in $variables.GetEnumerator()) {
        $previousValues[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    & dotnet test $testProject `
        --no-restore `
        --filter 'FullyQualifiedName~SqlServerIntegrationTests'
    if ($LASTEXITCODE -ne 0) {
        throw "真实 SQL Server 集成测试失败，退出码：$LASTEXITCODE"
    }
}
finally {
    foreach ($entry in $previousValues.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}
