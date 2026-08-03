[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$PackageName = 'WeChatVoiceToolkit',
    [string]$AllowedPublisherThumbprint = $env:WECHATVOICE_ALLOWED_PUBLISHER_THUMBPRINT,
    [string]$AllowedPublisherKeyId = $env:WECHATVOICE_ALLOWED_PUBLISHER_KEY_ID,
    [string]$AllowedPublishersJson = $env:WECHATVOICE_ALLOWED_PUBLISHERS_JSON,
    [string]$AllowedPublisherPolicyId = $env:WECHATVOICE_ALLOWED_PUBLISHER_POLICY_ID,
    [switch]$RunTrustSmoke
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-identity.ps1')
Assert-WeChatVoicePackageName $PackageName
$installed = @(Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending)
if ($installed.Count -ne 1) { throw "The package '$PackageName' is not installed; rollback has no current version." }

$package = [IO.Path]::GetFullPath($PackagePath.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "The rollback MSIX does not exist: $package" }
$packageDirectory = [IO.Path]::GetDirectoryName($package)
if ([string]::IsNullOrWhiteSpace($packageDirectory)) { throw 'The rollback package has no containing directory.' }
$manifest = [IO.Path]::GetFullPath((Join-Path $packageDirectory 'update-manifest.json'))
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw 'A signed update-manifest.json is required next to the rollback package.'
}
$metadata = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
if ($metadata.format -ne 'wechatvoice-update-v2' -or $metadata.updateKind -ne 'rollback') { throw 'The rollback update manifest format or updateKind is invalid.' }
if ([string]$metadata.packageFile -ne [IO.Path]::GetFileName($package)) {
    throw 'The rollback package does not match update-manifest.json.'
}
$targetVersion = [version]$metadata.version

# When no update manifest accompanies a retained rollback package, AppX still
# validates its embedded identity/version during Add-AppxPackage. If metadata
# is available, require an actual downgrade so this command cannot silently
# masquerade as an upgrade.
if ($targetVersion -ge [version]$installed[0].Version -or [string]$metadata.rollbackFromVersion -ne [string]$installed[0].Version) {
    throw "The rollback package version $targetVersion is not lower than installed version $($installed[0].Version)."
}

$installer = Join-Path $PSScriptRoot 'install-msix.ps1'
$installerArguments = @{
    PackagePath = $package
    PackageName = $PackageName
    AllowedPublisherThumbprint = $AllowedPublisherThumbprint
    AllowedPublisherKeyId = $AllowedPublisherKeyId
    AllowedPublishersJson = $AllowedPublishersJson
    AllowedPublisherPolicyId = $AllowedPublisherPolicyId
    RunTrustSmoke = $RunTrustSmoke
}
$installerArguments.UpdateManifestPath = $manifest
& $installer @installerArguments
if ($LASTEXITCODE -ne 0) { throw "MSIX rollback installation failed with exit code $LASTEXITCODE." }
Write-Host "Rollback completed without touching Snapshot, Workspace, or Export data."
