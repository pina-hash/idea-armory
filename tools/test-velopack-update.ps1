# A taskbar pin survives an update, end to end on a Windows runner (0.4.3, docs/agent/INSTALL.md
# "Taskbar pins"). Installs the PUBLISHED v<From> with its own IDEAArmory-win-Setup.exe, pins
# Armory the way Windows does (a copy of the Start menu shortcut in User Pinned\TaskBar) and adds
# a pin of a copy that is gone (what the move off the older install left), then applies this
# build's full package with Velopack's own Update.exe, exactly as an update on a lab computer
# does. After it: the app is still at the same path and answers the new version, the Start menu
# shortcut and both pins point at it with IdeaBosco.Armory (the identity the window has), another
# program's pin is untouched, and the first start records the update (install.json) and writes
# "updated" in its log. Then Velopack's uninstall, which keeps the vault. Prints what it read of
# v<From>'s shortcut, the identity that broke the pins. Runs under PowerShell 7 on Windows.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$From = '0.4.2',
    [string]$ReleaseDir = 'dist/velopack'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = if ([IO.Path]::IsPathRooted($ReleaseDir)) { $ReleaseDir } else { Join-Path $root $ReleaseDir }
$package = Join-Path $dir ('IDEAArmory-' + $Version + '-full.nupkg')
if (-not (Test-Path -LiteralPath $package)) { throw ('No ' + $package + '. Run tools/package-velopack.ps1 first.') }
$Aumid = 'IdeaBosco.Armory'
$installRoot = Join-Path $env:LOCALAPPDATA 'IDEAArmory'
$exe = Join-Path $installRoot 'current\IdeaArmory.exe'
$update = Join-Path $installRoot 'Update.exe'
$launcher = Join-Path $installRoot 'IDEA Armory.exe'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'IDEA Armory.lnk'
$pins = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$data = Join-Path $env:LOCALAPPDATA 'IDEA Armory'
$vault = Join-Path $env:SystemDrive 'IDEA\Armory'
$evidence = Join-Path $root 'evidence'
[void][IO.Directory]::CreateDirectory($evidence)
$report = Join-Path $evidence 'velopack-update.txt'
Set-Content -LiteralPath $report -Value ('Taskbar pins across an update from v' + $From + ' to ' + $Version) -Encoding utf8
function Note([string]$line) { Write-Output $line; Add-Content -LiteralPath $report -Value $line -Encoding utf8 }
function Fail([string]$line) { Note ('FAIL: ' + $line); throw $line }

