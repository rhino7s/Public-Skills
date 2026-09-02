[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [switch]$ConfirmIntegrationTestsCompleted,

    [switch]$ConfirmIntegrationTestsNotRequired
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot '..')).Path
$expectedRemote = 'https://github.com/rhino7s/Public-Skills.git'
$tagName = "sqlserver-readonly-mcp-v$Version"
$tagRef = "refs/tags/$tagName"
$projectPath = Join-Path $projectRoot 'src\SqlServerReadonlyMcp\SqlServerReadonlyMcp.csproj'
$workflowPath = Join-Path $repositoryRoot '.github\workflows\release-sqlserver-readonly-mcp.yml'
$localConfigPath = Join-Path $projectRoot 'appsettings.local.json'
$integrationSettingsPath = Join-Path $projectRoot 'integration.local.json'
$createdLocalTag = $false
$pushedRemoteTag = $false

function Invoke-GitText {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = @(& git -C $repositoryRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [switch]$AllowNotFound
    )

    $headers = @{
        'Accept' = 'application/vnd.github+json'
        'User-Agent' = 'Public-Skills-Release-Verifier'
        'X-GitHub-Api-Version' = '2022-11-28'
    }

    try {
        return Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode
        if ($AllowNotFound -and $null -ne $statusCode -and [int]$statusCode -eq 404) {
            return $null
        }
        throw
    }
}

function Wait-GitHubRelease {
    param(
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][bool]$IsPrerelease
    )

    $workflowRunsUri = 'https://api.github.com/repos/rhino7s/Public-Skills/actions/workflows/release-sqlserver-readonly-mcp.yml/runs?event=push&per_page=20'
    $workflowRun = $null

    for ($attempt = 1; $attempt -le 40; $attempt++) {
        $runs = Invoke-GitHubApi -Uri $workflowRunsUri
        $workflowRun = @(
            $runs.workflow_runs |
                Where-Object { $_.head_sha -eq $Commit -and $_.head_branch -eq $Tag } |
                Select-Object -First 1
        ) | Select-Object -First 1

        if ($null -eq $workflowRun) {
            Write-Host "Waiting for GitHub Actions run ($attempt/40)..."
            Start-Sleep -Seconds 15
            continue
        }

        Write-Host "GitHub Actions status: $($workflowRun.status)"
        if ($workflowRun.status -eq 'completed') {
            break
        }

        Start-Sleep -Seconds 15
    }

    if ($null -eq $workflowRun -or $workflowRun.status -ne 'completed') {
        throw "Timed out waiting for the GitHub Actions run for $Tag. The tag was pushed and must be checked on GitHub."
    }

    $runUrl = "https://github.com/rhino7s/Public-Skills/actions/runs/$($workflowRun.id)"
    if ($workflowRun.conclusion -ne 'success') {
        throw "GitHub Actions failed with conclusion '$($workflowRun.conclusion)': $runUrl"
    }

    $encodedTag = [Uri]::EscapeDataString($Tag)
    $releaseApiUri = "https://api.github.com/repos/rhino7s/Public-Skills/releases/tags/$encodedTag"
    $release = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $release = Invoke-GitHubApi -Uri $releaseApiUri -AllowNotFound
        if ($null -ne $release) {
            break
        }
        Start-Sleep -Seconds 5
    }

    if ($null -eq $release) {
        throw "GitHub Actions succeeded but the Release was not found for tag $Tag."
    }

    if ($release.tag_name -ne $Tag -or [bool]$release.draft) {
        throw "Release identity or draft state is invalid for tag $Tag."
    }

    if ([bool]$release.prerelease -ne $IsPrerelease) {
        throw "Release prerelease state does not match tag $Tag."
    }

    $expectedAssets = @(
        'sqlserver-readonly-mcp-win-x64.zip',
        'sqlserver-readonly-mcp-win-x64.zip.sha256'
    ) | Sort-Object
    $actualAssets = @($release.assets | ForEach-Object { [string]$_.name }) | Sort-Object
    if (Compare-Object -ReferenceObject $expectedAssets -DifferenceObject $actualAssets) {
        throw "Release assets do not match the fixed whitelist for tag $Tag."
    }

    if (@($release.assets | Where-Object { [long]$_.size -le 0 }).Count -gt 0) {
        throw "Release contains an empty asset for tag $Tag."
    }

    Write-Host "GitHub Actions succeeded: $runUrl"
    Write-Host "Release verified: https://github.com/rhino7s/Public-Skills/releases/tag/$Tag"
}

if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
    throw "Release workflow is missing: $workflowPath"
}

$remoteUrl = Invoke-GitText -Arguments @('remote', 'get-url', 'origin')
if (-not $remoteUrl.Equals($expectedRemote, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unexpected origin URL. The actual value is hidden because it may contain credentials.'
}

$branch = Invoke-GitText -Arguments @('branch', '--show-current')
if ($branch -ne 'main') {
    throw "Release tags can only be created from main. Current branch: $branch"
}

$status = Invoke-GitText -Arguments @('status', '--porcelain')
if (-not [string]::IsNullOrWhiteSpace($status)) {
    throw 'Working tree must be clean before releasing.'
}

$gitEmail = Invoke-GitText -Arguments @('config', '--get', 'user.email')
if ($gitEmail -notmatch '@users\.noreply\.github\.com$') {
    throw 'Repository Git email must be a GitHub noreply address. The current value is hidden.'
}

& git -C $repositoryRoot fetch --quiet origin main --tags
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to fetch origin/main and tags.'
}

