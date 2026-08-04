[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$UpdateManifestPath,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$StatePath,
    [string]$FunctionalFlowScriptPath = '',
    [string]$UpgradePackagePath = '',
    [string]$UpgradeUpdateManifestPath = '',
    [string]$RollbackPackagePath = '',
    [string]$PackageName = 'WeChatVoiceToolkit',
    [switch]$SkipFunctionalFlow,
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'The installed Release Candidate E2E gate is Windows-only.'
}

<#
This is the installed Release Candidate gate. It deliberately composes the
existing install, rollback, uninstall, trust-smoke, and user-data verifier
scripts instead of duplicating AppX or data-retention rules.

When FunctionalFlowScriptPath is supplied, the script must accept these named
parameters and throw (or return a non-zero native exit code) on failure:

    -Phase initial|upgrade|rollback
    -InstallLocation <protected AppX install directory>
    -DataRoot <test user's data root>
    -PackageName <fixed AppX identity name>

The initial phase is the real lawful-data flow:
Environment -> Source Discovery -> Snapshot -> Materialize -> second contact
-> incoming scan -> exact export -> curation -> dataset build -> verify.
The harness does not fabricate a source database or bypass a Verified boundary.
Use -SkipFunctionalFlow only for an install/trust/lifecycle smoke where a real
data fixture is not available; that mode is intentionally not an RC approval.
#>

function Get-FullFile([string]$path, [string]$description) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw "$description is required." }
    $full = [IO.Path]::GetFullPath($path.Trim().Trim('"'))
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$description does not exist: $full"
    }
    $attributes = [IO.File]::GetAttributes($full)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$description cannot be a reparse point: $full"
    }
    return $full
}

function Get-FullDirectory([string]$path, [string]$description) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw "$description is required." }
    $full = [IO.Path]::GetFullPath($path.Trim().Trim('"'))
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "$description does not exist: $full"
    }
    $attributes = [IO.File]::GetAttributes($full)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$description cannot be a reparse point: $full"
    }
    return $full
}

function Invoke-UserDataCheck([string]$root, [string]$state, [switch]$capture) {
    $arguments = @{
        DataRoot = $root
        StatePath = $state
    }
    if ($capture) { $arguments.Capture = $true }
    & (Join-Path $PSScriptRoot 'assert-user-data-preserved.ps1') @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "The user-data preservation check failed with exit code $LASTEXITCODE."
    }
}

function Invoke-FunctionalFlow(
    [string]$phase,
    [string]$installLocation,
    [string]$dataRoot,
    [string]$packageName,
    [string]$flowPath) {
    if ($SkipFunctionalFlow) {
        Write-Warning "Skipping the real data flow for phase '$phase'; this run is not an RC approval."
        return
    }

    if ([string]::IsNullOrWhiteSpace($flowPath)) {
        throw 'A lawful-data FunctionalFlowScriptPath is required for the Release Candidate gate.'
    }

    & $flowPath `
        -Phase $phase `
        -InstallLocation $installLocation `
        -DataRoot $dataRoot `
        -PackageName $packageName
    if ($LASTEXITCODE -ne 0) {
        throw "The installed functional flow '$phase' failed with exit code $LASTEXITCODE."
    }
}

$package = Get-FullFile $PackagePath 'PackagePath'
$updateManifest = Get-FullFile $UpdateManifestPath 'UpdateManifestPath'
$dataRoot = Get-FullDirectory $DataRoot 'DataRoot'
$state = [IO.Path]::GetFullPath($StatePath.Trim().Trim('"'))
$flow = if ([string]::IsNullOrWhiteSpace($FunctionalFlowScriptPath)) {
    $null
} else {
    Get-FullFile $FunctionalFlowScriptPath 'FunctionalFlowScriptPath'
}
$upgrade = if ([string]::IsNullOrWhiteSpace($UpgradePackagePath)) { $null } else { Get-FullFile $UpgradePackagePath 'UpgradePackagePath' }
$upgradeManifest = if ([string]::IsNullOrWhiteSpace($UpgradeUpdateManifestPath)) { $null } else { Get-FullFile $UpgradeUpdateManifestPath 'UpgradeUpdateManifestPath' }
$rollback = if ([string]::IsNullOrWhiteSpace($RollbackPackagePath)) { $null } else { Get-FullFile $RollbackPackagePath 'RollbackPackagePath' }

