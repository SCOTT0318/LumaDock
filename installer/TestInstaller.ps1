param([Parameter(Mandatory=$true)][string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot 'artifacts\installer-tests'
$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
$resolvedProjectRoot = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
if (-not $resolvedTestRoot.StartsWith($resolvedProjectRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Test directory must be inside the project.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$scriptPath = Join-Path $PSScriptRoot 'LumaDock.iss'
& $CompilerPath /Qp "/DProjectRoot=$projectRoot" /DTestBuild=1 "/DTestRoot=$testRoot" "/DOutputRoot=$testRoot" $scriptPath
if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
$testSetup = Join-Path $testRoot 'LumaDock-Setup-Test.exe'
$testResults = New-Object 'System.Collections.Generic.List[string]'
function Assert-Installer([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $testResults.Add("PASS $Message")
}
function Run-InstallerProcess([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer process failed ($($process.ExitCode)): $Executable" }
}
function Wait-UntilRemoved([string]$Path) {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) { return }
        Start-Sleep -Milliseconds 125
    }
    throw "Uninstall did not remove $Path"
}
$customParent = Join-Path $testRoot '사용자 지정 설치 경로'
$customInstall = Join-Path $customParent 'LumaDock'
$noIconsInstall = Join-Path $testRoot 'No shortcuts install\LumaDock'
$desktopLink = Join-Path $testRoot 'Desktop\LumaDock.lnk'
$startMenuLink = Join-Path $testRoot 'StartMenu\LumaDock\LumaDock.lnk'
try {
    Run-InstallerProcess $testSetup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="{0}"' -f $customParent),'/TASKS="desktopicon,startmenuicon"',('/LOG="{0}"' -f (Join-Path $testRoot 'install-selected.log')))
    $appPath = Join-Path $customInstall 'LumaDock.exe'
    Assert-Installer (Test-Path -LiteralPath $appPath) 'Selecting a parent with spaces and Korean characters installs into its LumaDock child'
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $customParent 'LumaDock.exe'))) 'Application payload is not placed directly in the selected parent'
    Assert-Installer (Test-Path -LiteralPath (Join-Path $customInstall 'LumaDock.exe.config')) 'Runtime configuration is installed beside the application'
    Assert-Installer ((Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'bin\LumaDock.exe') -Algorithm SHA256).Hash) 'Installed executable exactly matches the final build payload'
    Assert-Installer ((Get-FileHash -LiteralPath (Join-Path $customInstall 'LumaDock.exe.config') -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'bin\LumaDock.exe.config') -Algorithm SHA256).Hash) 'Installed runtime configuration exactly matches the final build payload'
    Assert-Installer (Test-Path -LiteralPath (Join-Path $customInstall 'unins000.exe')) 'A standard uninstaller is created'
    Assert-Installer (Test-Path -LiteralPath $desktopLink) 'Selected desktop shortcut is created in the isolated test desktop'
    Assert-Installer (Test-Path -LiteralPath $startMenuLink) 'Selected Start menu shortcut is created in the isolated test Start menu'
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($link in @($desktopLink,$startMenuLink)) {
            $shortcut = $shell.CreateShortcut($link)
            Assert-Installer ($shortcut.TargetPath -eq $appPath) 'Shortcut targets the chosen installation directory'
            Assert-Installer ($shortcut.WorkingDirectory -eq $customInstall) 'Shortcut working directory matches the selected installation directory'
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        }
    } finally { [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    $sentinelPath = Join-Path $customInstall 'user-file-keep.txt'
    [System.IO.File]::WriteAllText($sentinelPath,'Uninstall must preserve this unrelated user file.')
    Run-InstallerProcess (Join-Path $customInstall 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="{0}"' -f (Join-Path $testRoot 'uninstall-selected.log')))
    Wait-UntilRemoved $appPath
    Assert-Installer (-not (Test-Path -LiteralPath $appPath)) 'Uninstall removes the application payload'
    Assert-Installer (-not (Test-Path -LiteralPath $desktopLink)) 'Uninstall removes its desktop shortcut'
    Assert-Installer (-not (Test-Path -LiteralPath $startMenuLink)) 'Uninstall removes its Start menu shortcut'
    Assert-Installer (Test-Path -LiteralPath $sentinelPath) 'Uninstall preserves unrelated files in the installation directory'
    $slashInput = $noIconsInstall.Replace('\','/') + '/'
    Run-InstallerProcess $testSetup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="{0}"' -f $slashInput),'/MERGETASKS="!desktopicon,!startmenuicon"',('/LOG="{0}"' -f (Join-Path $testRoot 'install-no-shortcuts.log')))
    Assert-Installer (Test-Path -LiteralPath (Join-Path $noIconsInstall 'LumaDock.exe')) 'Installation also succeeds with both shortcut choices cleared'
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $noIconsInstall 'LumaDock\LumaDock.exe'))) 'A selected existing LumaDock suffix is not duplicated'
    Assert-Installer (-not (Test-Path -LiteralPath $desktopLink)) 'Cleared desktop checkbox creates no shortcut'
    Assert-Installer (-not (Test-Path -LiteralPath $startMenuLink)) 'Cleared Start menu checkbox creates no shortcut'
    Run-InstallerProcess (Join-Path $noIconsInstall 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    Wait-UntilRemoved (Join-Path $noIconsInstall 'LumaDock.exe')
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $noIconsInstall 'LumaDock.exe'))) 'Shortcut-free installation can be uninstalled cleanly'
    $duplicateInput = Join-Path $testRoot 'Repeated browse\LumaDock\LumaDock'
    $normalizedInstall = Join-Path $testRoot 'Repeated browse\LumaDock'
    Run-InstallerProcess $testSetup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="{0}"' -f $duplicateInput),'/MERGETASKS="!desktopicon,!startmenuicon"')
    Assert-Installer (Test-Path -LiteralPath (Join-Path $normalizedInstall 'LumaDock.exe')) 'Repeated trailing LumaDock folders are normalized to a single child'
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $duplicateInput 'LumaDock.exe'))) 'Repeated browse does not create nested LumaDock installation folders'
    Run-InstallerProcess (Join-Path $normalizedInstall 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    Wait-UntilRemoved (Join-Path $normalizedInstall 'LumaDock.exe')
    $testResults.Add(($testResults.Count.ToString() + ' installer checks passed without touching real desktop, Start menu, or uninstall registry.'))
    $testResults | Set-Content -LiteralPath (Join-Path $testRoot 'results.txt') -Encoding UTF8
    $testResults | Write-Output
} catch {
    $testResults.Add('FAIL ' + $_.Exception.Message)
    $testResults | Set-Content -LiteralPath (Join-Path $testRoot 'results.txt') -Encoding UTF8
    throw
}
