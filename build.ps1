# Builds dist\PadDeck.exe (framework-dependent single file; needs the .NET 10 Desktop Runtime).
# Run tests with: .\build.ps1 -Test
param([switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Get-Process PadDeck -ErrorAction SilentlyContinue) {
    throw 'PadDeck is running. Quit it from the tray icon first.'
}

if (-not (Test-Path assets\PadDeck.ico)) { & .\tools\make-icon.ps1 }

dotnet publish PadDeck.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -o dist
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if ($Test) {
    $report = Join-Path $env:TEMP 'paddeck-selftest.txt'
    $p = Start-Process dist\PadDeck.exe -ArgumentList '--selftest', "`"$report`"" -Wait -PassThru
    Get-Content $report
    if ($p.ExitCode -ne 0) { throw 'self-test failed' }
}
