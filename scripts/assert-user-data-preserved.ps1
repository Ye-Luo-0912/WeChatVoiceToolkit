[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$StatePath,
    [switch]$Capture
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($DataRoot.Trim().Trim('"'))
$state = [IO.Path]::GetFullPath($StatePath.Trim().Trim('"'))
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "The user-data test root does not exist: $root"
}

function Get-DataSnapshot([string]$path) {
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $path -File -Recurse -Force) {
        $attributes = [IO.File]::GetAttributes($file.FullName)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The user-data test root contains a reparse point: $($file.FullName)"
        }

        $relative = [IO.Path]::GetRelativePath($path, $file.FullName).Replace('\', '/')
        $files += [pscustomobject]@{
            Path = $relative
            Length = [int64]$file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    return [pscustomobject]@{
        Format = 'wechatvoice-user-data-preservation-v1'
        Root = $path
        Files = @($files | Sort-Object Path)
    }
}

if ($Capture) {
    $parent = [IO.Path]::GetDirectoryName($state)
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $snapshot = Get-DataSnapshot $root
    $snapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $state -Encoding UTF8
    Write-Host "Captured $($snapshot.Files.Count) user-data files."
    exit 0
}

if (-not (Test-Path -LiteralPath $state -PathType Leaf)) {
    throw "The user-data preservation state does not exist: $state"
}
$expected = Get-Content -LiteralPath $state -Raw -Encoding UTF8 | ConvertFrom-Json
if ($expected.Format -ne 'wechatvoice-user-data-preservation-v1') {
    throw 'The user-data preservation state format is invalid.'
}
foreach ($file in @($expected.Files)) {
    $path = [IO.Path]::GetFullPath((Join-Path $root ([string]$file.Path).Replace('/', [IO.Path]::DirectorySeparatorChar)))
    if (-not $path.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The user-data preservation state escapes its root: $($file.Path)"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "User data was removed: $($file.Path)"
    }
    $actual = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([int64]$actual.Length -ne [int64]$file.Length -or $hash -ne ([string]$file.Sha256).ToLowerInvariant()) {
        throw "User data changed: $($file.Path)"
    }
}
Write-Host "Verified $(@($expected.Files).Count) user-data files were preserved."
