# Shared, non-overridable AppX identity for the formal release channel.
# Publisher certificate fingerprints remain deployment trust anchors and are
# deliberately supplied by the protected release environment; this file must
# not guess or generate a signing identity.

function Get-WeChatVoiceReleaseIdentity {
    [pscustomobject]@{
        Name = 'WeChatVoiceToolkit'
        Architecture = 'x64'
        ApplicationExecutable = 'WeChatVoice.Desktop.exe'
    }
}

function Assert-WeChatVoiceReleaseIdentity([object]$identity) {
    $expected = Get-WeChatVoiceReleaseIdentity
    if ($null -eq $identity -or [string]$identity.Name -cne $expected.Name -or [string]$identity.Architecture -cne $expected.Architecture -or [string]$identity.Executable -cne $expected.ApplicationExecutable) {
        throw 'The package identity is not the fixed WeChatVoiceToolkit release identity.'
    }
}

function Assert-WeChatVoicePackageName([string]$packageName) {
    $expected = Get-WeChatVoiceReleaseIdentity
    if ($packageName -cne $expected.Name) {
        throw "PackageName is fixed to '$($expected.Name)' for formal releases."
    }
}
