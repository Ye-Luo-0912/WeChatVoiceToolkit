[CmdletBinding()]
param(
    [string]$PackageName = 'WeChatVoiceToolkit'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-identity.ps1')
Assert-WeChatVoicePackageName $PackageName
$installed = @(Get-AppxPackage -Name $PackageName)
if ($installed.Count -eq 0) {
    Write-Host "Package '$PackageName' is not installed. No user data was touched."
    exit 0
}

foreach ($package in $installed) {
    Remove-AppxPackage -Package $package.PackageFullName
    if ($LASTEXITCODE -ne 0) { throw "Remove-AppxPackage failed with exit code $LASTEXITCODE." }
}

if (@(Get-AppxPackage -Name $PackageName).Count -ne 0) {
    throw "Package '$PackageName' is still installed after uninstall."
}

# Deliberately do not delete LocalApplicationData, Snapshots, Workspaces,
# Export directories, or user caches. Those are user-owned data and have a
# separate explicit lifecycle in the application.
Write-Host "Uninstalled '$PackageName'; Snapshot, Workspace, and Export data were preserved."
