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
        'src/WeChatVoice.KeyBroker/WeChatVoice.KeyBroker.csproj',
        'src/WeChatVoice.SqlCipherWorker/WeChatVoice.SqlCipherWorker.csproj')) {
        dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -o $out --nologo
    }
    & (Join-Path $repo 'build/GenerateWorkerBundleManifest.ps1') -Directory $out
    & (Join-Path $repo 'build/GenerateBrokerBundleManifest.ps1') -Directory $out -PublisherThumbprint $PublisherThumbprint
    foreach ($file in @('WeChatVoice.Cli.exe', 'WeChatVoice.KeyBroker.exe', 'WeChatVoice.SqlCipherWorker.exe')) {
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
    $all = Get-ChildItem -LiteralPath $out -File
    if ($all.Count -eq 0) { throw 'Publish output is empty.' }
    Write-Host "Publish smoke passed: $out"
}
finally {
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    dotnet restore (Join-Path $repo 'WeChatVoice.slnx') --force-evaluate --nologo | Out-Host
}
