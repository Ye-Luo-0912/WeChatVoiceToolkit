[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$PackageVersion = '0.0.0'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = [IO.Path]::GetFullPath($Directory)
$destination = [IO.Path]::GetFullPath($OutputPath)
$toolManifest = Join-Path $repo '.config/dotnet-tools.json'

if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "The SBOM build drop does not exist: $root"
}
if (-not (Test-Path -LiteralPath $toolManifest -PathType Leaf)) {
    throw "The pinned SBOM tool manifest does not exist: $toolManifest"
}
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    throw 'PackageVersion must not be empty.'
}

$destinationParent = [IO.Path]::GetDirectoryName($destination)
if ($destinationParent) {
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
}

function Remove-GeneratedPath([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $full = [IO.Path]::GetFullPath($path)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a non-generated SBOM path: $full"
    }

    Remove-Item -LiteralPath $full -Recurse -Force
}

$manifestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('wechatvoice-sbom-' + [guid]::NewGuid().ToString('N'))
try {
    # Use the repository-pinned Microsoft SBOM Tool instead of hand-writing a
    # file that merely resembles SPDX. Restore is deterministic from the tool
    # manifest and the generated manifest directory stays outside the package
    # drop, so it cannot become part of the Broker load closure.
    dotnet tool restore --tool-manifest $toolManifest
    if ($LASTEXITCODE -ne 0) {
        throw "The pinned SBOM tool could not be restored (exit $LASTEXITCODE)."
    }

    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Force
    }

    Push-Location $repo
    try {
        & dotnet tool run sbom-tool generate `
            -b $root `
            -bc $repo `
            -m $manifestDirectory `
            -pn 'WeChatVoiceToolkit' `
            -pv $PackageVersion `
            -ps 'WeChatVoiceToolkit' `
            -nsb 'https://github.com/Ye-Luo-0912/WeChatVoiceToolkit/sbom' `
            -nsu ([guid]::NewGuid().ToString('N')) `
            -D
        if ($LASTEXITCODE -ne 0) {
            throw "Microsoft SBOM Tool failed (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }

    $generated = Get-ChildItem -LiteralPath $manifestDirectory -Filter 'manifest.spdx.json' -File -Recurse |
        Select-Object -First 1
    if ($null -eq $generated) {
        throw 'Microsoft SBOM Tool did not produce an SPDX manifest.'
    }

    Copy-Item -LiteralPath $generated.FullName -Destination $destination -Force
}
finally {
    Remove-GeneratedPath $manifestDirectory
}