if (($null -eq $upgrade) -xor ($null -eq $upgradeManifest)) {
    throw 'UpgradePackagePath and UpgradeUpdateManifestPath must be supplied together.'
}
if ($null -eq $rollback -and -not $SkipFunctionalFlow) {
    throw 'RollbackPackagePath is required for the Release Candidate lifecycle gate.'
}

$installScript = Join-Path $PSScriptRoot 'install-msix.ps1'
$rollbackScript = Join-Path $PSScriptRoot 'rollback-msix.ps1'
$uninstallScript = Join-Path $PSScriptRoot 'uninstall-msix.ps1'
$installedPackage = $null
$completed = $false
$primaryFailure = $null

try {
    Invoke-UserDataCheck $dataRoot $state -capture
    & $installScript `
        -PackagePath $package `
        -UpdateManifestPath $updateManifest `
        -PackageName $PackageName `
        -RunTrustSmoke
    if ($LASTEXITCODE -ne 0) { throw "Initial MSIX installation failed with exit code $LASTEXITCODE." }

    $installedPackage = @(Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending) | Select-Object -First 1
    if ($null -eq $installedPackage) { throw "The installed package '$PackageName' was not found." }
    Invoke-FunctionalFlow 'initial' ([string]$installedPackage.InstallLocation) $dataRoot $PackageName $flow
    Invoke-UserDataCheck $dataRoot $state

    if ($null -ne $upgrade) {
        & $installScript `
            -PackagePath $upgrade `
            -UpdateManifestPath $upgradeManifest `
            -PackageName $PackageName `
            -RunTrustSmoke
        if ($LASTEXITCODE -ne 0) { throw "MSIX upgrade failed with exit code $LASTEXITCODE." }
        $installedPackage = @(Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending) | Select-Object -First 1
        Invoke-FunctionalFlow 'upgrade' ([string]$installedPackage.InstallLocation) $dataRoot $PackageName $flow
        Invoke-UserDataCheck $dataRoot $state
    }

    if ($null -ne $rollback) {
        & $rollbackScript `
            -PackagePath $rollback `
            -PackageName $PackageName `
            -RunTrustSmoke
        if ($LASTEXITCODE -ne 0) { throw "MSIX rollback failed with exit code $LASTEXITCODE." }
        $installedPackage = @(Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending) | Select-Object -First 1
        Invoke-FunctionalFlow 'rollback' ([string]$installedPackage.InstallLocation) $dataRoot $PackageName $flow
        Invoke-UserDataCheck $dataRoot $state
    }

    $completed = $true
}
catch {
    $primaryFailure = $_
    throw
}
finally {
    if (-not $KeepInstalled) {
        try {
            & $uninstallScript -PackageName $PackageName
            if ($LASTEXITCODE -ne 0) { throw "MSIX uninstall failed with exit code $LASTEXITCODE." }
            if (@(Get-AppxPackage -Name $PackageName).Count -ne 0) {
                throw "The package '$PackageName' is still installed after the RC gate."
            }
            Invoke-UserDataCheck $dataRoot $state
        }
        catch {
            if ($null -eq $primaryFailure) {
                throw
            }

            Write-Warning "Cleanup after the primary RC failure also failed: $($_.Exception.Message)"
        }
    }
}

if ($completed) {
    if ($SkipFunctionalFlow) {
        Write-Warning 'Installed lifecycle smoke passed, but the real data flow was skipped.'
    } else {
        Write-Host 'Installed Release Candidate E2E passed; user data was preserved through install, upgrade, rollback, and uninstall.'
    }
}
