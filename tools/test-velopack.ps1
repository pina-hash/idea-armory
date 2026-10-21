# The install that updates itself, end to end on a Windows runner (0.4.0): IDEAArmory-win-Setup.exe
# installs silently for this account with no administrator, the installed IdeaArmory.exe answers
# --check with the release's version, and Velopack's uninstall removes the app and leaves the vault.
# Runs under PowerShell 7 on Windows.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ReleaseDir = 'dist/velopack'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = if ([IO.Path]::IsPathRooted($ReleaseDir)) { $ReleaseDir } else { Join-Path $root $ReleaseDir }
$setup = Join-Path $dir 'IDEAArmory-win-Setup.exe'
$installRoot = Join-Path $env:LOCALAPPDATA 'IDEAArmory'
$exe = Join-Path $installRoot 'current/IdeaArmory.exe'
$vault = Join-Path $env:SystemDrive 'IDEA/Armory'
[void][IO.Directory]::CreateDirectory($vault)
$proof = Join-Path $vault 'velopack-proof.txt'
Set-Content -LiteralPath $proof -Value 'kept' -Encoding utf8

$p = Start-Process -FilePath $setup -ArgumentList '--silent' -PassThru -Wait
if ($p.ExitCode -ne 0) { throw ('The setup exited with ' + $p.ExitCode + '.') }
for ($i = 0; $i -lt 60 -and -not (Test-Path -LiteralPath $exe); $i++) { Start-Sleep -Seconds 1 }
if (-not (Test-Path -LiteralPath $exe)) { throw ('No ' + $exe + ' after the setup.') }
if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'Update.exe'))) { throw 'No Update.exe beside current.' }
# Armory may have started after the setup: ask it to quit before checking.
& $exe --quit | Out-Null
$check = & $exe --check | Select-Object -Last 1
Write-Output ('--check: ' + $check)
$answer = $check | ConvertFrom-Json
if ($answer.version -ne $Version) { throw ('The installed Armory says ' + $answer.version + ', not ' + $Version + '.') }

# Uninstall through Velopack's own updater; the vault and its files stay.
$u = Start-Process -FilePath (Join-Path $installRoot 'Update.exe') -ArgumentList '--uninstall', '--silent' -PassThru -Wait
Write-Output ('Update.exe --uninstall exited ' + $u.ExitCode)
if (Test-Path -LiteralPath $exe) { Write-Output '::warning::IdeaArmory.exe is still there after the uninstall.' }
if ((Get-Content -LiteralPath $proof -Raw).Trim() -ne 'kept') { throw 'The uninstall touched the vault.' }
Write-Output 'Velopack install, --check and uninstall: OK; the vault kept its files.'
