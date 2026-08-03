[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$PackageName = 'WeChatVoiceToolkit',
    [switch]$RunTrustSmoke
)

$ErrorActionPreference = 'Stop'
$installed = @(Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending | Select-Object -First 1)
if ($installed.Count -ne 1) { throw "The package '$PackageName' is not installed; rollback has no current version." }

$package = [IO.Path]::GetFullPath($PackagePath.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "The rollback MSIX does not exist: $package" }
$packageDirectory = [IO.Path]::GetDirectoryName($package)
if ([string]::IsNullOrWhiteSpace($packageDirectory)) { throw 'The rollback package has no containing directory.' }
$manifest = [IO.Path]::GetFullPath((Join-Path $packageDirectory 'update-manifest.json'))
$targetVersion = $null
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    $metadata = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($metadata.format -ne 'wechatvoice-update-v1') { throw 'The rollback update manifest format is invalid.' }
    $targetVersion = [version]$metadata.version
}

# When no update manifest accompanies a retained rollback package, AppX still
# validates its embedded identity/version during Add-AppxPackage. If metadata
# is available, require an actual downgrade so this command cannot silently
# masquerade as an upgrade.
if ($null -ne $targetVersion -and $targetVersion -ge [version]$installed[0].Version) {
    throw "The rollback package version $targetVersion is not lower than installed version $($installed[0].Version)."
}

$installer = Join-Path $PSScriptRoot 'install-msix.ps1'
$installerArguments = @{
    PackagePath = $package
    PackageName = $PackageName
    RunTrustSmoke = $RunTrustSmoke
    ForceUpdateFromAnyVersion = $true
}
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    $installerArguments.UpdateManifestPath = $manifest
}
& $installer @installerArguments
if ($LASTEXITCODE -ne 0) { throw "MSIX rollback installation failed with exit code $LASTEXITCODE." }
Write-Host "Rollback completed without touching Snapshot, Workspace, or Export data."