. (Join-Path $PSScriptRoot 'armory-shell-test.ps1')
function Describe([string]$lnk) { [ArmoryPinReader]::Target($lnk) + ' (' + [ArmoryPinReader]::AppId($lnk) + ')' }
function Same([string]$a, [string]$b) { [IO.Path]::GetFullPath($a).TrimEnd('\') -ieq [IO.Path]::GetFullPath($b).TrimEnd('\') }
function Quit-Armory {
    if (Test-Path -LiteralPath $exe) { & $exe --quit | Out-Null }
    for ($i = 0; $i -lt 30 -and (Get-Process -Name IdeaArmory -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Seconds 1 }
    Get-Process -Name IdeaArmory -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# 1. The published v<From>, installed the way a lab computer got it.
$old = Join-Path $env:RUNNER_TEMP ('armory-v' + $From)
[void][IO.Directory]::CreateDirectory($old)
$oldSetup = Join-Path $old 'IDEAArmory-win-Setup.exe'
Invoke-WebRequest -Uri ('https://github.com/pina-hash/idea-armory/releases/download/v' + $From + '/IDEAArmory-win-Setup.exe') -OutFile $oldSetup
$p = Start-Process -FilePath $oldSetup -ArgumentList '--silent' -PassThru -Wait
if ($p.ExitCode -ne 0) { Fail ('The v' + $From + ' setup exited with ' + $p.ExitCode + '.') }
for ($i = 0; $i -lt 60 -and -not (Test-Path -LiteralPath $exe); $i++) { Start-Sleep -Seconds 1 }
if (-not (Test-Path -LiteralPath $exe)) { Fail ('No ' + $exe + ' after the v' + $From + ' setup.') }
# Armory itself stamps its identity on the Start menu shortcut at start, as on a real computer:
# v<From> is started (the silent setup does not start it) and runs until it has.
$oldLog = Join-Path $data 'logs\agent.log'
Start-Process -FilePath $exe -ArgumentList '--background' | Out-Null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    if ((Test-Path -LiteralPath $oldLog) -and ((Get-Content -LiteralPath $oldLog -Raw) -match 'Start menu shortcut')) { break }
}
Start-Sleep -Seconds 2
Quit-Armory
$before = (& $exe --check | Select-Object -Last 1 | ConvertFrom-Json).version
if ($before -ne $From) { Fail ('The installed Armory says ' + $before + ', not ' + $From + '.') }
if (-not (Test-Path -LiteralPath $startMenu)) { Fail ('No Start menu shortcut at ' + $startMenu + '.') }
Note ('v' + $From + ' Start menu shortcut: ' + (Describe $startMenu))
# What made pins blank: Velopack's own identity on the shortcut, not the window's.
$oldId = [ArmoryPinReader]::AppId($startMenu)
Note ('v' + $From + ' shortcut identity: ' + $oldId + ' (the window runs as ' + $Aumid + ')')

# 2. Pins: Armory pinned from Start (a copy of its shortcut, as Windows makes it), a pin of a copy
# that is gone, and another program's.
[void][IO.Directory]::CreateDirectory($pins)
$pinned = Join-Path $pins 'IDEA Armory.lnk'
Copy-Item -LiteralPath $startMenu -Destination $pinned -Force
$shell = New-Object -ComObject WScript.Shell
$goneFolder = Join-Path $env:LOCALAPPDATA 'Programs\IDEA Armory'
[void][IO.Directory]::CreateDirectory($goneFolder)
$goneExe = Join-Path $goneFolder 'IdeaArmory.exe'
Copy-Item -LiteralPath $exe -Destination $goneExe -Force
$gonePin = Join-Path $pins 'IDEA Armory (old).lnk'
$link = $shell.CreateShortcut($gonePin); $link.TargetPath = $goneExe; $link.Save()
Remove-Item -LiteralPath $goneFolder -Recurse -Force
$otherPin = Join-Path $pins 'Notepad (test).lnk'
$link = $shell.CreateShortcut($otherPin); $link.TargetPath = (Join-Path $env:WINDIR 'notepad.exe'); $link.Save()
$otherBytes = [IO.File]::ReadAllBytes($otherPin)
foreach ($lnk in @($pinned, $gonePin, $otherPin)) { Note ('pin before: ' + (Split-Path -Leaf $lnk) + ' -> ' + (Describe $lnk)) }

# 3. The update, the way Armory applies one: Velopack's Update.exe and this build's full package.
$u = Start-Process -FilePath $update -ArgumentList '--silent', 'apply', '--package', $package, '--norestart' -PassThru -Wait
Note ('Update.exe apply exited ' + $u.ExitCode)
if ($u.ExitCode -ne 0) { Fail ('Update.exe apply exited with ' + $u.ExitCode + '.') }
Start-Sleep -Seconds 3
Quit-Armory
# What the update itself wrote, before the new version has started (Velopack's rewrite, and
# Armory's install-step repair of the pins).
foreach ($lnk in @($startMenu, $pinned, $gonePin)) { Note ('right after the update: ' + (Split-Path -Leaf $lnk) + ' -> ' + (Describe $lnk)) }
if (-not (Test-Path -LiteralPath $exe)) { Fail ('The app is no longer at ' + $exe + ' after the update.') }
$after = (& $exe --check | Select-Object -Last 1 | ConvertFrom-Json).version
if ($after -ne $Version) { Fail ('After the update the app says ' + $after + ', not ' + $Version + '.') }
Note ('after the update: ' + $exe + ' answers ' + $after + ' (the same path as before)')

# 4. The first start of the new version: its own repair, and the record of the update.
Start-Process -FilePath $exe -ArgumentList '--background' | Out-Null
$log = Join-Path $data 'logs\agent.log'
$started = $false
for ($i = 0; $i -lt 60 -and -not $started; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path -LiteralPath $log) { $started = (Get-Content -LiteralPath $log -Raw) -match ('install: Version ' + [regex]::Escape($Version) + ', updated') }
}
Start-Sleep -Seconds 5
Quit-Armory
$said = if (Test-Path -LiteralPath $log) { @((Get-Content -LiteralPath $log) | Where-Object { $_ -match 'install:|taskbar pin|shortcut' }) } else { @() }
foreach ($line in $said) { Note ('agent.log: ' + $line) }
if (-not $started) { Fail ('The first start of ' + $Version + ' did not log "install: Version ' + $Version + ', updated".') }
$record = Get-Content -LiteralPath (Join-Path $data 'install.json') -Raw | ConvertFrom-Json
Note ('install.json: version ' + $record.version + ', updated ' + $record.updated + ', since ' + $record.since)
if ($record.version -ne $Version -or -not $record.updated) { Fail 'install.json does not record the update.' }

# 5. Every pin of Armory and the Start menu shortcut: this exe, Armory's identity. Not another's.
foreach ($lnk in @($startMenu, $pinned, $gonePin)) {
    $target = [ArmoryPinReader]::Target($lnk); $id = [ArmoryPinReader]::AppId($lnk)
    Note ('after: ' + (Split-Path -Leaf $lnk) + ' -> ' + $target + ' (' + $id + ')')
    if (-not (Same $target $launcher)) { Fail ((Split-Path -Leaf $lnk) + ' points at ' + $target + ', not ' + $launcher + '.') }
    if ($id -cne $Aumid) { Fail ((Split-Path -Leaf $lnk) + ' has the identity "' + $id + '", not ' + $Aumid + '.') }
}
$otherNow = [IO.File]::ReadAllBytes($otherPin)
if ([Convert]::ToBase64String($otherNow) -ne [Convert]::ToBase64String($otherBytes)) { Fail 'Another program''s pin was changed.' }
if ([ArmoryPinReader]::AppId($otherPin)) { Fail 'Another program''s pin was given an identity.' }
Note ('another program''s pin: unchanged, ' + (Describe $otherPin))

# 6. Uninstall; the vault stays.
[void][IO.Directory]::CreateDirectory($vault)
$proof = Join-Path $vault 'velopack-update-proof.txt'
Set-Content -LiteralPath $proof -Value 'kept' -Encoding utf8
$x = Start-Process -FilePath $update -ArgumentList '--uninstall', '--silent' -PassThru -Wait
Note ('Update.exe --uninstall exited ' + $x.ExitCode)
if ((Get-Content -LiteralPath $proof -Raw).Trim() -ne 'kept') { Fail 'The uninstall touched the vault.' }
foreach ($lnk in @($pinned, $gonePin, $otherPin)) { Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue }
Note ('Taskbar pins across an update from v' + $From + ' to ' + $Version + ': OK.')
