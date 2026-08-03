[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$UpdateManifestPath = '',
    [string]$PackageName = 'WeChatVoiceToolkit',
    [switch]$RunTrustSmoke,
    [switch]$ForceUpdateFromAnyVersion
)

$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackagePath.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "The MSIX package does not exist: $package" }
if ([IO.Path]::GetExtension($package) -ne '.msix') { throw 'PackagePath must point to an .msix package.' }

if (-not [string]::IsNullOrWhiteSpace($UpdateManifestPath)) {
    $updateManifest = [IO.Path]::GetFullPath($UpdateManifestPath.Trim().Trim('"'))
    if (-not (Test-Path -LiteralPath $updateManifest -PathType Leaf)) { throw "The update manifest does not exist: $updateManifest" }
    try {
        $metadata = Get-Content -LiteralPath $updateManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw 'The update manifest is not valid JSON.'
    }

    if ($metadata.format -ne 'wechatvoice-update-v1' -or $metadata.packageId -ne $PackageName) {
        throw 'The update manifest package identity is invalid.'
    }
    if ([IO.Path]::GetFileName($package) -ne [string]$metadata.packageFile) {
        throw 'The package does not match the update manifest packageFile.'
    }
    $actualHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$metadata.packageSha256).ToLowerInvariant()) {
        throw 'The MSIX package hash does not match the update manifest.'
    }
    if ((Get-Item -LiteralPath $package).Length -ne [int64]$metadata.packageByteLength) {
        throw 'The MSIX package length does not match the update manifest.'
    }
}
$arguments = @('-Path', $package)
if ($ForceUpdateFromAnyVersion) { $arguments += '-ForceUpdateFromAnyVersion' }
& Add-AppxPackage @arguments
if ($LASTEXITCODE -ne 0) { throw "Add-AppxPackage failed with exit code $LASTEXITCODE." }

$installed = @(Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending | Select-Object -First 1)
if ($installed.Count -ne 1) { throw "The installed package '$PackageName' could not be found after installation." }
Write-Host "Installed $PackageName version $($installed[0].Version)."

if ($RunTrustSmoke) {
    $desktop = Join-Path $installed[0].InstallLocation 'WeChatVoice.Desktop.exe'
    if (-not (Test-Path -LiteralPath $desktop -PathType Leaf)) { throw 'The installed Desktop executable is missing.' }
    $smoke = Start-Process -FilePath $desktop -ArgumentList @('--smoke-check', '--release-trust-smoke') -Wait -PassThru -WindowStyle Hidden
    if ($smoke.ExitCode -ne 0) { throw "Installed trust smoke failed with exit code $($smoke.ExitCode)." }
    Write-Host 'Installed ordinary-user trust smoke passed.'
}
