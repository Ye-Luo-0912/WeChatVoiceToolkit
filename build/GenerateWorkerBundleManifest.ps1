param(
    [Parameter(Mandatory = $true)][string]$Directory
)

$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory.Trim().Trim('"'))
$exe = Join-Path $Directory 'WeChatVoice.SqlCipherWorker.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Worker EXE was not produced: $exe"
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
}
$json = $manifest | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $Directory 'WeChatVoice.SqlCipherWorker.bundle.json'), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
