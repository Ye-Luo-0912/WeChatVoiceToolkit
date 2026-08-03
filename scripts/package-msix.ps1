[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$CertificateThumbprint = '',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [string]$Version = '1.0.0.0'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-identity.ps1')
$releaseIdentity = Get-WeChatVoiceReleaseIdentity

function Assert-GeneratedPath([string]$path, [string]$description) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw "$description is required." }
    $full = [IO.Path]::GetFullPath($path)
    if ([IO.Path]::GetFileName($full) -eq '') { throw "$description must be a file or directory path." }
    return $full
}

function Find-WindowsSdkTool([string]$name) {
    $roots = @()
    if (-not [string]::IsNullOrWhiteSpace($env:WindowsSdkDir)) {
        $roots += (Join-Path $env:WindowsSdkDir 'bin')
    }
    $roots += (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin')
    foreach ($root in $roots | Select-Object -Unique) {
        if (Test-Path -LiteralPath $root -PathType Container) {
            $versions = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d+(?:\.\d+){2,3}$' } |
                Sort-Object { [version]$_.Name } -Descending
            foreach ($versionDirectory in $versions) {
                # Prefer the x64 SDK tool for the win-x64 package. This also
                # avoids accidentally selecting an arm64 tool from a newer
                # SDK's sibling architecture directory.
                $candidate = Join-Path $versionDirectory.FullName (Join-Path 'x64' $name)
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
            }
        }
    }
    throw "$name was not found in the installed Windows SDK. Install the Windows 10/11 SDK before building an MSIX."
}

function Get-PublisherCertificate {
    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        return [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.Path]::GetFullPath($PfxPath),
            $PfxPassword,
            $flags)
    }

    $thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    foreach ($storeName in @('CurrentUser', 'LocalMachine')) {
        $certificate = Get-ChildItem "Cert:\$storeName\My\$thumbprint" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($certificate) { return $certificate
        }
    }
    throw 'A signing certificate must be supplied with -PfxPath or -CertificateThumbprint.'
}

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

function Remove-GeneratedDirectory([string]$path) {
    if (Test-Path -LiteralPath $path) {
        $full = [IO.Path]::GetFullPath($path)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $full.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a non-generated staging directory: $full"
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

$publishRoot = Assert-GeneratedPath $PublishDirectory 'PublishDirectory'
$packagePath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) { throw "Publish directory does not exist: $publishRoot" }
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'WeChatVoice.Desktop.exe') -PathType Leaf)) { throw 'The publish directory does not contain WeChatVoice.Desktop.exe.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'WeChatVoice.KeyBroker.bundle.json') -PathType Leaf)) { throw 'The publish directory has no signed Broker bundle manifest.' }

$certificate = Get-PublisherCertificate
$publisher = $certificate.Subject
$makeAppx = Find-WindowsSdkTool 'MakeAppx.exe'
$signtool = Find-WindowsSdkTool 'signtool.exe'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('wechatvoice-msix-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Get-ChildItem -LiteralPath $publishRoot -Force | ForEach-Object {
        $attributes = [IO.File]::GetAttributes($_.FullName)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "The publish output contains a reparse point: $($_.FullName)" }
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage $_.Name) -Recurse -Force
    }

    $assetDirectory = Join-Path $stage 'Assets'
    New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
    # A deterministic 1x1 PNG keeps the installer self-contained until the
    # product artwork is supplied; it is not used by the runtime or trust
    # boundary.
    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
    foreach ($name in @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')) {
        [IO.File]::WriteAllBytes((Join-Path $assetDirectory $name), $png)
    }

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="$($releaseIdentity.Name)" Publisher="$(Escape-Xml $publisher)" Version="$Version" ProcessorArchitecture="$($releaseIdentity.Architecture)" />
  <Properties>
    <DisplayName>WeChatVoiceToolkit</DisplayName>
    <PublisherDisplayName>WeChatVoiceToolkit</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources><Resource Language="en-us" /></Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" Executable="$($releaseIdentity.ApplicationExecutable)" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements AppListEntry="default" DisplayName="WeChatVoiceToolkit" Description="WeChat voice export toolkit" BackgroundColor="#FFFFFF" Square44x44Logo="Assets\Square44x44Logo.png" Square150x150Logo="Assets\Square150x150Logo.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
    [IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

    $packageParent = [IO.Path]::GetDirectoryName($packagePath)
    if ($packageParent) { New-Item -ItemType Directory -Path $packageParent -Force | Out-Null }
    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    & $makeAppx pack /d $stage /p $packagePath /o | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }

    $signArguments = @('sign', '/fd', 'SHA256')
    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        $signArguments += @('/f', [IO.Path]::GetFullPath($PfxPath))
        if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) { $signArguments += @('/p', $PfxPassword) }
    }
    else {
        $signArguments += @('/sha1', $CertificateThumbprint.Replace(' ', ''))
    }
    $signArguments += $packagePath
    & $signtool @signArguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE." }

    Write-Host "MSIX package created: $packagePath"
}
finally {
    Remove-GeneratedDirectory $stage
    $certificate.Dispose()
}
