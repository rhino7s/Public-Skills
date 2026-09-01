[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectDirectory = $PSScriptRoot
$projectPath = Join-Path $projectDirectory 'src\SqlServerReadonlyMcp\SqlServerReadonlyMcp.csproj'
$runtimeIdentifiers = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')

if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
    throw '找不到 dotnet。请先安装 .NET 10 SDK。'
}
foreach ($runtimeIdentifier in $runtimeIdentifiers) {
    $outputDirectory = Join-Path $projectDirectory "publish\$runtimeIdentifier"
    & dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $runtimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $outputDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "发布 $runtimeIdentifier 失败，退出代码：$LASTEXITCODE"
    }
}
