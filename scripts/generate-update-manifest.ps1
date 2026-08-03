[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PublisherThumbprint,
    [string]$PackageUri = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-identity.ps1')
$releaseIdentity = Get-WeChatVoiceReleaseIdentity

$package = [IO.Path]::GetFullPath($PackagePath.Trim().Trim('"'))
$output = [IO.Path]::GetFullPath($OutputPath.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    throw "The MSIX package does not exist: $package"
}
if ([IO.Path]::GetExtension($package) -ne '.msix') {
    throw 'PackagePath must point to an .msix package.'
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'Version is required.' }
if ([string]::IsNullOrWhiteSpace($PublisherThumbprint)) { throw 'PublisherThumbprint is required.' }
$publisherFingerprint = $PublisherThumbprint.Replace(' ', '').ToLowerInvariant()
if ($publisherFingerprint -notmatch '^[0-9a-f]{64}$') {
    throw 'PublisherThumbprint must be the 64-character SHA-256 fingerprint of the signer certificate.'
}

. (Join-Path $PSScriptRoot 'publisher-fingerprint.ps1')
$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
    throw "The MSIX Authenticode signature is not valid: $($signature.Status)."
}
$actualPublisherFingerprint = Get-CertificateSha256Fingerprint -Certificate $signature.SignerCertificate
if ($actualPublisherFingerprint -ne $publisherFingerprint) {
    throw 'PublisherThumbprint does not match the MSIX signer certificate.'
}
$publisherKeyId = Get-CertificatePublicKeyId -Certificate $signature.SignerCertificate

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) { throw 'The MSIX has no AppxManifest.xml.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$appx = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $identity = $appx.Package.Identity
    $application = $appx.Package.Applications.Application
    if ($null -eq $identity -or $null -eq $application) { throw 'The MSIX AppxManifest identity is incomplete.' }
    $packageName = [string]$identity.Name
    $packagePublisher = [string]$identity.Publisher
    $publisherId = Get-AppxPublisherId -Publisher $packagePublisher
    $packageVersion = [string]$identity.Version
    $packageArchitecture = [string]$identity.ProcessorArchitecture
    $applicationExecutable = [string]$application.Executable
    if ([string]::IsNullOrWhiteSpace($packageName) -or [string]::IsNullOrWhiteSpace($packagePublisher) -or [string]::IsNullOrWhiteSpace($applicationExecutable)) {
        throw 'The MSIX AppxManifest identity or executable is empty.'
    }
}
finally { $archive.Dispose() }

Assert-WeChatVoiceReleaseIdentity ([pscustomobject]@{
    Name = $packageName
    Architecture = $packageArchitecture
    Executable = $applicationExecutable
})

if ($packageVersion -ne $Version) {
    throw "The requested update version $Version does not match the MSIX Identity Version $packageVersion."
}
if ($packageArchitecture -ne 'x64') {
    throw "The release installer requires an x64 MSIX package; found architecture $packageArchitecture."
}

$parsedVersion = $null
if (-not [version]::TryParse($Version, [ref]$parsedVersion)) {
    throw "Version is not a valid four-part Windows package version: $Version"
}

$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    format = 'wechatvoice-update-v1'
    packageId = 'WeChatVoiceToolkit'
    version = $Version
    packageFile = [IO.Path]::GetFileName($package)
    packageSha256 = $packageHash
    packageByteLength = (Get-Item -LiteralPath $package).Length
    publisherThumbprint = $publisherFingerprint
    publisherKeyId = $publisherKeyId
    identityName = $packageName
    identityPublisher = $packagePublisher
    publisherId = $publisherId
    packageFamilyName = $packageName + '_' + $publisherId
    identityVersion = $packageVersion
    identityArchitecture = $packageArchitecture
    applicationExecutable = $applicationExecutable
    packageUri = if ([string]::IsNullOrWhiteSpace($PackageUri)) { $null } else { $PackageUri }
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}

$parent = [IO.Path]::GetDirectoryName($output)
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$temporary = Join-Path ($parent ?? [IO.Path]::GetTempPath()) ('.' + [IO.Path]::GetFileName($output) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    $json = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $output -Force
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

Write-Host "Update manifest created: $output"
