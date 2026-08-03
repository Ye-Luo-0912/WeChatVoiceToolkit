param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [string]$CertificateThumbprint = '',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory.Trim().Trim('"'))

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -and [string]::IsNullOrWhiteSpace($PfxPath)) {
    throw 'Provide either -CertificateThumbprint (CurrentUser/My or LocalMachine/My) or -PfxPath.'
}

function Find-SignTool {
    $kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kit)) { return $null }
    # Recent SDKs may place architecture directories beside versioned SDK
    # directories. Only parse numeric SDK directory names and always use the
    # x64 tool for the win-x64 release layout.
    $candidates = Get-ChildItem -LiteralPath $kit -Directory |
        Where-Object { $_.Name -match '^\d+(?:\.\d+){2,3}$' } |
        Sort-Object { [version]$_.Name } -Descending
    foreach ($versionDirectory in $candidates) {
        $candidate = Join-Path $versionDirectory.FullName 'x64\signtool.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

$signTool = Find-SignTool
if (-not $signTool) {
    throw 'signtool.exe was not found under the Windows 10 SDK bin directory.'
}

$signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $signArgs += @('/sha1', $CertificateThumbprint)
}
else {
    $signArgs += @('/f', $PfxPath)
    if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) {
        $signArgs += @('/p', $PfxPassword)
    }
}

foreach ($file in @('WeChatVoice.Cli.exe', 'WeChatVoice.Desktop.exe', 'WeChatVoice.KeyBroker.exe', 'WeChatVoice.SqlCipherWorker.exe')) {
    $path = Join-Path $Directory $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing published executable: $file" }
    & $signTool @signArgs $path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file (exit $LASTEXITCODE)." }
}

foreach ($file in @('WeChatVoice.Cli.exe', 'WeChatVoice.Desktop.exe', 'WeChatVoice.KeyBroker.exe', 'WeChatVoice.SqlCipherWorker.exe')) {
    $path = Join-Path $Directory $file
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid') {
        throw "Authenticode verification failed for ${file}: $($signature.Status)"
    }
    Write-Host "Signed and verified: $file ($($signature.SignerCertificate.Subject))"
}
