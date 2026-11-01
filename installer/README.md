# Windows installer

The offline EXE is compiled with official Inno Setup 7.1.1. The wizard uses a Korean interface, a dark navy theme, and custom mint monitor artwork.

- Default installation folder is `C:\LumaDock`, as requested. The production installer requests administrator privileges so this root-level folder works reliably.
- Browse selects a parent folder and automatically appends `LumaDock`. Example: `D:\Apps` becomes `D:\Apps\LumaDock`. Selecting an existing `LumaDock` folder or browsing repeatedly does not duplicate the suffix. The final effective path is normalized before leaving the directory page and appears in the installation summary.
- Desktop and Start menu shortcuts are independent checkboxes. Desktop starts unchecked; Start menu starts checked.
- The completion page has a checked **LumaDock 실행** option; uncheck it to finish without launching.
- Completion launch explicitly uses Inno Setup's `runasoriginaluser` flag so a normally started installer launches the app as the original desktop user after elevation.
- Installs the normal Windows uninstall entry and an uninstaller. Removing the app does not recursively delete unrelated files from the selected directory or remove user widget preferences.

## Build

First build the application and `assets/MonitorControl.ico`, then run:

```powershell
.\build-installer.ps1
.\build-installer.ps1 -Test
```

Output: `artifacts/LumaDock-1.0.3.exe`.

The compiler is a build dependency only. The produced installer contains the application and does not download anything at installation time. Inno Setup's optional official portable mode can be used so the compiler need not be installed on the system:

```powershell
# Download the signed installer from https://jrsoftware.org/isdl.php and verify its Authenticode signature first.
$compilerFolder = Join-Path $PWD 'installer\tools\InnoSetup'
Start-Process .\installer\tools\innosetup-7.1.1-x64.exe -ArgumentList @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',('/DIR="{0}"' -f $compilerFolder)) -WindowStyle Hidden -Wait
```

Compiler bootstrap tested here: official GitHub release linked by JRSoftware; Authenticode status **Valid**, signer **Pyrsys B.V.**. Compiler files reside only in `installer/tools` and should not be committed.

`installer/BootstrapCompiler.ps1` automates that pinned download, signature check, and portable bootstrap. Run it explicitly when setting up a new build machine.

## Validation

`TestInstaller.ps1` compiles a separate test installer with a distinct identity, no uninstall registry registration, and Desktop/Start menu paths redirected inside `artifacts/installer-tests`. It installs into a custom parent containing spaces and Korean characters and verifies the `LumaDock` child; tests an already-suffixed folder and a repeated suffix; verifies the EXE, configuration, uninstaller, both shortcut targets, and shortcut working directories; tests both selected and deselected shortcut choices; then uninstalls and confirms shortcut/application cleanup while preserving an unrelated user file.

These tests never install into the user's real Desktop, Start menu, AppData program directory, or uninstall registry. The release's normal Windows registration and interactive checkbox rendering rely on standard Inno Setup functionality and are not exercised against the user's system by automated tests. The launch option is intentionally skipped in silent tests, so tests do not change physical monitor settings.

References: [Inno Setup](https://jrsoftware.org/isinfo.php), [portable mode](https://jrsoftware.org/ishelp/topic_technotes.htm), [wizard styling](https://jrsoftware.org/ishelp/topic_setup_wizardstyle.htm), [command-line options](https://jrsoftware.org/ishelp/topic_setupcmdline.htm).
