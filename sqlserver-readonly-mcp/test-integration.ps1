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
$platform = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    'win'
}
elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux)) {
    'linux'
}
elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
    'osx'
}
else {
    throw '集成测试不支持当前操作系统。'
}
$runtimeIdentifier = "$platform-$([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())"
if ($runtimeIdentifier -notin @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')) {
    throw "集成测试不支持当前平台：$runtimeIdentifier"
}
$publishDirectory = Join-Path $PSScriptRoot "publish/$runtimeIdentifier"
$executableName = if ($platform -eq 'win') { 'sqlserver-readonly-mcp.exe' } else { 'sqlserver-readonly-mcp' }
$executablePath = Join-Path $publishDirectory $executableName

# Always rebuild the artifact under test; never fall back to an old EXE or test DLL.
& dotnet publish (Join-Path $PSScriptRoot 'src/SqlServerReadonlyMcp/SqlServerReadonlyMcp.csproj') `
    --configuration Release --runtime $runtimeIdentifier --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw '本地发布失败，禁止使用旧程序继续测试。'
}
$executablePath = (Resolve-Path -LiteralPath $executablePath -ErrorAction Stop).Path
Write-Host "测试发布程序：$executablePath"
Write-Host "测试配置文件：$configPath"
Write-Host "发布程序 SHA-256：$((Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash)"
$queryDatabase = if ($hasReferenceCase) { $TargetDatabase } else { [string]$detailsSettings.database }
$actualIncludeJobs = $IncludeJobs.IsPresent -or $referenceSettings.includeJobs -eq $true
$actualRequireReference = $RequireReference.IsPresent -or $referenceSettings.requireReference -eq $true
$variables = [ordered]@{
    SQLSERVER_MCP_INTEGRATION_CONFIG = $configPath
    SQLSERVER_MCP_INTEGRATION_EXE = $executablePath
    SQLSERVER_MCP_INTEGRATION_QUERY_DATABASE = $queryDatabase
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
