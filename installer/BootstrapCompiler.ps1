$ErrorActionPreference = 'Stop'
$toolDirectory = Join-Path $PSScriptRoot 'tools'
$portableDirectory = Join-Path $toolDirectory 'InnoSetup'
$compilerPath = Join-Path $portableDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compilerPath) { Write-Output $compilerPath; return }
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
$bootstrapPath = Join-Path $toolDirectory 'innosetup-7.1.1-x64.exe'
if (-not (Test-Path -LiteralPath $bootstrapPath)) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $bootstrapPath
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*O=Pyrsys B.V.*') {
    throw 'The Inno Setup bootstrap must have a valid Pyrsys B.V. Authenticode signature.'
}
$bootstrap = Start-Process -FilePath $bootstrapPath -ArgumentList @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',('/DIR="{0}"' -f $portableDirectory)) -WindowStyle Hidden -Wait -PassThru
if ($bootstrap.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compilerPath)) { throw 'Portable compiler bootstrap failed.' }
Write-Output $compilerPath
