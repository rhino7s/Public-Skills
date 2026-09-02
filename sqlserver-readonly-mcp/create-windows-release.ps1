[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot '..')).Path
$projectPath = Join-Path $projectRoot 'src\SqlServerReadonlyMcp\SqlServerReadonlyMcp.csproj'
$publicCheckPath = Join-Path $projectRoot 'check-public-repo.ps1'
$assetName = 'sqlserver-readonly-mcp-win-x64.zip'
$checksumName = "$assetName.sha256"

$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}

foreach ($unsafePath in @($repositoryRoot, $projectRoot)) {
    if ($outputPath.Equals($unsafePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "输出目录不可为仓库或项目根目录：$outputPath"
    }
}

if (Test-Path -LiteralPath $outputPath) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
        throw "输出路径不是目录：$outputPath"
    }

    if (Get-ChildItem -LiteralPath $outputPath -Force | Select-Object -First 1) {
        throw "输出目录必须不存在或为空，避免混入旧文件：$outputPath"
    }
}
else {
    New-Item -ItemType Directory -Path $outputPath | Out-Null
}

[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = @(
    $projectXml.Project.PropertyGroup |
        ForEach-Object { [string]$_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
) | Select-Object -First 1

if ($projectVersion -ne $Version) {
    throw "标签版本 '$Version' 与项目版本 '$projectVersion' 不一致。"
}

& $publicCheckPath
if ($LASTEXITCODE -ne 0) {
    throw "公开仓库检查失败，退出代码：$LASTEXITCODE"
}

$outputPrefix = $outputPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$stagingPath = Join-Path $outputPath ".staging-$([Guid]::NewGuid().ToString('N'))"
$stagingFullPath = [IO.Path]::GetFullPath($stagingPath)
if (-not $stagingFullPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "暂存目录超出输出目录：$stagingFullPath"
}

$publishPath = Join-Path $stagingFullPath 'publish'
$packagePath = Join-Path $stagingFullPath 'package'
$packageDocsPath = Join-Path $packagePath 'docs'
$archivePath = Join-Path $outputPath $assetName
$checksumPath = Join-Path $outputPath $checksumName

try {
    New-Item -ItemType Directory -Path $publishPath | Out-Null
    New-Item -ItemType Directory -Path $packageDocsPath | Out-Null

    & dotnet publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $publishPath

    if ($LASTEXITCODE -ne 0) {
        throw "Windows x64 发布失败，退出代码：$LASTEXITCODE"
    }

    $packageSources = @(
        @{ Source = Join-Path $publishPath 'sqlserver-readonly-mcp.exe'; Destination = Join-Path $packagePath 'sqlserver-readonly-mcp.exe' }
        @{ Source = Join-Path $projectRoot 'appsettings.example.json'; Destination = Join-Path $packagePath 'appsettings.example.json' }
        @{ Source = Join-Path $projectRoot 'appsettings.schema.json'; Destination = Join-Path $packagePath 'appsettings.schema.json' }
        @{ Source = Join-Path $projectRoot 'README.md'; Destination = Join-Path $packagePath 'README.md' }
        @{ Source = Join-Path $projectRoot 'docs\agent-install.md'; Destination = Join-Path $packageDocsPath 'agent-install.md' }
    )

    foreach ($packageSource in $packageSources) {
        if (-not (Test-Path -LiteralPath $packageSource.Source -PathType Leaf)) {
            throw "缺少发布文件：$($packageSource.Source)"
        }

        Copy-Item -LiteralPath $packageSource.Source -Destination $packageSource.Destination
    }

    [IO.File]::WriteAllText(
        (Join-Path $packagePath 'VERSION.txt'),
        "$Version`n",
        [Text.UTF8Encoding]::new($false))

    $expectedEntries = @(
        'README.md',
        'VERSION.txt',
        'appsettings.example.json',
        'appsettings.schema.json',
        'docs/agent-install.md',
        'sqlserver-readonly-mcp.exe'
    ) | Sort-Object
    $actualEntries = @(
        Get-ChildItem -LiteralPath $packagePath -File -Recurse |
            ForEach-Object { [IO.Path]::GetRelativePath($packagePath, $_.FullName).Replace('\', '/') }
    ) | Sort-Object

    if (Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries) {
        throw '发布包暂存内容不符合固定白名单。'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $packagePath,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $archiveEntries = @(
            $archive.Entries |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) } |
                ForEach-Object { $_.FullName.Replace('\', '/') }
        ) | Sort-Object

        if (Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $archiveEntries) {
            throw 'ZIP 内容不符合固定白名单。'
        }

        if ($archiveEntries | Where-Object { $_ -match '(?i)(^|/)(logs?|publish)/|\.log$|\.local\.json$' }) {
            throw 'ZIP 中发现禁止发布的日志、发布目录或本地配置。'
        }
    }
    finally {
        $archive.Dispose()
    }

    $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $checksumPath,
        "$sha256  $assetName`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host "Windows Release 包已生成：$archivePath"
    Write-Host "SHA-256：$sha256"
}
finally {
    if (Test-Path -LiteralPath $stagingFullPath) {
        $resolvedStagingPath = (Resolve-Path -LiteralPath $stagingFullPath).Path
        if (-not $resolvedStagingPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理输出目录之外的暂存路径：$resolvedStagingPath"
        }

        Remove-Item -LiteralPath $resolvedStagingPath -Recurse -Force
    }
}
