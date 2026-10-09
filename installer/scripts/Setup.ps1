# IDEA Armory setup. Runs under Windows PowerShell 5.1 (Windows 10 and 11) and PowerShell 7.
# Every mode but Badges never needs administrator rights: everything it writes is inside the
# profile and the registry (HKCU) of the Windows user who runs it, so each Windows account
# needs one run.
#
#   -Mode Install        copy the app to %LOCALAPPDATA%\Programs\IDEA Armory, add the Start menu
#                        shortcut, start at sign-in, the Apps entry and the notification and link
#                        registration, then start it in the tray (which writes File Explorer's
#                        right-click items itself)
#   -Mode Uninstall      remove all of that, every per-user key Armory writes, and
#                        %LOCALAPPDATA%\IDEA Armory (settings, logs, this computer's sign-in,
#                        WebView2 cache)
#   -Mode Check          read-only report; changes nothing
#   -Mode Stop           (IDEA-Armory-Setup.exe) close a running IDEA Armory from this install
#   -Mode InnoUninstall  (IDEA-Armory-Setup.exe's uninstaller) close it, then remove
#                        %LOCALAPPDATA%\IDEA Armory, the start at sign-in entry and every
#                        per-user key Armory writes
#   -Mode Badges         OPTIONAL, for the whole computer: run <Source>\badges\
#                        IDEA-Armory-Badges-Setup.exe as an administrator (Windows asks for the
#                        password), so Armory's status shows on file icons for every account
#
# No mode ever deletes, moves or writes the vault folder (C:\IDEA\Armory, or the vaultRoot in
# settings.json) or anything inside it. Deletion refuses any folder that is a vault, is inside
# one, or holds a vault's .armory folder, and it never follows a junction or symbolic link. No
# mode writes HKLM itself; only the badges setup does, with its own uninstaller.
#
# -Source <folder>  the "files" folder that holds IdeaArmory.exe (Install, Badges; Check reports it)
# -LogDir <folder>  also append one line per run to <folder>\<COMPUTERNAME>.txt (the flash drive)
# Ends with one large PASS or FAIL line and exit code 0 (PASS) or 1 (FAIL).
param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Uninstall', 'Check', 'Stop', 'InnoUninstall', 'Badges')][string]$Mode,
    [string]$Source = '',
    [string]$LogDir = ''
)
$ErrorActionPreference = 'Stop'
# Started from PowerShell 7 (a terminal, a CI runner), Windows PowerShell 5.1 inherits
# PowerShell 7's module folders ahead of its own and then cannot load Get-CimInstance and
# similar commands. Windows PowerShell keeps only its own module folders, its own first.
if ($PSVersionTable.PSEdition -ne 'Core') {
    $ownModules = Join-Path $PSHOME 'Modules'
    $keptModules = @($env:PSModulePath -split ';' | Where-Object { $_ -and ($_ -notmatch '\\PowerShell\\(7[^\\]*\\)?Modules\\?$') -and ($_ -ne $ownModules) })
    $env:PSModulePath = (@($ownModules) + $keptModules) -join ';'
}
$Version = '__VERSION__'
if ($Version -eq ('__' + 'VERSION__')) { $Version = 'dev' }
$AppName = 'IDEA Armory'
$Publisher = 'IDEA, Don Bosco Tech'
$ExeName = 'IdeaArmory.exe'
# Must match AppGuid in installer\IdeaArmory.iss (tools\package-agent.ps1 checks it).
$InnoAppId = '{28A1D010-82E3-4294-9676-83AC0AA1F5D3}'
$WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$WebView2Page = 'https://developer.microsoft.com/microsoft-edge/webview2/consumer/'
$DefaultVault = 'C:\IDEA\Armory'
$Local = [Environment]::GetFolderPath('LocalApplicationData')
$Target = Join-Path $Local 'Programs\IDEA Armory'
$Exe = Join-Path $Target $ExeName
$DataDir = Join-Path $Local 'IDEA Armory'
$AgentLog = Join-Path $DataDir 'logs\agent.log'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'IDEA Armory'
$RunCommand = '"' + $Exe + '" --background'
$UninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$UninstallKey = $UninstallRoot + '\IDEA Armory'
$InnoKey = $UninstallRoot + '\' + $InnoAppId + '_is1'
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'IDEA Armory.lnk'
$ManifestName = 'scripts\payload.sha256'
# Must match ShellIdentity.AppId and the Aumid in installer\IdeaArmory.iss (tools\package-agent.ps1
# checks it): the name Windows gives Armory's notifications.
$Aumid = 'IdeaBosco.Armory'
# Every per-user key Armory or this setup writes, relative to HKCU (docs/agent/INSTALL.md):
# the notification registration, the idea-armory: link scheme, File Explorer's right-click items
# (written by IdeaArmory.exe when it starts) and the badges' heartbeat. Uninstall removes them all.
$IdentityKey = 'Software\Classes\AppUserModelId\' + $Aumid
$SchemeKey = 'Software\Classes\idea-armory'
$VerbKeys = @('Software\Classes\AllFilesystemObjects\shell\IDEAArmory', 'Software\Classes\IDEAArmory.Menu',
    'Software\Classes\Directory\Background\shell\IDEAArmory', 'Software\Classes\IDEAArmory.BackgroundMenu')
$PerUserKeys = @($IdentityKey, $SchemeKey) + $VerbKeys + @('Software\IDEA Armory')
# The optional badges (docs/agent/EXPLORER.md): their setup, and what it records for the computer.
$BadgesSetupName = 'badges\IDEA-Armory-Badges-Setup.exe'
$BadgesKey = 'SOFTWARE\IDEA Armory\Badges'
$OverlaysKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
$BadgeNames = @(' IDEAArmory1Attention', ' IDEAArmory2Mine', ' IDEAArmory3Locked', ' IDEAArmory4Synced')
$OverlayLimit = 11
if (-not $Source) { $Source = Split-Path -Parent $PSScriptRoot }
$Watch = [Diagnostics.Stopwatch]::StartNew()
$Notes = New-Object System.Collections.Generic.List[string]
$script:Vaults = @($DefaultVault)
$script:Kept = New-Object System.Collections.Generic.List[string]
$script:Left = New-Object System.Collections.Generic.List[string]
# Never hold the program folder open as the current directory while it is being replaced.
try { Set-Location -LiteralPath ([IO.Path]::GetTempPath()); [Environment]::CurrentDirectory = [IO.Path]::GetTempPath() } catch {}

