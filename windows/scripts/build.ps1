# Restore, build (Release, x64 and ARM64) and run the Core tests.
$ErrorActionPreference = 'Stop'
$windows = Resolve-Path (Join-Path $PSScriptRoot '..')

dotnet test (Join-Path $windows 'tests\MDEditor.Core.Tests') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

foreach ($arch in @(@{ Platform = 'x64'; Rid = 'win-x64' }, @{ Platform = 'ARM64'; Rid = 'win-arm64' })) {
    dotnet build (Join-Path $windows 'src\MDEditor.App') -c Release -p:Platform=$($arch.Platform) -r $($arch.Rid) --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $($arch.Rid)." }
}
