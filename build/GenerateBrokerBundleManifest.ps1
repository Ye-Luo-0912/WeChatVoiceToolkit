param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [string]$PublisherThumbprint = '',
    [switch]$RequireSignedPublisher
)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory.Trim().Trim('"'))
. (Join-Path $PSScriptRoot '..\scripts\publisher-fingerprint.ps1')
$exe = Join-Path $Directory 'WeChatVoice.KeyBroker.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Broker EXE was not produced: $exe"
}

function Get-RelativePath([string]$root, [string]$path) {
    $rootFull = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($path)
    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the bundle directory: $path"
    }
    return $pathFull.Substring($rootFull.Length).Replace('\', '/')
}

function Get-Sha256([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($path)
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Get-HashOrNull([string]$relative) {
    $path = Join-Path $Directory $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($path)
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Get-BundleFiles {
    $excluded = @(
        'WeChatVoice.KeyBroker.bundle.json',
        'WeChatVoice.SqlCipherWorker.bundle.json',
        'package-manifest.json',
        'SHA256SUMS.txt',
        'sbom.spdx.json',
        'AppxManifest.xml',
        'AppxBlockMap.xml',
        'AppxSignature.p7x',
        '[Content_Types].xml'
    )
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force) {
        $attributes = [IO.File]::GetAttributes($file.FullName)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The Broker bundle contains a reparse-point file: $($file.FullName)"
        }

        $relative = Get-RelativePath $Directory $file.FullName
        if ($excluded -contains ([IO.Path]::GetFileName($relative))) { continue }
        $hash = Get-Sha256 $file.FullName
        $files += [pscustomobject]@{
            relativePath = $relative
            sha256 = $hash
            byteLength = $file.Length
        }
    }

    if ($files.Count -eq 0) { throw 'The Broker bundle has no loadable files.' }
    return @($files | Sort-Object relativePath)
}

$brokerSha = Get-HashOrNull 'WeChatVoice.KeyBroker.exe'
$publisher = $PublisherThumbprint.Trim()
if ([string]::IsNullOrWhiteSpace($publisher) -and $RequireSignedPublisher) {
    $signature = Get-AuthenticodeSignature -LiteralPath $exe
    if ($signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate) {
        $publisher = Get-CertificateSha256Fingerprint -Certificate $signature.SignerCertificate
    }
    elseif ($RequireSignedPublisher) {
        throw "The Broker EXE is not Authenticode-signed: $($signature.Status)"
    }
}
if (-not [string]::IsNullOrWhiteSpace($publisher)) {
    $publisher = $publisher.Replace(' ', '').ToLowerInvariant()
    if ($publisher -notmatch '^[0-9a-f]{64}$') {
        throw 'PublisherThumbprint must be the 64-character SHA-256 fingerprint of the signer certificate.'
    }
}

$manifest = [ordered]@{
    brokerExeSha256 = $brokerSha
    depsFile = 'WeChatVoice.KeyBroker.deps.json'
    depsSha256 = Get-HashOrNull 'WeChatVoice.KeyBroker.deps.json'
    runtimeConfigFile = 'WeChatVoice.KeyBroker.runtimeconfig.json'
    runtimeConfigSha256 = Get-HashOrNull 'WeChatVoice.KeyBroker.runtimeconfig.json'
    publisherThumbprint = if ([string]::IsNullOrWhiteSpace($publisher)) { $null } else { $publisher }
    files = Get-BundleFiles
}
$json = $manifest | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $Directory 'WeChatVoice.KeyBroker.bundle.json'), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
