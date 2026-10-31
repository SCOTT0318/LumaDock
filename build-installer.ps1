param([switch]$Test, [string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$version = '0.0.1'
if (-not $CompilerPath) { $CompilerPath = Join-Path $projectRoot 'installer\tools\InnoSetup\ISCC.exe' }
if (-not (Test-Path -LiteralPath $CompilerPath)) {
    $availableCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($availableCompiler) { $CompilerPath = $availableCompiler.Source }
}
if (-not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Inno Setup 7 compiler is required. See installer/README.md for portable bootstrap instructions.'
}
foreach ($requiredFile in @('bin\LumaDock.exe','bin\LumaDock.exe.config','assets\MonitorControl.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $requiredFile))) { throw "Missing build input: $requiredFile. Build the application first." }
}
& (Join-Path $projectRoot 'installer\CreateArtwork.ps1')
$installerScript = Join-Path $projectRoot 'installer\LumaDock.iss'
& $CompilerPath /Qp "/DProjectRoot=$projectRoot" "/DAppVersion=$version" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Output "Built: $(Join-Path $projectRoot "artifacts\LumaDock-$version.exe")"
if ($Test) {
    & (Join-Path $projectRoot 'installer\TestInstaller.ps1') -CompilerPath $CompilerPath
}
