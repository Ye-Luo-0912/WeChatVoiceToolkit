param(
    [string]$CertificateThumbprint = '',
    [string]$PublisherThumbprint = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path ([System.IO.Path]::GetTempPath()) ('wechatvoice-publish-' + [guid]::NewGuid().ToString('N'))
try {
    foreach ($project in @(
        'src/WeChatVoice.Cli/WeChatVoice.Cli.csproj',
        'src/WeChatVoice.Desktop/WeChatVoice.Desktop.csproj',
        'src/WeChatVoice.KeyBroker/WeChatVoice.KeyBroker.csproj',
        'src/WeChatVoice.SqlCipherWorker/WeChatVoice.SqlCipherWorker.csproj')) {
        dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -o $out --nologo
    }
    & (Join-Path $repo 'build/GenerateWorkerBundleManifest.ps1') -Directory $out
    & (Join-Path $repo 'build/GenerateBrokerBundleManifest.ps1') -Directory $out -PublisherThumbprint $PublisherThumbprint
    foreach ($file in @('WeChatVoice.Cli.exe', 'WeChatVoice.Desktop.exe', 'WeChatVoice.KeyBroker.exe', 'WeChatVoice.SqlCipherWorker.exe')) {
        $path = Join-Path $out $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing published executable: $file" }
    }
    foreach ($bundle in @('WeChatVoice.SqlCipherWorker.bundle.json', 'WeChatVoice.KeyBroker.bundle.json')) {
        $path = Join-Path $out $bundle
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing bundle manifest: $bundle" }
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        & (Join-Path $repo 'scripts/sign-release.ps1') -Directory $out -CertificateThumbprint $CertificateThumbprint
    }
    elseif ($env:CI) {
        throw 'Release publish output is unsigned; CI requires an Authenticode-signed broker and worker bundle.'
    }
    else {
        Write-Host 'WARNING: Release publish output is unsigned. The CLI will reject the Broker unless --allow-development-broker is used against a repository build directory.'
    }
    # Launch smoke: the published Desktop app must start headless (--smoke-check),
    # exercise the composition root and Workflow State Machine, and exit 0.
    $desktop = Join-Path $out 'WeChatVoice.Desktop.exe'
    $smoke = Start-Process -FilePath $desktop -ArgumentList '--smoke-check' -Wait -PassThru -WindowStyle Hidden
    if ($smoke.ExitCode -ne 0) {
        throw "Desktop smoke check failed with exit code $($smoke.ExitCode)."
    }
    Write-Host 'Desktop --smoke-check passed.'

    $all = Get-ChildItem -LiteralPath $out -File
    if ($all.Count -eq 0) { throw 'Publish output is empty.' }
    Write-Host "Publish smoke passed: $out"
}
finally {
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    dotnet restore (Join-Path $repo 'WeChatVoice.slnx') --force-evaluate --nologo | Out-Host
}
