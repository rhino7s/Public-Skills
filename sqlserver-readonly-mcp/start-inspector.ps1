[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$projectDirectory = $PSScriptRoot
$isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
$inspectorConfigName = if ($isWindows) {
    'inspector.config.local.json'
}
else {
    'inspector.config.unix.local.json'
}
$inspectorConfigPath = Join-Path $projectDirectory $inspectorConfigName
$sqlConfigPath = Join-Path $projectDirectory 'appsettings.local.json'

if (-not (Get-Command 'mcp-inspector' -ErrorAction SilentlyContinue)) {
    throw '找不到 mcp-inspector。请先安装：npm install -g @modelcontextprotocol/inspector@latest'
}

if (-not (Test-Path -LiteralPath $inspectorConfigPath -PathType Leaf)) {
    throw "Inspector 配置文件不存在：$inspectorConfigPath"
}

if ($isWindows) {
    $serverPath = Join-Path $projectDirectory 'publish\win-x64\sqlserver-readonly-mcp.exe'
    if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
        throw "MCP 可执行文件不存在：$serverPath。请先运行 publish-all.ps1。"
    }
}

if (-not (Test-Path -LiteralPath $sqlConfigPath -PathType Leaf)) {
    throw "SQL MCP 配置文件不存在：$sqlConfigPath"
}

Push-Location -LiteralPath $projectDirectory
try {
    & mcp-inspector --web --config $inspectorConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "MCP Inspector 已退出，代码：$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
