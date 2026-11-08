# Builds the installers, portable zips and SHA256SUMS.txt from artifacts/publish (run publish.ps1 first).
# Also copies the x64 installer to artifacts/install.exe.
# Inno Setup 6 is located through $env:ISCC, PATH or its default install folders.
param([string[]]$Rids = @('win-x64', 'win-arm64'))
$ErrorActionPreference = 'Stop'
$windows = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $windows 'artifacts'

[xml]$props = Get-Content (Join-Path $windows 'Directory.Build.props')
$version = @($props.Project.PropertyGroup.Version)[0]

$iscc = $env:ISCC
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'ISCC.exe not found. Install Inno Setup 6 or set $env:ISCC.' }

$outputs = @()
foreach ($rid in $Rids) {
    $arch = $rid -replace '^win-', ''
    $source = Join-Path $artifacts "publish\$rid"
    if (-not (Test-Path (Join-Path $source 'MDEditor.exe'))) { throw "Run publish.ps1 first: $source is empty." }

    $base = "MDEditor-$version-win-$arch-setup"
    & $iscc /Qp "/DAppVersion=$version" "/DArch=$arch" "/DSourceDir=$source" "/DOutputDir=$artifacts" "/DBaseName=$base" `
        (Join-Path $windows 'installer\MDEditor.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $rid." }
    # Code signing hook (v1 ships unsigned): run signtool on the setup executable here.
    $outputs += Join-Path $artifacts "$base.exe"

    $zip = Join-Path $artifacts "MDEditor-$version-win-$arch.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $zip -CompressionLevel Optimal
    $outputs += $zip
}

if ($Rids -contains 'win-x64') {
    $install = Join-Path $artifacts 'install.exe'
    Copy-Item (Join-Path $artifacts "MDEditor-$version-win-x64-setup.exe") $install -Force
    $outputs += $install
}

$sums = foreach ($file in $outputs) {
    '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
$sums | Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
$sums
