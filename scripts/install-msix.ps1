[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$UpdateManifestPath,
    [string]$PackageName = 'WeChatVoiceToolkit',
    [string]$AllowedPublisherThumbprint = $env:WECHATVOICE_ALLOWED_PUBLISHER_THUMBPRINT,
    [string]$AllowedPublisherKeyId = $env:WECHATVOICE_ALLOWED_PUBLISHER_KEY_ID,
    [string]$AllowedPublishersJson = $env:WECHATVOICE_ALLOWED_PUBLISHERS_JSON,
    [string]$AllowedPublisherPolicyId = $env:WECHATVOICE_ALLOWED_PUBLISHER_POLICY_ID,
    [switch]$RunTrustSmoke,
    [switch]$ForceUpdateFromAnyVersion
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-identity.ps1')
$releaseIdentity = Get-WeChatVoiceReleaseIdentity
. (Join-Path $PSScriptRoot 'publisher-fingerprint.ps1')
. (Join-Path $PSScriptRoot 'release-publisher-policy.ps1')
$package = [IO.Path]::GetFullPath($PackagePath.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "The MSIX package does not exist: $package" }
if ([IO.Path]::GetExtension($package) -ne '.msix') { throw 'PackagePath must point to an .msix package.' }

Assert-WeChatVoicePackageName $PackageName
$publisherPolicy = Get-WeChatVoicePublisherPolicy -PolicyJson $AllowedPublishersJson -LegacyThumbprint $AllowedPublisherThumbprint -LegacyKeyId $AllowedPublisherKeyId -PolicyId $AllowedPublisherPolicyId

function Read-AppxIdentity([string]$path) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) { throw 'The MSIX has no AppxManifest.xml.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$document = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $namespace = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
        $namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $identity = $document.SelectSingleNode('/f:Package/f:Identity', $namespace)
        $application = $document.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespace)
        if ($null -eq $identity -or $null -eq $application) { throw 'The MSIX identity or application is incomplete.' }
        $executable = [string]$application.Executable
        if ([string]::IsNullOrWhiteSpace($executable) -or
            [IO.Path]::IsPathRooted($executable) -or
            $executable.Replace('\', '/').Split('/') -contains '..') {
            throw 'The MSIX application executable path is invalid.'
        }
        if ($null -eq $archive.GetEntry($executable.Replace('\', '/'))) {
            throw "The MSIX application executable is not present in the package: $executable"
        }
        [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            PublisherId = Get-AppxPublisherId -Publisher ([string]$identity.Publisher)
            PackageFamilyName = [string]$identity.Name + '_' + (Get-AppxPublisherId -Publisher ([string]$identity.Publisher))
            Version = [string]$identity.Version
            Architecture = [string]$identity.ProcessorArchitecture
            Executable = $executable
        }
    }
    finally { $archive.Dispose() }
}

$packageIdentity = Read-AppxIdentity $package
Assert-WeChatVoiceReleaseIdentity $packageIdentity
if ($packageIdentity.Name -ne $PackageName) { throw "The MSIX Identity Name does not match PackageName: $($packageIdentity.Name)" }
$parsedPackageVersion = $null
if (-not [version]::TryParse($packageIdentity.Version, [ref]$parsedPackageVersion)) { throw 'The MSIX Identity Version is invalid.' }

    $updateManifest = [IO.Path]::GetFullPath($UpdateManifestPath.Trim().Trim('"'))
    if (-not (Test-Path -LiteralPath $updateManifest -PathType Leaf)) { throw "The update manifest does not exist: $updateManifest" }
    try {
        $metadata = Get-Content -LiteralPath $updateManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw 'The update manifest is not valid JSON.'
    }

    if ($metadata.format -ne 'wechatvoice-update-v2' -or $metadata.packageId -ne $PackageName) {
        throw 'The update manifest package identity is invalid.'
    }
    if ([string]$metadata.publisherPolicyId -ne [string]$publisherPolicy.PolicyId) {
        throw 'The update manifest publisher policy does not match the installed release policy.'
    }
    if ([IO.Path]::GetFileName($package) -ne [string]$metadata.packageFile) {
        throw 'The package does not match the update manifest packageFile.'
    }
    $actualHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if (([string]$metadata.packageSha256 -notmatch '^[0-9a-fA-F]{64}$') -or
        ($actualHash -ne ([string]$metadata.packageSha256).ToLowerInvariant())) {
        throw 'The MSIX package hash does not match the update manifest.'
    }
    if ($null -eq $metadata.packageByteLength -or [int64]$metadata.packageByteLength -lt 1 -or
        (Get-Item -LiteralPath $package).Length -ne [int64]$metadata.packageByteLength) {
        throw 'The MSIX package length does not match the update manifest.'
    }
    foreach ($field in @('publisherThumbprint', 'publisherKeyId', 'identityName', 'identityPublisher', 'publisherId', 'packageFamilyName', 'identityVersion', 'identityArchitecture', 'applicationExecutable')) {
        if ([string]::IsNullOrWhiteSpace([string]$metadata.$field)) { throw "The update manifest field '$field' is missing." }
    }
    [void](Find-WeChatVoicePublisherAnchor $publisherPolicy $metadata.publisherThumbprint $metadata.publisherKeyId $packageIdentity.PublisherId ([DateTimeOffset]::UtcNow))
    if ($metadata.identityName -ne $packageIdentity.Name -or
        $metadata.identityPublisher -ne $packageIdentity.Publisher -or
        $metadata.publisherId -ne $packageIdentity.PublisherId -or
        $metadata.packageFamilyName -ne $packageIdentity.PackageFamilyName -or
        $metadata.identityVersion -ne $packageIdentity.Version -or
        $metadata.identityArchitecture -ne $packageIdentity.Architecture -or
        $metadata.applicationExecutable -ne $packageIdentity.Executable) {
        throw 'The MSIX AppxManifest identity does not match the independently supplied update manifest.'
    }
    if ([string]$metadata.version -ne $packageIdentity.Version) {
        throw 'The update manifest version does not match the MSIX Identity Version.'
    }
    $updateKind = [string]$metadata.updateKind
    if ($updateKind -notin @('install', 'upgrade', 'rollback')) {
        throw 'The update manifest updateKind is invalid.'
    }
    $rollbackFromVersion = $null
    if (-not [string]::IsNullOrWhiteSpace([string]$metadata.rollbackFromVersion) -and
        -not [version]::TryParse([string]$metadata.rollbackFromVersion, [ref]$rollbackFromVersion)) {
        throw 'The update manifest rollbackFromVersion is invalid.'
    }
    $minimumPreviousVersion = $null
    if (-not [string]::IsNullOrWhiteSpace([string]$metadata.minimumPreviousVersion) -and
        -not [version]::TryParse([string]$metadata.minimumPreviousVersion, [ref]$minimumPreviousVersion)) {
        throw 'The update manifest minimumPreviousVersion is invalid.'
    }
    $installedBefore = @(Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending)
    if ($installedBefore.Count -gt 1) { throw "Multiple installed versions of '$PackageName' were found." }
    if ($installedBefore.Count -eq 1) {
        $currentVersion = [version]$installedBefore[0].Version
        if ($updateKind -eq 'rollback') {
            if ($parsedPackageVersion -ge $currentVersion -or $null -eq $rollbackFromVersion -or $rollbackFromVersion -ne $currentVersion) {
                throw "The rollback package must target a lower version and name the exact installed version ($currentVersion)."
            }
        }
        elseif ($parsedPackageVersion -le $currentVersion) {
            throw "The package version $parsedPackageVersion is not a strictly newer update than $currentVersion."
        }
        elseif ($null -ne $minimumPreviousVersion -and $currentVersion -lt $minimumPreviousVersion) {
            throw "The installed version $currentVersion is below the update manifest minimumPreviousVersion $minimumPreviousVersion."
        }
    }
    elseif ($updateKind -eq 'rollback') {
        throw 'A rollback requires an installed current package.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $package
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "The MSIX Authenticode signature is not valid: $($signature.Status)."
    }
    $actualPublisherThumbprint = Get-CertificateSha256Fingerprint -Certificate $signature.SignerCertificate
    if ($actualPublisherThumbprint -ne ([string]$metadata.publisherThumbprint).Replace(' ', '').ToLowerInvariant()) {
        throw 'The MSIX signer thumbprint does not match the update manifest.'
    }
    $actualPublisherKeyId = Get-CertificatePublicKeyId -Certificate $signature.SignerCertificate
    if ($actualPublisherKeyId -ne ([string]$metadata.publisherKeyId).Replace(' ', '').ToLowerInvariant()) {
        throw 'The MSIX signer public key does not match the update manifest.'
    }
if ($ForceUpdateFromAnyVersion -and $updateKind -ne 'rollback') {
    throw 'ForceUpdateFromAnyVersion is reserved for a rollback update manifest.'
}
if ($updateKind -eq 'rollback') {
    Add-AppxPackage -Path $package -ForceUpdateFromAnyVersion
}
else {
    Add-AppxPackage -Path $package
}

$installed = @(Get-AppxPackage -Name $PackageName | Where-Object { $_.Version -eq $parsedPackageVersion })
if ($installed.Count -ne 1) { throw "The installed package '$PackageName' could not be found after installation." }
$installedPackage = $installed[0]
if ([string]$installedPackage.Name -ne $packageIdentity.Name -or
    [string]$installedPackage.Publisher -ne $packageIdentity.Publisher -or
    [version]$installedPackage.Version -ne $parsedPackageVersion) {
    throw 'The installed AppX identity does not exactly match the signed package.'
}
if ([string]::IsNullOrWhiteSpace([string]$installedPackage.PackageFamilyName) -or
    [string]$installedPackage.PackageFamilyName -cne $packageIdentity.PackageFamilyName) {
    throw 'The installed PackageFamilyName is missing or does not match the package identity.'
}
if ([string]::IsNullOrWhiteSpace([string]$installedPackage.PublisherId) -or
    [string]$installedPackage.PublisherId -cne $packageIdentity.PublisherId) {
    throw 'The installed PublisherId is missing or does not match the package identity.'
}
if ([string]$installedPackage.PackageFamilyName -cne [string]$metadata.packageFamilyName) {
    throw 'The installed PackageFamilyName does not match the independent update manifest.'
}
$programFiles = [IO.Path]::GetFullPath([Environment]::GetFolderPath('ProgramFiles')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$installLocation = [IO.Path]::GetFullPath([string]$installedPackage.InstallLocation)
if (-not $installLocation.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The package was not installed under the protected Program Files root: $installLocation"
}
Write-Host "Installed $PackageName version $($installedPackage.Version) ($($installedPackage.PackageFamilyName))."

if ($RunTrustSmoke) {
    $desktop = Join-Path $installedPackage.InstallLocation $packageIdentity.Executable
    if (-not (Test-Path -LiteralPath $desktop -PathType Leaf)) { throw 'The installed Desktop executable is missing.' }
    $smoke = Start-Process -FilePath $desktop -ArgumentList @('--smoke-check', '--release-trust-smoke') -Wait -PassThru -WindowStyle Hidden
    if ($smoke.ExitCode -ne 0) { throw "Installed trust smoke failed with exit code $($smoke.ExitCode)." }
    Write-Host 'Installed ordinary-user trust smoke passed.'
}
