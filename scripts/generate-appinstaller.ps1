[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$PackageUri,
    [Parameter(Mandatory = $true)][string]$AppInstallerUri,
    [int]$HoursBetweenUpdateChecks = 24
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-identity.ps1')

if ($HoursBetweenUpdateChecks -lt 1 -or $HoursBetweenUpdateChecks -gt 168) {
    throw 'HoursBetweenUpdateChecks must be between 1 and 168.'
}

function Assert-HttpsUri([string]$value, [string]$name) {
    $parsed = $null
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$parsed) -or $parsed.Scheme -ne 'https') {
        throw "$name must be an absolute HTTPS URI."
    }

    return $parsed.AbsoluteUri
}

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

$package = [IO.Path]::GetFullPath($PackagePath.Trim().Trim('"'))
$output = [IO.Path]::GetFullPath($OutputPath.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    throw "The MSIX package does not exist: $package"
}
if ([IO.Path]::GetExtension($package) -ne '.msix') {
    throw 'PackagePath must point to an .msix package.'
}
$packageUri = Assert-HttpsUri $PackageUri 'PackageUri'
$appInstallerUri = Assert-HttpsUri $AppInstallerUri 'AppInstallerUri'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $entry) { throw 'The MSIX has no AppxManifest.xml.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$document = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $namespace = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $document.SelectSingleNode('/f:Package/f:Identity', $namespace)
    $application = $document.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespace)
    if ($null -eq $identity -or $null -eq $application) {
        throw 'The MSIX AppxManifest identity is incomplete.'
    }

    $name = [string]$identity.Name
    $publisher = [string]$identity.Publisher
    $version = [string]$identity.Version
    $architecture = [string]$identity.ProcessorArchitecture
    $executable = [string]$application.Executable
    $invalidIdentity = [string]::IsNullOrWhiteSpace($name) -or
        [string]::IsNullOrWhiteSpace($publisher) -or
        [string]::IsNullOrWhiteSpace($version) -or
        [string]::IsNullOrWhiteSpace($executable) -or
        $architecture -cne 'x64' -or
        [IO.Path]::IsPathRooted($executable) -or
        $executable.Replace('\', '/').Split('/') -contains '..'
    if ($invalidIdentity) {
        throw 'The MSIX AppxManifest identity is invalid for the fixed x64 release channel.'
    }

    $releaseIdentity = Get-WeChatVoiceReleaseIdentity
    Assert-WeChatVoiceReleaseIdentity ([pscustomobject]@{
        Name = $name
        Publisher = $publisher
        Architecture = $architecture
        Executable = $executable
    })
    if ($null -eq $archive.GetEntry($executable.Replace('\', '/'))) {
        throw "The MSIX application executable is not present in the package: $executable"
    }

    $parsedVersion = $null
    if (-not [version]::TryParse($version, [ref]$parsedVersion)) {
        throw 'The MSIX AppxManifest Version is invalid.'
    }
}
finally {
    $archive.Dispose()
}

$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller Uri="$(Escape-Xml $appInstallerUri)" Version="$(Escape-Xml $version)" xmlns="http://schemas.microsoft.com/appx/appinstaller/2018">
  <MainBundle Name="$(Escape-Xml $name)" Publisher="$(Escape-Xml $publisher)" Version="$(Escape-Xml $version)" Uri="$(Escape-Xml $packageUri)" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="$HoursBetweenUpdateChecks" ShowPrompt="true" />
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
"@

$parent = [IO.Path]::GetDirectoryName($output)
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$temporary = Join-Path ($parent ?? [IO.Path]::GetTempPath()) ('.' + [IO.Path]::GetFileName($output) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, $xml.Trim() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $output -Force
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "AppInstaller created: $output"
