# Self-contained, ReadyToRun, untrimmed publish into artifacts/publish/<rid>.
param([string[]]$Rids = @('win-x64', 'win-arm64'))
$ErrorActionPreference = 'Stop'
$windows = Resolve-Path (Join-Path $PSScriptRoot '..')

foreach ($rid in $Rids) {
    $platform = if ($rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }
    $output = Join-Path $windows "artifacts\publish\$rid"
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }

    dotnet publish (Join-Path $windows 'src\MDEditor.App') -c Release -r $rid -p:Platform=$platform `
        -p:PublishReadyToRun=true -p:DebugType=none -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }
    if (-not (Test-Path (Join-Path $output 'preview\index.html'))) { throw "Preview page missing from $output." }
    # Code signing hook (v1 ships unsigned): run signtool on $output\MDEditor.exe here.
}
