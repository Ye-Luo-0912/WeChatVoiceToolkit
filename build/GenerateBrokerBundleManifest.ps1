param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [string]$PublisherThumbprint = '',
    [switch]$RequireSignedPublisher
)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory.Trim().Trim('"'))
$exe = Join-Path $Directory 'WeChatVoice.KeyBroker.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Broker EXE was not produced: $exe"
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

$brokerSha = Get-HashOrNull 'WeChatVoice.KeyBroker.exe'
$publisher = $PublisherThumbprint.Trim()
if ([string]::IsNullOrWhiteSpace($publisher) -and $RequireSignedPublisher) {
    $signature = Get-AuthenticodeSignature -LiteralPath $exe
    if ($signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate) {
        $publisher = $signature.SignerCertificate.Thumbprint
    }
    elseif ($RequireSignedPublisher) {
        throw "The Broker EXE is not Authenticode-signed: $($signature.Status)"
    }
}

$manifest = [ordered]@{
    brokerExeSha256 = $brokerSha
    depsFile = 'WeChatVoice.KeyBroker.deps.json'
    depsSha256 = Get-HashOrNull 'WeChatVoice.KeyBroker.deps.json'
    runtimeConfigFile = 'WeChatVoice.KeyBroker.runtimeconfig.json'
    runtimeConfigSha256 = Get-HashOrNull 'WeChatVoice.KeyBroker.runtimeconfig.json'
    publisherThumbprint = if ([string]::IsNullOrWhiteSpace($publisher)) { $null } else { $publisher.ToLowerInvariant() }
}
$json = $manifest | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $Directory 'WeChatVoice.KeyBroker.bundle.json'), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
