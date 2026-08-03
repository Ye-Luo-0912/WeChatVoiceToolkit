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

function Get-CertificatePublicKeyId {
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
        # The public-key ID is independent of the certificate's validity
        # period and is used as the installer-side publisher trust anchor.
        return [Convert]::ToHexString($hasher.ComputeHash($Certificate.GetPublicKey())).ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
    }
}

function Get-AppxPublisherId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Publisher
    )

    if ([string]::IsNullOrWhiteSpace($Publisher)) {
        throw 'An AppX publisher subject is required.'
    }

    # AppX PublisherId is the first eight bytes of SHA-256(UTF-16LE
    # publisher subject), encoded with the Windows base-32 alphabet. The
    # trailing zero bit makes the 64-bit input exactly 13 five-bit symbols.
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $hasher.ComputeHash([Text.Encoding]::Unicode.GetBytes($Publisher))
    }
    finally {
        $hasher.Dispose()
    }
    $bits = [Text.StringBuilder]::new(65)
    foreach ($byte in $hash[0..7]) {
        [void]$bits.Append([Convert]::ToString($byte, 2).PadLeft(8, '0'))
    }
    [void]$bits.Append('0')
    $alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
    $result = [Text.StringBuilder]::new(13)
    for ($offset = 0; $offset -lt 65; $offset += 5) {
        $index = [Convert]::ToInt32($bits.ToString().Substring($offset, 5), 2)
        [void]$result.Append($alphabet[$index])
    }

    return $result.ToString().ToLowerInvariant()
}
