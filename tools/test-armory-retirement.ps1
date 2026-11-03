# Disposable Windows runner only. Uses the production startup and post-update retirement.
# A prerelease package of this build exercises startup first, then a real Velopack package
# transition to the release. The published 0.3.3 Inno installer supplies the competing layout.
param([Parameter(Mandatory = $true)][string]$Version,
      [string]$ReleaseDir = 'dist/velopack')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'armory-shell-test.ps1')
$release = if ([IO.Path]::IsPathRooted($ReleaseDir)) { $ReleaseDir } else { Join-Path $root $ReleaseDir }
$install = Join-Path $env:LOCALAPPDATA 'IDEAArmory'
$exe = Join-Path $install 'current\IdeaArmory.exe'
$launcher = Join-Path $install 'IDEA Armory.exe'
$data = Join-Path $env:LOCALAPPDATA 'IDEA Armory'
$legacy = Join-Path $env:LOCALAPPDATA 'Programs\IDEA Armory'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'IDEA Armory.lnk'
$pins = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$pin = Join-Path $pins 'Armory retirement smoke.lnk'
$id = 'IdeaBosco.Armory'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runOnce = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
$machineRun = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$scratch = Join-Path $env:RUNNER_TEMP 'armory-retirement-smoke'
$evidence = Join-Path $root 'evidence'
[void][IO.Directory]::CreateDirectory($evidence)
$report = Join-Path $evidence 'install-retirement.txt'
Set-Content -LiteralPath $report -Value ('Measured ' + [DateTime]::UtcNow.ToString('O'))
function Note([string]$s) { Write-Output $s; Add-Content -LiteralPath $report -Value $s }
function Require([bool]$ok, [string]$s) { if (-not $ok) { Note ('FAIL: ' + $s); throw $s }; Note ('PASS: ' + $s) }
function Wait-For([scriptblock]$condition, [string]$message) {
    for ($i = 0; $i -lt 90; $i++) { if (& $condition) { return }; Start-Sleep -Seconds 1 }
    throw $message
}
function Quit {
    if (Test-Path -LiteralPath $exe) { & $exe --quit | Out-Null }
    Wait-For { @(Get-Process -Name IdeaArmory -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -eq 0 } 'The surviving Armory did not quit.'
}
function Link([string]$file, [string]$target) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $file))
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($file); $link.TargetPath = $target; $link.Save()
}
$oldSetup = Join-Path $scratch 'IDEA-Armory-Setup-v0.3.3.exe'
[void][IO.Directory]::CreateDirectory($scratch)
Invoke-WebRequest 'https://github.com/pina-hash/idea-armory/releases/download/v0.3.3/IDEA-Armory-Setup-v0.3.3.exe' -OutFile $oldSetup
$loose = Join-Path $scratch 'unpacked zip\files'
$machine = Join-Path $env:ProgramFiles 'IDEA Armory retirement smoke'
$orphan = $legacy + '.old'
$taskName = 'Armory retirement smoke'
$machineApp = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ArmoryRetirementSmoke'
$staleLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Old Armory smoke.lnk'
$looseLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Loose Armory smoke.lnk'
$machineLink = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Machine Armory smoke.lnk'
$proofs = @{}
function Proof([string]$path, [string]$text) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path, $text)
    $script:proofs[$path] = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
}
function Seed {
    # A real older installer, including its real uninstaller and automatic launch.
    $p = Start-Process $oldSetup -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    Require ($p.ExitCode -eq 0) 'published 0.3.3 installed beside current'
    Wait-For { @(Get-Process -Name IdeaArmory -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $legacy 'IdeaArmory.exe') }).Count -gt 0 } 'The older install did not run.'
    Proof (Join-Path $legacy 'student-work.sldprt') 'untracked student work in the old program folder'
    foreach ($folder in @($loose, $machine, $orphan)) {
        [void][IO.Directory]::CreateDirectory($folder)
        Copy-Item -LiteralPath (Join-Path $legacy 'IdeaArmory.exe') -Destination $folder -Force
        Proof (Join-Path $folder 'student-work.txt') ('keep at ' + $folder)
        Proof (Join-Path $folder 'settings.json') '{"localSetting":"keep"}'
    }
    New-Item -Path ('HKCU:\Software\Classes\AppUserModelId\velopack.IDEAArmory') -Force | Out-Null
    Link $pin (Join-Path $legacy 'IdeaArmory.exe')
    Link $staleLink (Join-Path $legacy 'IdeaArmory.exe')
    Link $looseLink (Join-Path $loose 'IdeaArmory.exe')
    Link $machineLink (Join-Path $machine 'IdeaArmory.exe')
    foreach ($key in @($run, $runOnce, $machineRun)) { [void](New-Item -Path $key -Force) }
    New-ItemProperty -LiteralPath $run -Name ArmoryOldAlias -Value ('"' + (Join-Path $legacy 'IdeaArmory.exe') + '" --background') -Force | Out-Null
    New-ItemProperty -LiteralPath $runOnce -Name ArmoryZipAlias -Value ('"' + (Join-Path $loose 'IdeaArmory.exe') + '" --background') -Force | Out-Null
    New-ItemProperty -LiteralPath $machineRun -Name ArmoryMachineAlias -Value ('"' + (Join-Path $machine 'IdeaArmory.exe') + '" --background') -Force | Out-Null
    New-Item -Path $machineApp -Force | Out-Null
    New-ItemProperty -LiteralPath $machineApp -Name DisplayName -Value 'IDEA Armory' -Force | Out-Null
    New-ItemProperty -LiteralPath $machineApp -Name InstallLocation -Value $machine -Force | Out-Null
    $action = New-ScheduledTaskAction -Execute (Join-Path $loose 'IdeaArmory.exe') -Argument '--background'
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger (New-ScheduledTaskTrigger -AtLogOn) -Force | Out-Null
}
function Assert-One([string]$phase) {
    Wait-For { -not (Test-Path -LiteralPath (Join-Path $legacy 'IdeaArmory.exe')) -and (Test-Path -LiteralPath $menu) -and
        [ArmoryPinReader]::Target($menu) -ieq $launcher } ('Retirement did not complete ' + $phase)
    Require (Test-Path -LiteralPath $launcher) ($phase + ': stable root launcher exists')
    foreach ($folder in @($legacy, $loose, $machine, $orphan)) {
        Require (-not (Test-Path -LiteralPath (Join-Path $folder 'IdeaArmory.exe'))) ($phase + ': retired executable at ' + $folder)
    }
    foreach ($key in @($run, $runOnce, $machineRun)) {
        $values = Get-ItemProperty -LiteralPath $key
        foreach ($name in @('ArmoryOldAlias', 'ArmoryZipAlias', 'ArmoryMachineAlias')) { Require (-not $values.$name) ($phase + ': no ' + $name + ' in ' + $key) }
    }
    Require (-not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) ($phase + ': scheduled task removed')
    foreach ($file in @($staleLink, $looseLink, $machineLink)) { Require (-not (Test-Path -LiteralPath $file)) ($phase + ': retired shortcut ' + $file) }
    foreach ($file in @($menu, $pin)) {
        Require ([ArmoryPinReader]::Target($file) -ieq $launcher) ($phase + ': shortcut uses root launcher ' + $file)
        Require ([ArmoryPinReader]::AppId($file) -ceq $id) ($phase + ': shortcut identity ' + $file)
    }
    foreach ($file in $proofs.Keys) { Require ((Test-Path -LiteralPath $file) -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($file)) -ceq $proofs[$file]) ($phase + ': unchanged bytes at ' + $file) }
    Require (-not (Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{28A1D010-82E3-4294-9676-83AC0AA1F5D3}_is1')) ($phase + ': legacy Apps entry removed')
    Require (-not (Test-Path -LiteralPath $machineApp)) ($phase + ': machine Apps entry removed')
    $apps = @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match '^IDEA Armory(?:\s+\d.*)?$' })
    Require ($apps.Count -eq 1) ($phase + ': exactly one Armory Apps entry')
    Require (@(Get-Process -Name IdeaArmory -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $exe }).Count -eq 0) ($phase + ': no competing process')
}
function Assert-Window {
    Wait-For { @(Get-Process -Name IdeaArmory -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 }).Count -eq 1 } 'The surviving window did not appear.'
    $window = (Get-Process -Name IdeaArmory | Where-Object { $_.Path -eq $exe }).MainWindowHandle
    Require ([ArmoryPinReader]::WindowProperty($window, 5) -ceq $id) 'running window has the shortcut AppUserModelID'
    Require ([ArmoryPinReader]::WindowProperty($window, 2) -ceq ('"' + $launcher + '"')) 'pinning the running window uses the stable relaunch command'
    Require (-not (Test-Path -LiteralPath 'HKCU:\Software\Classes\AppUserModelId\velopack.IDEAArmory')) 'only the canonical AppUserModelID registration remains'
}
try {
    # Keep the existing production settings byte for byte while retirement runs.
    Proof (Join-Path $data 'settings.json') '{"vaultRoot":"C:\\IDEA\\Armory","startAtSignIn":false,"theme":"dark"}'
    Proof (Join-Path $data 'secrets\retirement-proof.bin') 'local settings and sign-in proof'
    Proof 'C:\IDEA\Armory\retirement-proof.sldprt' 'team folder proof'
    $baseline = Join-Path $scratch 'baseline'
    & (Join-Path $PSScriptRoot 'package-velopack.ps1') -Version ($Version + '-retirement-smoke.1') -PayloadDir (Join-Path $root ('dist/IDEA-Armory-USB-v' + $Version + '/files')) -OutDir $baseline
    $p = Start-Process (Join-Path $baseline 'IDEAArmory-win-Setup.exe') -ArgumentList '--silent' -Wait -PassThru
    Require ($p.ExitCode -eq 0) 'baseline package installed'
    Quit
    Seed
    Start-Process -FilePath $launcher | Out-Null
    Assert-One 'after startup'
    Assert-Window
    Quit
    Seed
    $package = Join-Path $release ('IDEAArmory-' + $Version + '-full.nupkg')
    # A real newer package, applied by the same updater and hooks as ApplyOnQuit.
    $u = Start-Process (Join-Path $install 'Update.exe') -ArgumentList ('--silent apply --package "' + $package + '" --norestart') -Wait -PassThru
    Require ($u.ExitCode -eq 0) 'Velopack applied the release over the prerelease package'
    Assert-One 'after update callback'
    Start-Process -FilePath $pin | Out-Null
    Assert-Window
    Assert-One 'after updated start through the pin'
    $said = Get-Content -LiteralPath (Join-Path $data 'logs\agent.log') -Raw
    Require ($said -match 'retire: .*ran uninstaller') 'flight audit includes the real older uninstaller'
    Require ($said -match 'retire: .*removed task') 'flight audit includes removed auto-start task'
    Require ($said -match 'retire: .*retired install') 'flight audit includes retired install paths'
    Require (-not (Get-ItemProperty -LiteralPath $run -Name 'IDEA Armory' -ErrorAction SilentlyContinue)) 'StartAtSignIn=false stayed off'
    Quit
    Note 'PASS: one runnable Armory, one shell identity, stable pin before and after a real package update.'
} finally {
    Quit
    if (Test-Path -LiteralPath (Join-Path $install 'Update.exe')) { Start-Process (Join-Path $install 'Update.exe') -ArgumentList '--silent --uninstall' -Wait | Out-Null }
    Remove-Item -LiteralPath $pin -Force -ErrorAction SilentlyContinue
    # Evidence includes the full retirement audit even if an assertion failed.
    Copy-Item -LiteralPath (Join-Path $data 'logs\agent.log') -Destination (Join-Path $evidence 'retirement-agent.log') -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath (Join-Path $data 'logs\install-retirement.jsonl') -Destination $evidence -ErrorAction SilentlyContinue
}
