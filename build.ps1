param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath 'csc.exe'))) {
    $frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Install .NET Framework 4.8 before building.' }
$outputDirectory = Join-Path $projectRoot 'bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Management.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll') |
    ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') |
    ForEach-Object { '/reference:' + (Join-Path (Join-Path $frameworkPath 'WPF') $_) }
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
$applicationPath = Join-Path $outputDirectory 'LumaDock.exe'
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'assets\MonitorControl.ico'))) { & (Join-Path $projectRoot 'tools\CreateIcon.ps1') }
& $compilerPath /nologo /utf8output /codepage:65001 /langversion:5 /optimize+ /platform:anycpu /target:winexe "/out:$applicationPath" "/win32icon:$(Join-Path $projectRoot 'assets\MonitorControl.ico')" "/win32manifest:$(Join-Path $projectRoot 'app.manifest')" "/resource:$(Join-Path $projectRoot 'src\MainView.xaml'),MonitorControl.MainView.xaml" "/resource:$(Join-Path $projectRoot 'assets\MonitorControl.ico'),MonitorControl.ico" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination ($applicationPath + '.config') -Force
Write-Output "Built: $applicationPath"
if ($Test) {
    $testPath = Join-Path $outputDirectory 'MonitorControl.Tests.exe'
    $testSources = @('src\Capabilities.cs','src\NativeMethods.cs','src\MonitorService.cs','tests\Tests.cs') | ForEach-Object { Join-Path $projectRoot $_ }
    & $compilerPath /nologo /utf8output /codepage:65001 /langversion:5 /target:exe "/out:$testPath" @references @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $testPath
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $settingsTestPath = Join-Path $outputDirectory 'MonitorControl.SettingsTests.exe'
    & $compilerPath /nologo /utf8output /codepage:65001 /langversion:5 /target:exe "/out:$settingsTestPath" @references (Join-Path $projectRoot 'src\WidgetSettings.cs') (Join-Path $projectRoot 'tests\WidgetSettingsTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Settings test build failed.' }
    & $settingsTestPath (Join-Path $projectRoot 'artifacts\settings-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Settings tests failed.' }
    $uiTestPath = Join-Path $outputDirectory 'MonitorControl.UiTests.exe'
    & $compilerPath /nologo /utf8output /codepage:65001 /langversion:5 /target:winexe "/out:$uiTestPath" "/reference:$applicationPath" @references (Join-Path $projectRoot 'tests\UiTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination ($uiTestPath + '.config') -Force
    $testArtifacts = Join-Path $projectRoot 'artifacts'
    New-Item -ItemType Directory -Path $testArtifacts -Force | Out-Null
    $uiResults = Join-Path $testArtifacts 'ui-tests.txt'
    $uiProcess = Start-Process -FilePath $uiTestPath -ArgumentList ('"{0}"' -f $uiResults) -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath $uiResults
    if ($uiProcess.ExitCode -ne 0) { throw 'UI tests failed.' }
}
