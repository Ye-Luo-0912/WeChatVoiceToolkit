[CmdletBinding()]
param(
    [string]$CertificateThumbprint = '',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [switch]$RequireSignature,
    [switch]$ReleaseTrustSmoke,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

# This is the single supported package entry point. Keep publish, signing,
# post-signature manifests, package closure, and Desktop smoke in the existing
# implementation so CI and local diagnostics cannot drift into two layouts.
$publishSmoke = Join-Path $PSScriptRoot 'publish-smoke.ps1'
$arguments = @{}
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $arguments.CertificateThumbprint = $CertificateThumbprint }
if (-not [string]::IsNullOrWhiteSpace($PfxPath)) { $arguments.PfxPath = $PfxPath }
if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) { $arguments.PfxPassword = $PfxPassword }
if ($RequireSignature) { $arguments.RequireSignature = $true }
if ($ReleaseTrustSmoke) { $arguments.ReleaseTrustSmoke = $true }
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) { $arguments.OutputDirectory = $OutputDirectory }

& $publishSmoke @arguments
if ($LASTEXITCODE -ne 0) {
    throw "The release package workflow failed (exit $LASTEXITCODE)."
}