function Say([string]$text) { Write-Host ('  ' + $text) }
function Banner([bool]$ok, [string]$reason) {
    $word = if ($ok) { 'PASS' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ''
    Write-Host ('=' * 64) -ForegroundColor $color
    Write-Host ('   ' + $word + '   ' + $reason) -ForegroundColor $color
    Write-Host ('=' * 64) -ForegroundColor $color
    Write-Host ''
}
function Get-UserName { return [Security.Principal.WindowsIdentity]::GetCurrent().Name }
function Get-WindowsName {
    try {
        $build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild
        if ($build -ge 22000) { return 'Windows11-' + $build } else { return 'Windows10-' + $build }
    } catch { return 'Windows' }
}
# One line per run on the flash drive: date, mode, result, version, user, Windows, seconds, reason.
function Write-DriveLog([string]$result, [string]$reason) {
    if (-not $LogDir) { return }
    try {
        [void][IO.Directory]::CreateDirectory($LogDir)
        $line = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0} {1} {2} v{3} user={4} windows={5} seconds={6:0.0} {7}',
            [DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture), $Mode.ToUpperInvariant(), $result, $Version, (Get-UserName), (Get-WindowsName),
            $Watch.Elapsed.TotalSeconds, $reason)
        [IO.File]::AppendAllText((Join-Path $LogDir ($env:COMPUTERNAME + '.txt')), $line + "`r`n", (New-Object Text.UTF8Encoding $false))
    } catch { Say ('(Could not write the log on the drive: ' + $_.Exception.Message + ')') }
}
function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
# The account that owns the desktop in this session (explorer.exe), or $null.
function Get-DesktopUser {
    try {
        $session = (Get-Process -Id $PID).SessionId
        $explorer = @(Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" -ErrorAction Stop | Where-Object { $_.SessionId -eq $session }) | Select-Object -First 1
        if (-not $explorer) { return $null }
        $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner -ErrorAction Stop
        if ($owner.ReturnValue -ne 0 -or -not $owner.User) { return $null }
        return ($owner.Domain + '\' + $owner.User)
    } catch { return $null }
}
# "Run as administrator" with another account's password would install for that account,
# not for the person signed in. Per-user setup refuses that instead of doing it silently.
function Assert-SameUser {
    if (-not (Test-Admin)) { return }
    $desktop = Get-DesktopUser
    $me = Get-UserName
    if ($desktop -and -not [string]::Equals($desktop, $me, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('This window runs as ' + $me + ', but ' + $desktop + ' is signed in. IDEA Armory installs for one Windows account at a time. Close this window and double-click the file again, without "Run as administrator".')
    }
}
function Get-WebView2 {
    $places = @(
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + $WebView2Client; Scope = 'for every user' },
        @{ Path = 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\' + $WebView2Client; Scope = 'for this Windows user' })
    foreach ($place in $places) {
        $pv = $null
        try { $pv = [string](Get-ItemProperty -LiteralPath $place.Path -Name pv -ErrorAction Stop).pv } catch {}
        if ($pv -and $pv -ne '0.0.0.0') { return (New-Object psobject -Property @{ Version = $pv; Scope = $place.Scope }) }
    }
    return $null
}
function Format-WebView2($runtime) {
    if ($runtime) { return ($runtime.Version + ' (' + $runtime.Scope + ')') }
    return ('MISSING. The Armory window needs it: ' + $WebView2Page)
}
function Get-VersionOf([string]$file) {
    try {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
        return ('{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart)
    } catch { return 'unknown' }
}
function Get-InstalledVersion {
    if (-not (Test-Path -LiteralPath $Exe)) { return $null }
    return (Get-VersionOf $Exe)
}
function Read-Settings {
    $file = Join-Path $DataDir 'settings.json'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { return (Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { $Notes.Add('settings.json could not be read, so only the default vault folder is known') ; return $null }
}
function Test-StartAtSignInOff($settings) {
    return ($settings -and ($settings.PSObject.Properties.Name -contains 'startAtSignIn') -and ($settings.startAtSignIn -eq $false))
}
# The vault folders to protect: the default one and the one in settings.json.
function Set-Vaults($settings) {
    $list = New-Object System.Collections.Generic.List[string]
    $list.Add($DefaultVault)
    if ($settings -and ($settings.PSObject.Properties.Name -contains 'vaultRoot') -and $settings.vaultRoot) {
        $candidate = ([string]$settings.vaultRoot).Trim().Replace('/', '\')
        try {
            if ([IO.Path]::IsPathRooted($candidate)) {
                $full = [IO.Path]::GetFullPath($candidate).TrimEnd('\')
                if (-not ($list -contains $full)) { $list.Add($full) }
            }
        } catch {}
    }
    $script:Vaults = $list.ToArray()
}
function Get-VaultRoot($settings) {
    if ($script:Vaults.Count -gt 1) { return $script:Vaults[1] }
    return $DefaultVault
}
# True when $Child is $Parent or inside it.
function Test-Within([string]$Child, [string]$Parent) {
    $c = $Child.TrimEnd('\') + '\'
    $p = $Parent.TrimEnd('\') + '\'
    return $c.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)
}
function Test-KeptInside([string]$Folder) {
    foreach ($kept in $script:Kept) { if (Test-Within $kept $Folder) { return $true } }
    return $false
}
function Invoke-Retry([scriptblock]$Action, [string]$On) {
    for ($i = 1; $i -le 20; $i++) {
        try { & $Action $On; return }
        catch {
            if ($i -eq 20) { $script:Left.Add($On + ' (' + $_.Exception.Message + ')'); return }
            Start-Sleep -Milliseconds 250
        }
    }
}
# Deletes a file or folder that belongs to this setup. Never enters or deletes a vault folder
# or anything inside one, never deletes a folder that holds a vault's .armory folder, and
# removes a junction or symbolic link itself without following it.
function Remove-Tree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    $full = $item.FullName.TrimEnd('\')
    foreach ($vault in $script:Vaults) {
        if (Test-Within $full $vault) { $script:Kept.Add($full); return }
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($item.PSIsContainer) { Invoke-Retry { param($p) [IO.Directory]::Delete($p, $false) } $full }
        else { Invoke-Retry { param($p) [IO.File]::Delete($p) } $full }
        return
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
        try { $item.Attributes = ($item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)) } catch {}
    }
    if (-not $item.PSIsContainer) { Invoke-Retry { param($p) [IO.File]::Delete($p) } $full; return }
    if (Test-Path -LiteralPath (Join-Path $full '.armory')) { $script:Kept.Add($full); return }
    $children = @()
    try { $children = @(Get-ChildItem -LiteralPath $full -Force) } catch { $script:Left.Add($full + ' (' + $_.Exception.Message + ')'); return }
    foreach ($child in $children) { Remove-Tree $child.FullName }
    $holdsVault = Test-KeptInside $full
    foreach ($vault in $script:Vaults) { if (Test-Within $vault $full) { $holdsVault = $true } }
    if ($holdsVault) { return }
    Invoke-Retry { param($p) [IO.Directory]::Delete($p, $false) } $full
}
# Runs a program hidden and waits at most $Seconds. Never throws.
function Invoke-Exe([string]$File, [string]$Arguments, [int]$Seconds) {
    $result = New-Object psobject -Property @{ ExitCode = $null; Output = ''; Error = ''; Problem = $null; Json = $null }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $File
    $info.Arguments = $Arguments
    $info.WorkingDirectory = Split-Path -Parent $File
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    try { $process = [Diagnostics.Process]::Start($info) } catch { $result.Problem = $_.Exception.Message; return $result }
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($Seconds * 1000)) {
        try { $process.Kill() } catch {}
        $result.Problem = 'it did not finish within ' + $Seconds + ' seconds'
        return $result
    }
    $result.ExitCode = $process.ExitCode
    if ($out.Wait(5000)) { $result.Output = [string]$out.Result }
    if ($err.Wait(5000)) { $result.Error = [string]$err.Result }
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line.Trim().StartsWith('{')) { try { $result.Json = $line.Trim() | ConvertFrom-Json } catch {}; break }
    }
    return $result
}
# Process ids of IdeaArmory.exe started from exactly this install's path (this user only:
# other accounts' copies live under their own profile, so their path never matches).
function Get-ArmoryProcesses {
    $ids = @()
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = 'IdeaArmory.exe'" -ErrorAction Stop)) {
            if ($p.ExecutablePath -and [string]::Equals([string]$p.ExecutablePath, $Exe, [StringComparison]::OrdinalIgnoreCase)) { $ids += [int]$p.ProcessId }
        }
    } catch {
        foreach ($p in @(Get-Process -Name 'IdeaArmory' -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $p.Path } catch {}
            if ($path -and [string]::Equals($path, $Exe, [StringComparison]::OrdinalIgnoreCase)) { $ids += $p.Id }
        }
    }
    return $ids
}
# The window's WebView2 helpers that use this user's cache folder; they exit soon after the app.
function Get-WebView2Helpers {
    $marker = Join-Path $DataDir 'WebView2'
    $ids = @()
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -ErrorAction Stop)) {
            if ($p.CommandLine -and $p.CommandLine.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $ids += [int]$p.ProcessId }
        }
    } catch {}
    return $ids
}
function Wait-Until([scriptblock]$Done, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (& $Done)) {
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 250
    }
    return $true
}
# Asks IDEA Armory to quit (IdeaArmory.exe --quit), waits a bounded time, and only then stops
# processes started from this exact IdeaArmory.exe. Returns how it went.
function Stop-Armory {
    if (@(Get-ArmoryProcesses).Count -eq 0) {
        [void](Wait-Until { @(Get-WebView2Helpers).Count -eq 0 } 5)
        return 'was not running'
    }
    Say 'Closing IDEA Armory...'
    if (Test-Path -LiteralPath $Exe) { [void](Invoke-Exe $Exe '--quit' 45) }
    $how = 'closed cleanly'
    if (-not (Wait-Until { @(Get-ArmoryProcesses).Count -eq 0 } 20)) {
        foreach ($id in @(Get-ArmoryProcesses)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
        if (-not (Wait-Until { @(Get-ArmoryProcesses).Count -eq 0 } 10)) {
            throw 'IDEA Armory is still running and could not be closed. Quit it from its icon near the clock, then run this again.'
        }
        $how = 'had to be stopped'
        $Notes.Add('IDEA Armory did not close when asked, so setup stopped it')
    }
    if (-not (Wait-Until { @(Get-WebView2Helpers).Count -eq 0 } 5)) {
        foreach ($id in @(Get-WebView2Helpers)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
        [void](Wait-Until { @(Get-WebView2Helpers).Count -eq 0 } 5)
    }
    return $how
}
function Get-LogLength {
    try { if (Test-Path -LiteralPath $AgentLog) { return (Get-Item -LiteralPath $AgentLog).Length } } catch {}
    return 0
}
function Read-LogSince([long]$offset) {
    try {
        $stream = New-Object IO.FileStream($AgentLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            if ($offset -gt $stream.Length) { $offset = 0 }
            [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
            return (New-Object IO.StreamReader($stream)).ReadToEnd()
        } finally { $stream.Dispose() }
    } catch { return '' }
}
# Starts IDEA Armory in the tray, as sign-in does. ShellExecute shares no handles with this
# window, so nothing that waits for this setup also waits for the app.
function Start-Armory {
    $offset = Get-LogLength
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = '--background'
    $info.WorkingDirectory = $Target
    $info.UseShellExecute = $true
    $process = [Diagnostics.Process]::Start($info)
    if ($process) { $process.Dispose() }
    $word = 'started'
    if ($Version -ne 'dev') { $word = 'started ' + $Version }
    $state = New-Object psobject -Property @{ Running = $false; Logged = $false }
    [void](Wait-Until {
            $state.Running = @(Get-ArmoryProcesses).Count -gt 0
            $state.Logged = (Read-LogSince $offset).Contains($word)
            $state.Running -and $state.Logged
        } 30)
    if (-not $state.Running) { $state.Running = @(Get-ArmoryProcesses).Count -gt 0 }
    return $state
}
function Read-Manifest([string]$root) {
    $file = Join-Path $root $ManifestName
    if (-not (Test-Path -LiteralPath $file)) { throw ('The file list ' + $ManifestName + ' is missing. Copy the whole folder from the ZIP again.') }
    $entries = @()
    foreach ($line in [IO.File]::ReadAllLines($file)) {
        if ($line -match '^([0-9a-fA-F]{64})  (.+)$') { $entries += (New-Object psobject -Property @{ Hash = $Matches[1].ToLowerInvariant(); Path = $Matches[2] }) }
    }
    if ($entries.Count -eq 0) { throw ('The file list ' + $ManifestName + ' is empty. Copy the whole folder from the ZIP again.') }
    return $entries
}
# SHA-256 of a file, lowercase hex, without Get-FileHash (a script command that Windows
# PowerShell 5.1 loads from its module path).
function Get-Sha256([string]$file) {
    $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
# Paths (relative) whose bytes are missing or differ from the package's file list.
function Test-Manifest([string]$root, $entries) {
    $bad = @()
    foreach ($entry in $entries) {
        $file = Join-Path $root $entry.Path
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $bad += $entry.Path; continue }
        $hash = Get-Sha256 $file
        if ($hash -ne $entry.Hash) { $bad += $entry.Path }
    }
    return $bad
}
# Renames a folder, retrying for a few seconds: a virus scan or the search indexer can hold
# a just-copied file for a moment. Rethrows the last error.
function Move-Folder([string]$From, [string]$To) {
    for ($i = 1; ; $i++) {
        try { [IO.Directory]::Move($From, $To); return }
        catch {
            if ($i -ge 20) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}
function Copy-Tree([string]$From, [string]$To) {
    $from = $From.TrimEnd('\')
    [void][IO.Directory]::CreateDirectory($To)
    foreach ($dir in @(Get-ChildItem -LiteralPath $from -Recurse -Directory -Force)) {
        [void][IO.Directory]::CreateDirectory((Join-Path $To $dir.FullName.Substring($from.Length + 1)))
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $from -Recurse -File -Force)) {
        [IO.File]::Copy($file.FullName, (Join-Path $To $file.FullName.Substring($from.Length + 1)), $true)
    }
}
function Get-RunValue {
    try { return [string](Get-ItemProperty -LiteralPath $RunKey -Name $RunName -ErrorAction Stop).$RunName } catch { return $null }
}
function Set-RunValue {
    if (-not (Test-Path -LiteralPath $RunKey)) { [void](New-Item -Path $RunKey) }
    [void](New-ItemProperty -LiteralPath $RunKey -Name $RunName -PropertyType String -Value $RunCommand -Force)
}
function Remove-RunValue { Remove-ItemProperty -LiteralPath $RunKey -Name $RunName -ErrorAction SilentlyContinue }
function New-StartMenuShortcut {
    try {
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Shortcut))
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($Shortcut)
        $link.TargetPath = $Exe
        $link.WorkingDirectory = $Target
        $link.IconLocation = $Exe + ',0'
        $link.Description = 'IDEA Armory: the team and class CAD vault'
        $link.Save()
    } catch { $Notes.Add('the Start menu shortcut could not be created (' + $_.Exception.Message + ')') }
}
function Write-AppsEntry {
    if (Test-Path -LiteralPath $UninstallKey) { Remove-Item -LiteralPath $UninstallKey -Recurse -Force }
    [void](New-Item -Path $UninstallKey -Force)
    $sum = (Get-ChildItem -LiteralPath $Target -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
    $kb = [int][Math]::Ceiling([double]$sum / 1KB)
    $uninstall = Join-Path $Target 'scripts\Uninstall.cmd'
    $strings = [ordered]@{
        DisplayName          = $AppName
        DisplayVersion       = $Version
        Publisher            = $Publisher
        DisplayIcon          = $Exe
        InstallLocation      = $Target
        UninstallString      = ('"' + $uninstall + '"')
        QuietUninstallString = ('"' + $uninstall + '" /quiet')
        URLInfoAbout         = 'https://ideabosco.com'
        Comments             = 'Team and class CAD vault. Uninstall keeps the vault folder and its files.'
        InstallDate          = (Get-Date -Format 'yyyyMMdd')
    }
    foreach ($name in $strings.Keys) { [void](New-ItemProperty -LiteralPath $UninstallKey -Name $name -PropertyType String -Value $strings[$name] -Force) }
    foreach ($name in @('NoModify', 'NoRepair')) { [void](New-ItemProperty -LiteralPath $UninstallKey -Name $name -PropertyType DWord -Value 1 -Force) }
    [void](New-ItemProperty -LiteralPath $UninstallKey -Name 'EstimatedSize' -PropertyType DWord -Value $kb -Force)
}
# The notification registration and the link scheme, exactly as ShellIdentity.Layout writes them
# (all REG_SZ; "" is the key's default value).
function Get-IdentityLayout {
    $quotedExe = '"' + $Exe + '"'
    return @(
        @{ Key = $IdentityKey; Name = 'DisplayName'; Value = $AppName },
        @{ Key = $IdentityKey; Name = 'IconUri'; Value = (Join-Path $Target 'Assets\armory.ico') },
        @{ Key = $SchemeKey; Name = ''; Value = 'URL:IDEA Armory' },
        @{ Key = $SchemeKey; Name = 'URL Protocol'; Value = '' },
        @{ Key = $SchemeKey + '\DefaultIcon'; Name = ''; Value = ($quotedExe + ',0') },
        @{ Key = $SchemeKey + '\shell\open\command'; Name = ''; Value = ($quotedExe + ' "%1"') })
}
# A REG_SZ value under HKCU, or $null.
function Get-UserString([string]$key, [string]$name) {
    $open = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
    if (-not $open) { return $null }
    try {
        if ($null -eq $open.GetValue($name, $null)) { return $null }
        if ($open.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::String) { return $null }
        return [string]$open.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    } finally { $open.Close() }
}
# The identity values that are missing or differ (their key and name).
function Get-IdentityProblems {
    $wrong = @()
    foreach ($value in Get-IdentityLayout) {
        if ((Get-UserString $value.Key $value.Name) -cne $value.Value) { $wrong += ($value.Key + '\' + $(if ($value.Name) { $value.Name } else { '(Default)' })) }
    }
    return $wrong
}
# Writes the values that differ, as Armory does at start. True when it wrote any.
function Set-ShellIdentity {
    $wrote = $false
    foreach ($value in Get-IdentityLayout) {
        if ((Get-UserString $value.Key $value.Name) -ceq $value.Value) { continue }
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($value.Key)
        try { $key.SetValue($value.Name, $value.Value, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Close() }
        $wrote = $true
    }
    return $wrote
}
# Tells File Explorer that associations changed (SHCNE_ASSOCCHANGED), so the right-click items
# and the link scheme come and go at once. Where PowerShell may not compile code (a locked-down
# computer), Explorer catches up at the next sign-in instead.
function Send-AssociationsChanged {
    try {
        $shell = @(Add-Type -Namespace IdeaArmorySetup -Name ShellNotify -PassThru -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);')[0]
        $shell::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    } catch { $Notes.Add('File Explorer was not told at once (' + $_.Exception.Message + '); it catches up at the next sign-in') }
}
# Deletes every per-user key Armory writes, then tells Explorer once. Returns the keys still there.
function Remove-PerUserKeys {
    $removed = $false
    foreach ($key in $PerUserKeys) {
        $open = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
        if (-not $open) { continue }
        $open.Close()
        # A key that will not go is named below, by what is still there.
        try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($key, $false); $removed = $true } catch {}
    }
    if ($removed) { Send-AssociationsChanged }
    $still = @()
    foreach ($key in $PerUserKeys) {
        $open = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
        if ($open) { $open.Close(); $still += ('HKCU\' + $key) }
    }
    return $still
}
# File Explorer's right-click items, as this account's Armory wrote them.
function Describe-RightClickItems {
    $appliesTo = Get-UserString $VerbKeys[0] 'AppliesTo'
    $command = Get-UserString ($VerbKeys[1] + '\shell\01checkout\command') ''
    if (-not $appliesTo -and -not $command) { return 'not written yet (Armory writes them when it starts)' }
    $forwarder = '"' + (Join-Path $Target 'ArmoryShell.exe') + '"'
    if (-not $command -or -not $command.StartsWith($forwarder, [StringComparison]::OrdinalIgnoreCase)) { return ('point somewhere else: ' + $command) }
    $vault = [regex]::Match([string]$appliesTo, '~<"(.*)\\"$').Groups[1].Value
    return ('present, inside ' + $vault.Replace('""', '"'))
}
# What the badges setup recorded for the whole computer (any account may read it), and where
# Armory's four badges stand among Windows' overlay handlers (Windows shows the first 11).
function Get-BadgesState {
    $state = New-Object psobject -Property @{ Version = $null; Format = $null; Registered = 0; Shown = 0; First = 0; Total = 0 }
    try {
        $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        try {
            $setup = $machine.OpenSubKey($BadgesKey)
            if ($setup) { $state.Version = [string]$setup.GetValue('Version'); $state.Format = [string]$setup.GetValue('Format'); $setup.Close() }
            $list = $machine.OpenSubKey($OverlaysKey)
            if ($list) {
                $names = @($list.GetSubKeyNames())
                $list.Close()
                $state.Total = $names.Count
                for ($i = 0; $i -lt $names.Count; $i++) {
                    if ($BadgeNames -notcontains $names[$i]) { continue }
                    $state.Registered++
                    if ($i -lt $OverlayLimit) { $state.Shown++ }
                    if (-not $state.First) { $state.First = $i + 1 }
                }
            }
        } finally { $machine.Close() }
    } catch {}
    return $state
}
function Describe-Badges($state) {
    if (-not $state.Version -and $state.Registered -eq 0) { return 'not installed (optional: "Show Armory status on file icons.cmd" adds them, with an administrator''s password once for this computer)' }
    if (-not $state.Version -or $state.Registered -ne $BadgeNames.Count) { return ('BROKEN: ' + $state.Registered + ' of ' + $BadgeNames.Count + ' badges registered, version ' + $state.Version + '. Run "Show Armory status on file icons.cmd" again.') }
    $where = 'all 4 within the ' + $OverlayLimit + ' Windows shows'
    if ($state.Shown -eq 0) { $where = 'NONE within the ' + $OverlayLimit + ' Windows shows (other apps come first)' }
    elseif ($state.Shown -lt $BadgeNames.Count) { $where = 'only ' + $state.Shown + ' of 4 within the ' + $OverlayLimit + ' Windows shows (other apps come first)' }
    return ('installed, version ' + $state.Version + ' (format ' + $state.Format + '); ' + $where + ', from position ' + $state.First + ' of ' + $state.Total)
}
# The ShellExecute "runas" error when the person said no to the password prompt.
function Test-Cancelled($exception) {
    for ($e = $exception; $e; $e = $e.InnerException) {
        if ($e -is [ComponentModel.Win32Exception] -and $e.NativeErrorCode -eq 1223) { return $true }
    }
    return $false
}
function Describe-RunValue($settings) {
    $value = Get-RunValue
    if ($value -and [string]::Equals($value, $RunCommand, [StringComparison]::OrdinalIgnoreCase)) { return 'on' }
    if ($value) { return ('points somewhere else: ' + $value) }
    if (Test-StartAtSignInOff $settings) { return 'off (turned off in IDEA Armory settings)' }
    return 'MISSING'
}
function Get-Problems($settings) {
    $problems = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Exe)) { $problems.Add('IdeaArmory.exe is missing') }
    if (-not (Test-Path -LiteralPath (Join-Path $Target 'wwwroot'))) { $problems.Add('the wwwroot folder is missing') }
    if (-not (Test-StartAtSignInOff $settings)) {
        $value = Get-RunValue
        if (-not $value -or -not [string]::Equals($value, $RunCommand, [StringComparison]::OrdinalIgnoreCase)) { $problems.Add('the start at sign-in entry is missing or points somewhere else') }
    }
    if (-not (Test-Path -LiteralPath $UninstallKey) -and -not (Test-Path -LiteralPath $InnoKey)) { $problems.Add('the Apps entry is missing') }
    # Only this version's install writes them (an older one installed here never did).
    $installed = Get-InstalledVersion
    if ($installed -and ($Version -eq 'dev' -or $installed -eq $Version)) {
        $wrong = @(Get-IdentityProblems)
        if ($wrong.Count -gt 0) { $problems.Add('the notification and link registration is missing or points somewhere else (' + ($wrong -join ', ') + ')') }
    }
    return $problems
}
function Get-CheckProblem($check, $runtime) {
    if ($check.Problem) {
        return ('Windows did not let IdeaArmory.exe run (' + $check.Problem + '). If the school blocks programs in AppData, ask IT to allow ' + $Target + '.')
    }
    if ($check.ExitCode -ne 0) {
        if (-not $runtime) {
            return ('the Microsoft Edge WebView2 Runtime is missing, so the Armory window cannot open. Install it from ' + $WebView2Page + ' (no administrator password needed), then run this again.')
        }
        return ('IdeaArmory.exe --check reported a problem (exit ' + $check.ExitCode + '): ' + $check.Output.Trim())
    }
    return $null
}
function Show-AppCheck($check) {
    if ($check.Problem) { Say ('IdeaArmory.exe --check: could not run: ' + $check.Problem); return }
    Say ('IdeaArmory.exe --check: exit ' + $check.ExitCode)
    if ($check.Json) {
        Say ('  version ' + $check.Json.version + ', WebView2 runtime ' + $check.Json.webView2Runtime + ', page files ' + $check.Json.wwwroot + ', vault ' + $check.Json.vaultRoot)
    } elseif ($check.Output) { Say ('  ' + $check.Output.Trim()) }
}

function Install {
    Assert-SameUser
    $resolved = Resolve-Path -LiteralPath $Source -ErrorAction SilentlyContinue
    if (-not $resolved) { throw ('The files folder is missing (' + $Source + '). Copy the whole folder from the ZIP again.') }
    $from = $resolved.ProviderPath.TrimEnd('\')
    if ((Test-Within $from $Target) -or (Test-Within $Target $from)) { throw 'Run Install from the flash drive or from IDEA-Armory-Setup, not from the installed folder.' }
    if (-not (Test-Path -LiteralPath (Join-Path $from $ExeName))) { throw ('IdeaArmory.exe is missing from ' + $from + '. Copy the whole folder from the ZIP again.') }
    $manifest = Read-Manifest $from
    $settings = Read-Settings
    Set-Vaults $settings
    foreach ($vault in $script:Vaults) {
        if ((Test-Within $Target $vault) -or (Test-Within $vault $Target)) { throw ('The vault folder ' + $vault + ' overlaps the program folder ' + $Target + ', so nothing was changed.') }
    }
    $runtime = Get-WebView2
    Say ('Windows account:   ' + (Get-UserName) + ' (Armory installs for this account only)')
    Say ('Program folder:    ' + $Target)
    Say ('WebView2 runtime:  ' + (Format-WebView2 $runtime))
    $before = Get-InstalledVersion
    if ($before) { Say ('Found IDEA Armory ' + $before + '. Replacing it with ' + $Version + '.') }

    # Copy beside the old folder and check every byte against the package's list while the
    # old copy keeps running, so a damaged flash drive changes nothing. Then close it and swap.
    $stage = $Target + '.new'
    $old = $Target + '.old'
    foreach ($leftover in @($stage, $old)) {
        Remove-Tree $leftover
        if (Test-Path -LiteralPath $leftover) { throw ('Could not clear the leftover folder ' + $leftover + '. Restart Windows, then run this again.') }
    }
    Say 'Copying files...'
    Copy-Tree $from $stage
    $bad = @(Test-Manifest $stage $manifest)
    if ($bad.Count -gt 0) {
        Remove-Tree $stage
        throw ('These files did not copy correctly: ' + (($bad | Select-Object -First 3) -join ', ') + '. The copy on the flash drive may be damaged; copy the whole folder from the ZIP again.')
    }
    Get-ChildItem -LiteralPath $stage -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue
    try { $stopped = Stop-Armory } catch { $failure = $_; Remove-Tree $stage; throw $failure }
    if ($stopped -ne 'was not running') { Say ('IDEA Armory ' + $stopped + '.') }
    if (Test-Path -LiteralPath $Target) {
        try { Move-Folder $Target $old }
        catch {
            Remove-Tree $stage
            if ($stopped -ne 'was not running') { try { [void](Start-Armory) } catch {} }
            throw ('A file in ' + $Target + ' is in use, so nothing was changed. Close any window showing that folder, then run this again.')
        }
    }
    try { Move-Folder $stage $Target }
    catch {
        $message = $_.Exception.Message
        if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $Target)) { Move-Folder $old $Target }
        throw ('Could not put the new files in place: ' + $message)
    }
    if (Test-Path -LiteralPath $old) {
        Remove-Tree $old
        if (Test-Path -LiteralPath $old) { $Notes.Add('the old copy at ' + $old + ' could not be removed yet') }
    }

    New-StartMenuShortcut
    # The notification registration and the link scheme (Armory gives the shortcut its
    # AppUserModelID at start, which WScript.Shell cannot set, and writes the right-click items).
    if (Set-ShellIdentity) { Send-AssociationsChanged }
    Say 'Notifications:     registered (IDEA Armory, with the idea-armory: links)'
    $startOff = Test-StartAtSignInOff $settings
    if ($startOff) { Say 'Start at sign-in:  off (this person turned it off in IDEA Armory settings; left off)' }
    else { Set-RunValue; Say 'Start at sign-in:  on' }
    Write-AppsEntry
    if (Test-Path -LiteralPath $InnoKey) {
        Remove-Item -LiteralPath $InnoKey -Recurse -Force
        Say 'This replaced an IDEA-Armory-Setup install. Apps shows one IDEA Armory entry.'
    }

    $check = Invoke-Exe $Exe '--check' 60
    Show-AppCheck $check
    if ($check.Problem) { throw ('Installed, but ' + (Get-CheckProblem $check $runtime)) }
    $state = Start-Armory
    if (-not $state.Running) { throw ('Installed, but IDEA Armory did not start. Its log is ' + $AgentLog + '.') }
    Say 'Running now:       yes, in the tray near the clock'
    if (-not $state.Logged) { $Notes.Add('agent.log did not show "started" within 30 seconds') }
    $problem = Get-CheckProblem $check $runtime
    if ($problem) { throw ('Installed, but ' + $problem) }
    $problems = Get-Problems $settings
    if ($problems.Count -gt 0) { throw ('Installed, but these checks failed: ' + ($problems -join '; ')) }

    $what = 'Installed IDEA Armory ' + $Version
    if ($before -and $before -eq $Version) { $what = 'Reinstalled IDEA Armory ' + $Version }
    elseif ($before) { $what = 'Upgraded IDEA Armory ' + $before + ' to ' + $Version }
    $when = 'It is running and starts at each sign-in.'
    if ($startOff) { $when = 'It is running. Start at sign-in stays off, as this person chose.' }
    return ($what + ' for ' + (Get-UserName) + '. ' + $when)
}

function Uninstall {
    Assert-SameUser
    $before = Get-InstalledVersion
    $settings = Read-Settings
    Set-Vaults $settings
    Say ('Windows account:   ' + (Get-UserName))
    foreach ($vault in $script:Vaults) { Say ('Vault folder:      ' + $vault + '  (never deleted; stays as it is)') }
    $stopped = Stop-Armory
    if ($stopped -ne 'was not running') { Say ('IDEA Armory ' + $stopped + '.') }
    if (Test-Path -LiteralPath $Shortcut) { Remove-Item -LiteralPath $Shortcut -Force }
    Remove-RunValue
    foreach ($key in @($UninstallKey, $InnoKey)) { if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force } }
    $keysLeft = @(Remove-PerUserKeys)
    foreach ($folder in @($Target, ($Target + '.new'), ($Target + '.old'), $DataDir)) { Remove-Tree $folder }

    $left = New-Object System.Collections.Generic.List[string]
    foreach ($key in $keysLeft) { $left.Add($key) }
    if ((Test-Path -LiteralPath $Target) -and -not (Test-KeptInside $Target)) { $left.Add('the program folder') }
    if (Get-RunValue) { $left.Add('the start at sign-in entry') }
    if ((Test-Path -LiteralPath $UninstallKey) -or (Test-Path -LiteralPath $InnoKey)) { $left.Add('the Apps entry') }
    if (Test-Path -LiteralPath $Shortcut) { $left.Add('the Start menu shortcut') }
    if ((Test-Path -LiteralPath $DataDir) -and -not (Test-KeptInside $DataDir)) { $left.Add('the settings folder ' + $DataDir) }
    foreach ($item in $script:Left) { $left.Add($item) }
    foreach ($item in $script:Kept) { $Notes.Add('kept ' + $item + ' because a vault folder is there') }
    if ($left.Count -gt 0) { throw ('Some parts could not be removed: ' + ($left -join '; ') + '. Restart Windows, then run Uninstall again.') }
    $what = 'IDEA Armory was not installed for this Windows account; nothing is left behind'
    if ($before) { $what = 'Removed IDEA Armory ' + $before + ' from this Windows account (' + (Get-UserName) + ')' }
    return ($what + '. The vault folder ' + ($script:Vaults -join ' and ') + ' and every file in it were not touched.')
}

function Check {
    $installed = Get-InstalledVersion
    $settings = Read-Settings
    Set-Vaults $settings
    $runtime = Get-WebView2
    $vault = Get-VaultRoot $settings
    Say ('Windows account:    ' + (Get-UserName))
    $shown = 'not installed for this Windows account'
    if ($installed) { $shown = $installed }
    Say ('Installed version:  ' + $shown)
    $resolved = Resolve-Path -LiteralPath $Source -ErrorAction SilentlyContinue
    if ($resolved -and -not (Test-Within $resolved.ProviderPath $Target) -and (Test-Path -LiteralPath (Join-Path $resolved.ProviderPath $ExeName))) {
        Say ('On this drive:      ' + (Get-VersionOf (Join-Path $resolved.ProviderPath $ExeName)))
    }
    Say ('Program folder:     ' + $Target)
    Say ('Start at sign-in:   ' + (Describe-RunValue $settings))
    $apps = 'MISSING'
    if (Test-Path -LiteralPath $UninstallKey) { $apps = 'present (flash drive install)' }
    elseif (Test-Path -LiteralPath $InnoKey) { $apps = 'present (IDEA-Armory-Setup install)' }
    Say ('Apps entry:         ' + $apps)
    Say ('Start menu:         ' + $(if (Test-Path -LiteralPath $Shortcut) { 'present' } else { 'MISSING' }))
    Say ('WebView2 runtime:   ' + (Format-WebView2 $runtime))
    Say ('Vault folder:       ' + $vault + $(if (Test-Path -LiteralPath $vault) { '' } else { '  (not created yet)' }))
    Say ('Running now:        ' + $(if (@(Get-ArmoryProcesses).Count -gt 0) { 'yes' } else { 'no' }))
    $wrong = @(Get-IdentityProblems)
    $registered = { param($prefix) if (@($wrong | Where-Object { $_.StartsWith($prefix + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) { 'registered' } else { 'MISSING' } }
    Say ('Notifications:      ' + (& $registered $IdentityKey))
    Say ('Link scheme:        ' + (& $registered $SchemeKey))
    Say ('Right-click items:  ' + (Describe-RightClickItems))
    Say ('File icons:         ' + (Describe-Badges (Get-BadgesState)))
    if (-not $installed) { throw 'IDEA Armory is not installed for this Windows account.' }
    $problems = Get-Problems $settings
    if (Test-Path -LiteralPath (Join-Path $Target $ManifestName)) {
        $bad = @(Test-Manifest $Target (Read-Manifest $Target))
        if ($bad.Count -gt 0) { $problems.Add('damaged or missing files: ' + (($bad | Select-Object -First 3) -join ', ')) }
    }
    $check = Invoke-Exe $Exe '--check' 60
    Show-AppCheck $check
    $problem = Get-CheckProblem $check $runtime
    if ($problem) { $problems.Add($problem) }
    if ($problems.Count -gt 0) { throw ('IDEA Armory ' + $installed + ' is installed, but: ' + ($problems -join '; ')) }
    return ('IDEA Armory ' + $installed + ' is installed for ' + (Get-UserName) + ' and ready.')
}

function Stop {
    $how = Stop-Armory
    return ('IDEA Armory ' + $how + '.')
}

# The one optional administrator step, for the whole computer: the badges setup, run as an
# administrator through Windows' password prompt (ShellExecute's "runas", as Start-Process -Verb
# RunAs does, but with the cancel told apart by its error number, whatever Windows' language),
# silent but for its progress, and waited for. Armory itself stays installed per account.
function Badges {
    $resolved = Resolve-Path -LiteralPath $Source -ErrorAction SilentlyContinue
    if (-not $resolved) { throw ('The files folder is missing (' + $Source + '). Copy the whole folder from the ZIP again.') }
    $setup = Join-Path $resolved.ProviderPath $BadgesSetupName
    if (-not (Test-Path -LiteralPath $setup)) { throw ('The badges setup is missing (' + $setup + '). Copy the whole folder from the ZIP again.') }
    Say ('Windows account:   ' + (Get-UserName))
    Say ('For:               every Windows account on ' + $env:COMPUTERNAME)
    Say ('Before:            ' + (Describe-Badges (Get-BadgesState)))
    Say 'Windows now asks for an administrator password. Armory itself needs none.'
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $setup
    $info.Arguments = '/SILENT /SUPPRESSMSGBOXES /NORESTART'
    $info.WorkingDirectory = Split-Path -Parent $setup
    $info.UseShellExecute = $true
    $info.Verb = 'runas'
    try { $process = [Diagnostics.Process]::Start($info) }
    catch {
        if (Test-Cancelled $_.Exception) { throw 'Nothing changed: an administrator''s password is needed for this one step.' }
        throw ('The badges setup did not start (' + $_.Exception.Message + '), so nothing changed.')
    }
    if (-not $process) { throw 'The badges setup did not start, so nothing changed.' }
    $process.WaitForExit()
    $code = $process.ExitCode
    $process.Dispose()
    if ($code -ne 0) { throw ('The badges setup stopped before it finished (exit ' + $code + '), so nothing changed.') }
    $state = Get-BadgesState
    if (-not $state.Version -or $state.Registered -ne $BadgeNames.Count) { throw ('The badges setup finished, but ' + (Describe-Badges $state)) }
    if ($Version -ne 'dev' -and $state.Version -ne $Version) { throw ('The badges setup finished, but this computer records badges ' + $state.Version + ', not ' + $Version + '.') }
    Say ('After:             ' + (Describe-Badges $state))
    if ($state.Shown -lt $BadgeNames.Count) { $Notes.Add('other apps'' badges come first, so Windows shows only ' + $state.Shown + ' of Armory''s 4 (tools\check-overlays.ps1 lists them)') }
    return ('Armory''s status shows on file icons for every account on ' + $env:COMPUTERNAME + ' after each person signs out of Windows and back in (badges ' + $state.Version + ').')
}

function InnoUninstall {
    $settings = Read-Settings
    Set-Vaults $settings
    [void](Stop-Armory)
    Remove-RunValue
    if (Test-Path -LiteralPath $UninstallKey) { Remove-Item -LiteralPath $UninstallKey -Recurse -Force }
    foreach ($key in @(Remove-PerUserKeys)) { $script:Left.Add($key) }
    foreach ($folder in @(($Target + '.new'), ($Target + '.old'), $DataDir)) { Remove-Tree $folder }
    if ($script:Left.Count -gt 0) { throw ('Some parts could not be removed: ' + ($script:Left -join '; ')) }
    foreach ($item in $script:Kept) { $Notes.Add('kept ' + $item + ' because a vault folder is there') }
    return ('Removed the settings, logs, sign-in and File Explorer items of IDEA Armory. The vault folder ' + ($script:Vaults -join ' and ') + ' and every file in it were not touched.')
}

try {
    Write-Host ''
    Write-Host ('IDEA Armory ' + $Version + ' - ' + $Mode) -ForegroundColor Cyan
    $output = @(switch ($Mode) {
            'Install' { Install }
            'Uninstall' { Uninstall }
            'Check' { Check }
            'Stop' { Stop }
            'InnoUninstall' { InnoUninstall }
            'Badges' { Badges }
        })
    $reason = [string]$output[-1]
    if ($Notes.Count) { $reason += ' Note: ' + ($Notes -join '; ') + '.' }
    Say ('Took {0:0.0} seconds.' -f $Watch.Elapsed.TotalSeconds)
    Banner $true $reason
    Write-DriveLog 'PASS' $reason
    exit 0
} catch {
    $message = $_.Exception.Message
    Banner $false $message
    Write-DriveLog 'FAIL' $message
    exit 1
}
