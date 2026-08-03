# Protected release input for MSIX/update verification. A rotated
# certificate is allowed only when its certificate and public-key fingerprints
# are explicitly present in this policy. The package identity remains fixed
# and is checked separately by release-identity.ps1.

function Normalize-WeChatVoiceFingerprint([string]$value, [string]$name) {
    $normalized = ($value ?? '').Replace(' ', '').ToLowerInvariant()
    if ($normalized -notmatch '^[0-9a-f]{64}$') {
        throw "$name must be a 64-character SHA-256 value."
    }

    return $normalized
}

function Get-WeChatVoicePublisherPolicy {
    [CmdletBinding()]
    param(
        [string]$PolicyJson = $env:WECHATVOICE_ALLOWED_PUBLISHERS_JSON,
        [string]$LegacyThumbprint = $env:WECHATVOICE_ALLOWED_PUBLISHER_THUMBPRINT,
        [string]$LegacyKeyId = $env:WECHATVOICE_ALLOWED_PUBLISHER_KEY_ID,
        [string]$PolicyId = $env:WECHATVOICE_ALLOWED_PUBLISHER_POLICY_ID
    )

    $entries = @()
    if (-not [string]::IsNullOrWhiteSpace($PolicyJson)) {
        try {
            $decoded = $PolicyJson | ConvertFrom-Json
        }
        catch {
            throw 'WECHATVOICE_ALLOWED_PUBLISHERS_JSON is not valid JSON.'
        }

        $rawEntries = if ($null -ne $decoded.entries) { @($decoded.entries) } else { @($decoded) }
        foreach ($entry in $rawEntries) {
            if ($null -eq $entry) { continue }
            $entries += [pscustomobject]@{
                Thumbprint = Normalize-WeChatVoiceFingerprint ([string]$entry.thumbprint) 'Publisher thumbprint'
                KeyId = Normalize-WeChatVoiceFingerprint ([string]$entry.keyId) 'Publisher public-key ID'
                PublisherId = if ([string]::IsNullOrWhiteSpace([string]$entry.publisherId)) { $null } else { ([string]$entry.publisherId).Trim().ToLowerInvariant() }
                NotBeforeUtc = if ([string]::IsNullOrWhiteSpace([string]$entry.notBeforeUtc)) { $null } else { [DateTimeOffset]::Parse([string]$entry.notBeforeUtc).ToUniversalTime() }
                NotAfterUtc = if ([string]::IsNullOrWhiteSpace([string]$entry.notAfterUtc)) { $null } else { [DateTimeOffset]::Parse([string]$entry.notAfterUtc).ToUniversalTime() }
            }
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($LegacyThumbprint) -and -not [string]::IsNullOrWhiteSpace($LegacyKeyId)) {
        $entries += [pscustomobject]@{
            Thumbprint = Normalize-WeChatVoiceFingerprint $LegacyThumbprint 'Publisher thumbprint'
            KeyId = Normalize-WeChatVoiceFingerprint $LegacyKeyId 'Publisher public-key ID'
            PublisherId = $null
            NotBeforeUtc = $null
            NotAfterUtc = $null
        }
    }

    if ($entries.Count -eq 0) {
        throw 'At least one independent allowed publisher certificate/public-key pair is required.'
    }

    $duplicate = $entries | Group-Object { $_.Thumbprint + ':' + $_.KeyId } | Where-Object Count -gt 1
    if ($null -ne $duplicate) {
        throw 'The allowed publisher policy contains duplicate certificate/public-key pairs.'
    }

    [pscustomobject]@{
        Format = 'wechatvoice-publisher-policy-v1'
        PolicyId = if ([string]::IsNullOrWhiteSpace($PolicyId)) { 'wechatvoice-publisher-policy-v1' } else { $PolicyId.Trim() }
        Entries = @($entries)
    }
}

function Find-WeChatVoicePublisherAnchor {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Policy,
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [Parameter(Mandatory = $true)][string]$KeyId,
        [string]$PublisherId,
        [DateTimeOffset]$AtUtc = [DateTimeOffset]::UtcNow
    )

    $normalizedThumbprint = Normalize-WeChatVoiceFingerprint $Thumbprint 'Publisher thumbprint'
    $normalizedKeyId = Normalize-WeChatVoiceFingerprint $KeyId 'Publisher public-key ID'
    $match = @($Policy.Entries | Where-Object {
        $_.Thumbprint -eq $normalizedThumbprint -and
        $_.KeyId -eq $normalizedKeyId -and
        ([string]::IsNullOrWhiteSpace([string]$_.PublisherId) -or [string]$_.PublisherId -eq ([string]$PublisherId).ToLowerInvariant()) -and
        ($null -eq $_.NotBeforeUtc -or $AtUtc -ge $_.NotBeforeUtc) -and
        ($null -eq $_.NotAfterUtc -or $AtUtc -le $_.NotAfterUtc)
    })
    if ($match.Count -ne 1) {
        throw 'The publisher certificate/public-key pair is not allowed by the independent release policy.'
    }

    return $match[0]
}
