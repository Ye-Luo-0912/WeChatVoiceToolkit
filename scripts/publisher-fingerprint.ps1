function Get-CertificateSha256Fingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($null -eq $Certificate) {
        throw 'A publisher certificate is required.'
    }

    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($hasher.ComputeHash($Certificate.RawData)).ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
    }
}
