[CmdletBinding()]
param(
    [string]$Config
)

$ErrorActionPreference = 'Stop'

function Get-RuntimeIdentifier {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        if ($architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
            return 'win-x64'
        }
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Linux)) {
        if ($architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
            return 'linux-x64'
        }
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        if ($architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
            return 'osx-x64'
        }

        if ($architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
            return 'osx-arm64'
        }
    }

    throw "不支持当前平台或 CPU 架构：$architecture"
}

$runtimeIdentifier = Get-RuntimeIdentifier
$configPath = if (-not [string]::IsNullOrWhiteSpace($Config)) {
    $Config
}
elseif (-not [string]::IsNullOrWhiteSpace($env:SQLSERVER_MCP_CONFIG)) {
    $env:SQLSERVER_MCP_CONFIG
}
else {
    Join-Path $PSScriptRoot 'appsettings.local.json'
}
$executableName = if ($runtimeIdentifier -eq 'win-x64') {
    'sqlserver-readonly-mcp.exe'
}
else {
    'sqlserver-readonly-mcp'
}
$serverPath = Join-Path $PSScriptRoot "publish\$runtimeIdentifier\$executableName"

if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
    throw "找不到 $runtimeIdentifier 发布文件：$serverPath。请先运行 publish-all.ps1。"
}

if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "SQL MCP 配置文件不存在：$configPath"
}

& $serverPath --config (Resolve-Path -LiteralPath $configPath).Path
exit $LASTEXITCODE