$headCommit = Invoke-GitText -Arguments @('rev-parse', 'HEAD')
$originCommit = Invoke-GitText -Arguments @('rev-parse', 'origin/main')
if ($headCommit -ne $originCommit) {
    throw "HEAD must equal origin/main. HEAD=$headCommit origin/main=$originCommit"
}

[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = @(
    $projectXml.Project.PropertyGroup |
        ForEach-Object { [string]$_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
) | Select-Object -First 1
if ($projectVersion -ne $Version) {
    throw "Requested version '$Version' does not match project version '$projectVersion'."
}

if ($ConfirmIntegrationTestsCompleted -and $ConfirmIntegrationTestsNotRequired) {
    throw 'Choose only one integration-test confirmation.'
}

& git -C $repositoryRoot show-ref --verify --quiet $tagRef
if ($LASTEXITCODE -eq 0) {
    throw "Local tag already exists: $tagName"
}
if ($LASTEXITCODE -ne 1) {
    throw "Unable to check local tag: $tagName"
}

$remoteTagOutput = @(& git -C $repositoryRoot ls-remote --exit-code --tags origin $tagRef 2>&1)
if ($LASTEXITCODE -eq 0) {
    throw "Remote tag already exists: $tagName"
}
if ($LASTEXITCODE -ne 2) {
    throw "Unable to check remote tag: $($remoteTagOutput -join [Environment]::NewLine)"
}

Push-Location -LiteralPath $projectRoot
try {
    & .\check-public-repo.ps1
    if ($LASTEXITCODE -ne 0) {
        throw 'Public repository check failed.'
    }

    & dotnet restore SqlServerReadonlyMcp.slnx
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet restore failed.'
    }

    & dotnet build SqlServerReadonlyMcp.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet build failed.'
    }

    & dotnet test SqlServerReadonlyMcp.slnx --configuration Release --no-restore --no-build
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet test failed.'
    }

    $canRunIntegrationTests =
        (Test-Path -LiteralPath $localConfigPath -PathType Leaf) -and
        (Test-Path -LiteralPath $integrationSettingsPath -PathType Leaf)
    if ($canRunIntegrationTests) {
        & .\test-integration.ps1
        if ($LASTEXITCODE -ne 0) {
            throw 'SQL Server integration tests failed.'
        }
    }
    elseif ($ConfirmIntegrationTestsCompleted) {
        Write-Host "Integration tests confirmed as completed for commit $headCommit."
    }
    elseif ($ConfirmIntegrationTestsNotRequired) {
        Write-Host "Integration tests confirmed as not required for commit $headCommit."
    }
    else {
        throw 'Local integration configuration is unavailable. Confirm the current commit with -ConfirmIntegrationTestsCompleted or -ConfirmIntegrationTestsNotRequired before creating a release tag.'
    }
}
finally {
    Pop-Location
}

& git -C $repositoryRoot fetch --quiet origin main --tags
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to refresh origin/main and tags after testing.'
}

$finalBranch = Invoke-GitText -Arguments @('branch', '--show-current')
if ($finalBranch -ne 'main') {
    throw "Branch changed during testing. Current branch: $finalBranch"
}

$finalStatus = Invoke-GitText -Arguments @('status', '--porcelain')
if (-not [string]::IsNullOrWhiteSpace($finalStatus)) {
    throw 'Working tree changed during testing.'
}

$finalHeadCommit = Invoke-GitText -Arguments @('rev-parse', 'HEAD')
$finalOriginCommit = Invoke-GitText -Arguments @('rev-parse', 'origin/main')
if ($finalHeadCommit -ne $headCommit -or $finalOriginCommit -ne $headCommit) {
    throw "Release state changed during testing. Tested=$headCommit HEAD=$finalHeadCommit origin/main=$finalOriginCommit"
}

if (-not $PSCmdlet.ShouldProcess("origin/$tagRef", "Create and push release tag for commit $headCommit")) {
    return
}

try {
    & git -C $repositoryRoot tag --annotate $tagName $headCommit --message "SQL Server Read-only MCP v$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create local tag: $tagName"
    }
    $createdLocalTag = $true

    $tagCommit = Invoke-GitText -Arguments @('rev-parse', "$tagName^{}")
    if ($tagCommit -ne $headCommit) {
        throw "Created tag does not point to the tested commit. Expected=$headCommit Actual=$tagCommit"
    }

    & git -C $repositoryRoot push origin "${tagRef}:${tagRef}"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to push release tag: $tagName"
    }
    $pushedRemoteTag = $true

    Write-Host "Release tag pushed: $tagName"
    Wait-GitHubRelease -Commit $headCommit -Tag $tagName -IsPrerelease $Version.Contains('-')
}
catch {
    if ($createdLocalTag -and -not $pushedRemoteTag) {
        & git -C $repositoryRoot tag --delete $tagName | Out-Null
    }
    throw
}
