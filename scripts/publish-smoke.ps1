param(
    [string]$CertificateThumbprint = '',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [switch]$RequireSignature,
    [switch]$ReleaseTrustSmoke
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path ([System.IO.Path]::GetTempPath()) ('wechatvoice-publish-' + [guid]::NewGuid().ToString('N'))
$install = $null

function Assert-GeneratedDirectory([string]$path, [string]$prefix) {
    $full = [IO.Path]::GetFullPath($path)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ((-not $full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) -or
        (-not [IO.Path]::GetFileName($full).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to operate on a non-generated directory: $full"
    }
}

function Remove-GeneratedDirectory([string]$path, [string]$prefix) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) { return }
    Assert-GeneratedDirectory $path $prefix
    Remove-Item -LiteralPath $path -Recurse -Force
}

function Get-PublishedExecutables([string]$directory) {
    @(
        'WeChatVoice.Cli.exe',
        'WeChatVoice.Desktop.exe',
        'WeChatVoice.KeyBroker.exe',
        'WeChatVoice.SqlCipherWorker.exe'
    ) | ForEach-Object {
        $path = Join-Path $directory $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing published executable: $_"
        }
        $path
    }
}

function Protect-InstallDirectory([string]$directory) {
    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $directory /inheritance:r /grant:r `
        '*S-1-5-18:(OI)(CI)(F)' `
        '*S-1-5-32-544:(OI)(CI)(F)' `
        "*${currentSid}:(OI)(CI)(RX)" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not protect the release smoke installation directory (exit $LASTEXITCODE)."
    }
}

function Restore-InstallDirectoryAcl([string]$directory) {
    if (-not (Test-Path -LiteralPath $directory)) { return }
    & icacls.exe $directory /reset /t /c | Out-Host
}

try {
    if ($ReleaseTrustSmoke) {
        $RequireSignature = $true
    }

    if ($RequireSignature -and
        [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
        [string]::IsNullOrWhiteSpace($PfxPath)) {
        throw 'Signed release smoke requires -CertificateThumbprint or -PfxPath.'
    }

    New-Item -ItemType Directory -Path $out -Force | Out-Null
    foreach ($project in @(
        'src/WeChatVoice.Cli/WeChatVoice.Cli.csproj',
        'src/WeChatVoice.Desktop/WeChatVoice.Desktop.csproj',
        'src/WeChatVoice.KeyBroker/WeChatVoice.KeyBroker.csproj',
        'src/WeChatVoice.SqlCipherWorker/WeChatVoice.SqlCipherWorker.csproj')) {
        dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -o $out --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project (exit $LASTEXITCODE)." }
    }

    $executables = @(Get-PublishedExecutables $out)
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -or -not [string]::IsNullOrWhiteSpace($PfxPath)) {
        $signScript = Join-Path $repo 'scripts/sign-release.ps1'
        & $signScript -Directory $out -CertificateThumbprint $CertificateThumbprint -PfxPath $PfxPath -PfxPassword $PfxPassword
        if ($LASTEXITCODE -ne 0) { throw "Release signing failed (exit $LASTEXITCODE)." }
    }
    elseif ($RequireSignature -or $env:CI -and $env:WECHATVOICE_SIGNED_RELEASE -eq 'true') {
        throw 'Release publish output is unsigned; signed release smoke requires an Authenticode certificate.'
    }
    else {
        Write-Host 'Unsigned layout smoke: release trust is intentionally not asserted.'
    }

    # Signing changes PE bytes. Generate both manifests only after signing so
    # runtime hash verification binds the final shipped files.
    & (Join-Path $repo 'build/GenerateWorkerBundleManifest.ps1') -Directory $out
    & (Join-Path $repo 'build/GenerateBrokerBundleManifest.ps1') -Directory $out -RequireSignedPublisher:$RequireSignature
    if ($LASTEXITCODE -ne 0) { throw 'Bundle manifest generation failed.' }

    foreach ($manifest in @('WeChatVoice.SqlCipherWorker.bundle.json', 'WeChatVoice.KeyBroker.bundle.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $out $manifest) -PathType Leaf)) {
            throw "Missing bundle manifest: $manifest"
        }
    }

    $desktopDirectory = $out
    $smokeArguments = @('--smoke-check')
    if ($ReleaseTrustSmoke) {
        $install = Join-Path ([IO.Path]::GetTempPath()) ('wechatvoice-install-' + [guid]::NewGuid().ToString('N'))
        Assert-GeneratedDirectory $install 'wechatvoice-install-'
        New-Item -ItemType Directory -Path $install -Force | Out-Null
        Get-ChildItem -LiteralPath $out -Force | Copy-Item -Destination $install -Recurse -Force
        Protect-InstallDirectory $install
        $desktopDirectory = $install
        $smokeArguments += '--release-trust-smoke'
    }

    $desktop = Join-Path $desktopDirectory 'WeChatVoice.Desktop.exe'
    $smoke = Start-Process -FilePath $desktop -ArgumentList $smokeArguments -Wait -PassThru -WindowStyle Hidden
    if ($smoke.ExitCode -ne 0) {
        throw "Desktop smoke check failed with exit code $($smoke.ExitCode)."
    }
    Write-Host 'Desktop smoke check passed.'

    if (@(Get-ChildItem -LiteralPath $out -File).Count -eq 0) { throw 'Publish output is empty.' }
    Write-Host "Publish smoke passed: $out"
}
finally {
    if ($null -ne $install) {
        Restore-InstallDirectoryAcl $install
        Remove-GeneratedDirectory $install 'wechatvoice-install-'
    }
    Remove-GeneratedDirectory $out 'wechatvoice-publish-'
}
