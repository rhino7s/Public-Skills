[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$repositoryRoot = (& git -C $projectRoot rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw '无法定位 Git 仓库。'
}

$repositoryRoot = (Resolve-Path -LiteralPath $repositoryRoot.Trim()).Path
$repositoryPrefix = $repositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$projectPrefix = if ($projectRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    '.'
}
elseif ($projectRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    $projectRoot.Substring($repositoryPrefix.Length).Replace('\', '/')
}
else {
    throw "项目目录不在 Git 仓库内：$projectRoot"
}
$trackedFiles = if ($projectPrefix -eq '.') {
    @(& git -C $repositoryRoot ls-files)
}
else {
    @(& git -C $repositoryRoot ls-files -- $projectPrefix)
}

if ($LASTEXITCODE -ne 0) {
    throw '无法读取 Git 跟踪文件列表。'
}

$candidateFiles = if ($projectPrefix -eq '.') {
    @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard)
}
else {
    @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard -- $projectPrefix)
}

if ($LASTEXITCODE -ne 0) {
    throw '无法读取待公开文件列表。'
}

$forbiddenTrackedFiles = @(
    $trackedFiles | Where-Object {
        $_ -match '(^|/)appsettings\.local\.json$' -or
        $_ -match '(^|/)integration\.local\.json$' -or
        $_ -match '(^|/)logs/' -or
        $_ -match '(^|/)publish/' -or
        $_ -match '\.log$'
    }
)

if ($forbiddenTrackedFiles.Count -gt 0) {
    $paths = $forbiddenTrackedFiles -join [Environment]::NewLine
    throw "发现不应提交的配置或运行产物：$([Environment]::NewLine)$paths"
}

$localConfigs = @(Get-ChildItem -LiteralPath $projectRoot -Filter 'appsettings.local.json' -File -Recurse)
$sensitiveValues = [Collections.Generic.List[object]]::new()
$publicExampleValues = @(
    'ExampleDatabase',
    'SQLSERVER\INSTANCE',
    '<readonly_login>',
    '<password>',
    'readonly_test',
    'invalid.example.local',
    'not-a-real-secret'
)
foreach ($configFile in $localConfigs) {
    $config = Get-Content -LiteralPath $configFile.FullName -Raw | ConvertFrom-Json
    foreach ($fieldName in @('server', 'username', 'password', 'defaultDatabase')) {
        $value = [string]$config.connection.$fieldName
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        if ($value -in $publicExampleValues) {
            continue
        }

        if ($fieldName -ne 'password' -and $value.Length -lt 3) {
            continue
        }

        $sensitiveValues.Add([pscustomobject]@{ Field = $fieldName; Value = $value })
    }
}

$textExtensions = @('.cs', '.csproj', '.json', '.md', '.ps1', '.sh', '.slnx', '.sql')
foreach ($candidateFile in $candidateFiles) {
    $fullPath = Join-Path $repositoryRoot $candidateFile
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $extension = [IO.Path]::GetExtension($fullPath).ToLowerInvariant()
    $leafName = [IO.Path]::GetFileName($fullPath)
    if ($extension -notin $textExtensions -and $leafName -notin @('.gitattributes', '.gitignore')) {
        continue
    }

    if ((Get-Item -LiteralPath $fullPath).Length -gt 2MB) {
        continue
    }

    if ($extension -eq '.ps1') {
        $bytes = [IO.File]::ReadAllBytes($fullPath)
        $hasUtf8Bom = $bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and
            $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF
        if (-not $hasUtf8Bom) {
            throw "PowerShell 脚本必须使用 UTF-8 BOM，以兼容 Windows PowerShell 5.1：$candidateFile"
        }
    }

    $content = [IO.File]::ReadAllText($fullPath)
    foreach ($sensitiveValue in $sensitiveValues) {
        $containsValue = if ($sensitiveValue.Field -eq 'password') {
            $content.IndexOf($sensitiveValue.Value, [StringComparison]::Ordinal) -ge 0
        }
        else {
            $pattern = '(?<![\p{L}\p{N}_])' + [Regex]::Escape($sensitiveValue.Value) + '(?![\p{L}\p{N}_])'
            [Regex]::IsMatch($content, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }

        if ($containsValue) {
            throw "本机配置字段 '$($sensitiveValue.Field)' 的值出现在待公开文件 '$candidateFile'。"
        }
    }
}

Write-Host "公开仓库检查通过：$($candidateFiles.Count) 个项目文件未发现本地配置、日志、发布目录或本机连接值。"
