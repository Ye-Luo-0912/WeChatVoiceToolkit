param(
    [Parameter(Mandatory = $true)][string]$Directory
)

$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory.Trim().Trim('"'))
$exe = Join-Path $Directory 'WeChatVoice.SqlCipherWorker.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Worker EXE was not produced: $exe"
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
            throw "The Worker bundle contains a reparse-point file: $($file.FullName)"
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

    if ($files.Count -eq 0) { throw 'The Worker bundle has no loadable files.' }
    return @($files | Sort-Object relativePath)
}

$workerSha = Get-HashOrNull 'WeChatVoice.SqlCipherWorker.exe'

$nativeRelative = 'runtimes/win-x64/native/e_sqlcipher.dll'
$providerRelative = 'SQLitePCLRaw.provider.e_sqlcipher.dll'
$manifest = [ordered]@{
    workerExeSha256 = $workerSha
    depsFile = 'WeChatVoice.SqlCipherWorker.deps.json'
    depsSha256 = Get-HashOrNull 'WeChatVoice.SqlCipherWorker.deps.json'
    runtimeConfigFile = 'WeChatVoice.SqlCipherWorker.runtimeconfig.json'
    runtimeConfigSha256 = Get-HashOrNull 'WeChatVoice.SqlCipherWorker.runtimeconfig.json'
    nativeSqlCipherFile = $nativeRelative
    nativeSqlCipherSha256 = Get-HashOrNull $nativeRelative
    providerFile = $providerRelative
    providerSha256 = Get-HashOrNull $providerRelative
    files = Get-BundleFiles
}
$json = $manifest | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $Directory 'WeChatVoice.SqlCipherWorker.bundle.json'), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
