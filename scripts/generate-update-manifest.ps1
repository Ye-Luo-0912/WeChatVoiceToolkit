[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PublisherThumbprint,
    [string]$PackageUri = ''
)

$ErrorActionPreference = 'Stop'

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
