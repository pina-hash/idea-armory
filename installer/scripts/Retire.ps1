# Shared by startup and Velopack's post-install/update hooks. Windows PowerShell 5.1.
# Inventory is bounded to registrations, running processes and known install locations.
# Never search a vault or a whole drive, never delete a directory tree, never follow links.
param([Parameter(Mandatory = $true)][string]$CurrentExe,
      [Parameter(Mandatory = $true)][string]$DataFolder)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core') {
    $env:PSModulePath = (Join-Path $PSHOME 'Modules') + ';' + (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules')
}
function Audit([string]$text) {
    $folder = Join-Path $DataFolder 'logs'
    [void][IO.Directory]::CreateDirectory($folder)
    $line = ([DateTime]::UtcNow.ToString('O') + ' ' + $text) | ConvertTo-Json -Compress
    [IO.File]::AppendAllText((Join-Path $folder 'install-retirement.jsonl'), $line + "`n")
}
function Initialize-NativePaths {
    if ('ArmoryRetirementPath' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ArmoryRetirementPath {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string path, StringBuilder buffer, uint length);
    public static string Expand(string path) {
        var buffer = new StringBuilder(32768);
        var size = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return size > 0 && size < buffer.Capacity ? buffer.ToString() : path;
    }
}
'@
}
function Full([string]$path) {
    Initialize-NativePaths
    $full = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($path))
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    # Expand 8.3 aliases even when the executable was already removed. Otherwise a short
    # path in a shortcut could make the surviving install look like a different copy.
    $existing = $full; $suffix = ''
    while (-not (Test-Path -LiteralPath $existing)) {
        $parent = Split-Path -Parent $existing
        if (-not $parent -or $parent -eq $existing) { break }
        $suffix = '\' + (Split-Path -Leaf $existing) + $suffix; $existing = $parent
    }
    $expanded = [ArmoryRetirementPath]::Expand($existing)
    if ($suffix) { $expanded.TrimEnd('\') + $suffix } else { $expanded }
}
function Within([string]$path, [string]$folder) {
    $p = Full $path; $f = Full $folder
    $p -ieq $f -or $p.StartsWith($f.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Linked([string]$path) {
    for ($p = Full $path; $p; $p = Split-Path -Parent $p) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $true }
    }
    return $false
}
function Binary([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Linked $path)) { return $false }
    $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $v.ProductName -ceq 'IDEA Armory' -and $v.CompanyName -ceq 'IDEA, Don Bosco Tech'
}
function CommandFile([string]$command) {
    if ($command -match '^\s*"([^"]+)"') { return $Matches[1] }
    if ($command -match '^\s*(.+?\.(?:exe|cmd|bat|ps1))(?=\s|,|$)') { return $Matches[1] }
    return $null
}
function Start-Uninstaller([string]$file, [string]$arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $file; $start.Arguments = $arguments
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    [Diagnostics.Process]::Start($start)
}
function Stream-Hash($stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function Unmodified-LegacyPayload($copy) {
    $manifest = Join-Path $copy.Root 'scripts\payload.sha256'
    if (-not (Test-Path -LiteralPath $manifest) -or (Test-Path -LiteralPath (Join-Path $copy.Root 'IdeaArmory.exe.WebView2'))) { return $false }
    $entries = 0
    foreach ($line in [IO.File]::ReadAllLines($manifest)) {
        if (-not $line) { continue }
        if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { return $false }
        $hash = $Matches[1]; $file = Full (Join-Path $copy.Root $Matches[2]); $entries++
        if (-not (Within $file $copy.Root) -or (Protected $file)) { return $false }
        if ($file -ieq $copy.Exe -and -not (Test-Path -LiteralPath $file)) { $file += '.retired' }
        if ((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $hash) { return $false }
    }
    return $entries -gt 0
}
function Owned-VelopackTree($copy) {
    # Update.exe removes its whole root. Prove every existing byte is package-owned first;
    # a user document, changed program file, cache, junction, or unknown metadata defers it.
    if (Test-Path -LiteralPath (Join-Path $copy.Root '.msi-installed')) { return $false }
    try {
        [xml]$manifest = [IO.File]::ReadAllText((Join-Path $copy.Root 'current\sq.version'))
        if ($manifest.package.metadata.id -cne 'IDEAArmory') { return $false }
        $package = Join-Path $copy.Root ('packages\IDEAArmory-' + $manifest.package.metadata.version + '-full.nupkg')
        if (-not (Test-Path -LiteralPath $package) -or (Linked $package)) { return $false }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        $expected = @{}
        try {
            foreach ($entry in $zip.Entries) {
                if (-not $entry.FullName.StartsWith('lib/app/') -or $entry.FullName.EndsWith('/')) { continue }
                $relative = $entry.FullName.Substring(8).Replace('/', '\')
                $file = Full (Join-Path $copy.Root ('current\' + $relative))
                if (-not (Within $file $copy.Root)) { return $false }
                $hash = Stream-Hash ($entry.Open()); $expected[$file] = $hash
                if ($relative -ieq 'IdeaArmory.exe') { $expected[$file + '.retired'] = $hash }
                if ($relative -ieq 'Squirrel.exe') { $expected[(Join-Path $copy.Root 'Update.exe')] = $hash }
                if ($relative -ieq 'IDEA Armory_ExecutionStub.exe') { $expected[(Join-Path $copy.Root 'IDEA Armory.exe')] = $hash }
            }
        } finally { $zip.Dispose() }
        $todo = New-Object 'System.Collections.Generic.Queue[string]'; $todo.Enqueue($copy.Root)
        while ($todo.Count) {
            foreach ($item in @(Get-ChildItem -LiteralPath $todo.Dequeue() -Force)) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
                if ($item.PSIsContainer) { $todo.Enqueue($item.FullName); continue }
                if ($item.FullName -ieq $package) { continue }
                if ($item.FullName -ieq (Join-Path $copy.Root 'packages\.velopack_lock') -and $item.Length -eq 0) { continue }
                if ($item.FullName -ieq (Join-Path $copy.Root '.portable') -and $item.Length -eq 0) { continue }
                if (-not $expected.ContainsKey($item.FullName) -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ine $expected[$item.FullName]) { return $false }
            }
        }
        return $true
    } catch { Audit ('could not prove updater ownership at ' + $copy.Root + ': ' + $_.Exception.Message); return $false }
}
$currentFolder = Split-Path -Parent (Full $CurrentExe)
$currentRoot = $currentFolder
if ((Split-Path -Leaf $currentFolder) -ieq 'current' -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $currentFolder) 'Update.exe'))) {
    $currentRoot = Split-Path -Parent $currentFolder
}
$protected = New-Object 'System.Collections.Generic.List[string]'
$protected.Add((Full $DataFolder)); $protected.Add('C:\IDEA\Armory')
$startWanted = $true
try {
    $settings = [IO.File]::ReadAllText((Join-Path $DataFolder 'settings.json')) | ConvertFrom-Json
    if ($settings.startAtSignIn -is [bool] -and -not $settings.startAtSignIn) { $startWanted = $false }
} catch { }
# Profiles can retain a different vault. Read only settings, never the vault's files.
foreach ($s in @((Join-Path $DataFolder 'settings.json')) + @(Get-ChildItem -LiteralPath (Join-Path $DataFolder 'profiles') -Filter settings.json -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)) {
    try { $v = (Get-Content -LiteralPath $s -Raw | ConvertFrom-Json).vaultRoot; if ($v) { $protected.Add((Full $v)) } } catch { }
}
function Protected([string]$path) {
    if (Linked $path) { return $true }
    foreach ($folder in $protected) { if (Within $path $folder) { return $true } }
    for ($p = Full $path; $p; $p = Split-Path -Parent $p) {
        if (Test-Path -LiteralPath (Join-Path $p '.armory')) { return $true }
    }
    return $false
}
function OverlapsProtected([string]$root) {
    if (Protected $root) { return $true }
    foreach ($folder in $protected) { if (Within $folder $root) { return $true } }
    return $false
}
$copies = @{}
function Add-Copy([string]$path) {
    if (-not $path) { return }
    try {
        $path = Full $path
        if ($path -notmatch '^[A-Za-z]:\\') { Audit ('deferred nonlocal target ' + $path); return }
        $folder = Split-Path -Parent $path
        $name = Split-Path -Leaf $path
        if ($name -ieq 'Update.exe' -or $name -ieq 'IDEA Armory.exe') { $folder = Join-Path $folder 'current'; $path = Join-Path $folder 'IdeaArmory.exe' }
        if ((Split-Path -Leaf $path) -ine 'IdeaArmory.exe' -or (Within $path $currentRoot)) { return }
        if (Protected $path) { Audit ('deferred protected target ' + $path); return }
        # Never retire a development build, including one registered by a developer.
        if ($path -match '\\(?:bin|obj)\\(?:Debug|Release)\\') { return }
        $known = (Binary $path) -or (Binary ($path + '.retired')) -or (Binary (Join-Path $folder 'IdeaArmory.dll'))
        if (-not $known -and (Test-Path -LiteralPath (Join-Path $folder 'sq.version'))) {
            [xml]$meta = [IO.File]::ReadAllText((Join-Path $folder 'sq.version'))
            $known = $meta.package.metadata.id -ceq 'IDEAArmory' -and $meta.package.metadata.authors -ceq 'IDEA, Don Bosco Tech' -and $meta.package.metadata.mainExe -ceq 'IdeaArmory.exe'
        }
        if (-not $known) { return }
        $root = $folder; $kind = 'files'
        if ((Split-Path -Leaf $folder) -ieq 'current' -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $folder) 'Update.exe'))) {
            $root = Split-Path -Parent $folder; $kind = 'velopack'
        }
        if ((Full $root).Length -le 3) { Audit ('deferred drive-root install ' + $root); return }
        if ((Within $CurrentExe $root) -or (OverlapsProtected $root)) { Audit ('deferred protected install ' + $root); return }
        $copies[$root] = [pscustomobject]@{ Root = $root; Exe = $path; Kind = $kind }
    } catch { Audit ('could not inspect ' + $path + ': ' + $_.Exception.Message) }
}
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$gate = New-Object Threading.Mutex($false, ('Global\IDEA-Armory-Retire-' + $sid))
$owned = $false
try {
    try { $owned = $gate.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { Audit 'deferred: another retirement owns the inventory'; exit 1 }
    # Both registry views and scopes. The optional badges have a different DisplayName and stay.
    $apps = @(); $runs = @(); $survivorApps = @()
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                $uninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
                $list = $base.OpenSubKey($uninstall)
                if ($list) {
                    try { foreach ($name in $list.GetSubKeyNames()) {
                        $key = $list.OpenSubKey($name)
                        try {
                            if ([string]$key.GetValue('DisplayName') -notmatch '^IDEA Armory(?:\s+\d.*)?$') { continue }
                            $location = [string]$key.GetValue('InstallLocation')
                            if ($location) { Add-Copy (Join-Path $location 'IdeaArmory.exe'); Add-Copy (Join-Path $location 'current\IdeaArmory.exe') }
                            $icon = CommandFile ([string]$key.GetValue('DisplayIcon')); Add-Copy $icon
                            $command = [string]$key.GetValue('QuietUninstallString')
                            if (-not $command) { $command = [string]$key.GetValue('UninstallString') }
                            $apps += [pscustomobject]@{ Hive = $hive; View = $view; Key = $uninstall + '\' + $name; Location = $location; Icon = $icon; Command = $command }
                            if ($location -and (Full $location) -ieq $currentRoot) {
                                $values = @{}
                                foreach ($valueName in $key.GetValueNames()) { $values[$valueName] = @{ Value = $key.GetValue($valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); Kind = $key.GetValueKind($valueName) } }
                                $survivorApps += [pscustomobject]@{ Hive = $hive; View = $view; Key = $uninstall + '\' + $name; Values = $values }
                            }
                        } finally { if ($key) { $key.Dispose() } }
                    } } finally { $list.Dispose() }
                }
                foreach ($runKey in @('Software\Microsoft\Windows\CurrentVersion\Run', 'Software\Microsoft\Windows\CurrentVersion\RunOnce')) {
                    $key = $base.OpenSubKey($runKey)
                    if (-not $key) { continue }
                    try { foreach ($name in $key.GetValueNames()) {
                        $file = CommandFile ([string]$key.GetValue($name)); Add-Copy $file
                        $runs += [pscustomobject]@{ Hive = $hive; View = $view; Key = $runKey; Name = $name; File = $file }
                    } } finally { $key.Dispose() }
                }
            } finally { $base.Dispose() }
        }
    }
    foreach ($folder in @((Join-Path $env:LOCALAPPDATA 'Programs\IDEA Armory'), (Join-Path $env:LOCALAPPDATA 'IDEAArmory'),
                          (Join-Path $env:ProgramFiles 'IDEA Armory'), (Join-Path $env:ProgramFiles 'IDEAArmory'),
                          (Join-Path ${env:ProgramFiles(x86)} 'IDEA Armory'))) {
        foreach ($suffix in @('', '.old', '.new')) {
            Add-Copy (Join-Path ($folder + $suffix) 'IdeaArmory.exe'); Add-Copy (Join-Path ($folder + $suffix) 'current\IdeaArmory.exe')
        }
    }
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'IdeaArmory.exe' OR Name = 'Update.exe'" -ErrorAction Stop)
    $caller = (Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $PID)).ParentProcessId
    $survivorRunning = @($processes | Where-Object { $_.ExecutablePath -ieq $CurrentExe -and $_.ProcessId -ne $caller }).Count -gt 0
    foreach ($p in $processes) {
        # An elevated inventory must not stop another student's per-user copy.
        $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction SilentlyContinue
        if ($owner.Sid -eq $sid) { Add-Copy $p.ExecutablePath }
    }
    $shell = New-Object -ComObject WScript.Shell
    $links = @()
    foreach ($special in @('Programs', 'CommonPrograms', 'DesktopDirectory', 'CommonDesktopDirectory', 'Startup', 'CommonStartup', 'Pins')) {
        $folder = if ($special -eq 'Pins') { Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar' } else { [Environment]::GetFolderPath($special) }
        if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { continue }
        # Do not follow a redirected/junction folder. No search outside these shell folders.
        $todo = New-Object 'System.Collections.Generic.Queue[string]'; $todo.Enqueue($folder)
        while ($todo.Count) {
            $dir = $todo.Dequeue(); if (Linked $dir) { continue }
            foreach ($item in @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) {
                if ($item.PSIsContainer) { if ($special -match 'Programs') { $todo.Enqueue($item.FullName) }; continue }
                if ($item.Extension -ine '.lnk') { continue }
                try {
                    $target = $shell.CreateShortcut($item.FullName).TargetPath; Add-Copy $target
                    $links += [pscustomobject]@{ File = $item.FullName; Target = $target; Pin = ($special -eq 'Pins'); Startup = ($special -match 'Startup') }
                } catch { Audit ('could not read shortcut ' + $item.FullName) }
            }
        }
    }
    $tasks = @()
    try {
        foreach ($task in @(Get-ScheduledTask -ErrorAction Stop)) {
            foreach ($action in $task.Actions) {
                $file = [Environment]::ExpandEnvironmentVariables([string]$action.Execute).Trim('"'); Add-Copy $file
                $tasks += [pscustomobject]@{ Task = $task; File = $file }
            }
        }
    } catch { Audit ('could not inventory scheduled tasks: ' + $_.Exception.Message) }
    function Belongs([string]$file, $copy) {
        if (-not $file) { return $false }
        $file = Full $file
        foreach ($relative in @('IdeaArmory.exe', 'current\IdeaArmory.exe', 'IDEA Armory.exe', 'Update.exe')) {
            if ($file -ieq (Join-Path $copy.Root $relative)) { return $true }
        }
        return $false
    }
    function Remove-TaskAction($entry, $copy) {
        $remaining = @($entry.Task.Actions | Where-Object { -not (Belongs ([Environment]::ExpandEnvironmentVariables([string]$_.Execute).Trim('"')) $copy) })
        if ($remaining.Count) {
            Set-ScheduledTask -TaskName $entry.Task.TaskName -TaskPath $entry.Task.TaskPath -Action $remaining | Out-Null
            Audit ('removed Armory action from mixed task ' + $entry.Task.TaskPath + $entry.Task.TaskName)
        } else {
            Stop-ScheduledTask -InputObject $entry.Task -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -InputObject $entry.Task -Confirm:$false
            Audit ('removed task ' + $entry.Task.TaskPath + $entry.Task.TaskName)
        }
    }
    function Remove-Reg($item, [bool]$app) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($item.Hive, $item.View)
        try {
            if ($app) {
                $key = $base.OpenSubKey($item.Key); if (-not $key) { return }; $key.Dispose()
                $base.DeleteSubKeyTree($item.Key, $false)
            } else {
                $key = $base.OpenSubKey($item.Key, $true); if (-not $key) { return }
                try { if ($null -eq $key.GetValue($item.Name)) { return }; $key.DeleteValue($item.Name, $false) } finally { $key.Dispose() }
            }
            Audit ('removed registry ' + $item.Hive + '/' + $item.View + '/' + $item.Key + '/' + $item.Name)
        } finally { $base.Dispose() }
    }
    # Consolidate even aliases of the surviving install. The app writes the one canonical Run
    # entry after this step, according to StartAtSignIn, including when that choice is off.
    $survivor = [pscustomobject]@{ Root = $currentRoot }
    foreach ($run in $runs) {
        if (Belongs $run.File $survivor) {
            # Setup checks its canonical entry as soon as the process says "started".
            # Retain a correct one until the host repairs it; do not create an off/on gap.
            if ($startWanted -and $run.Hive -eq [Microsoft.Win32.RegistryHive]::CurrentUser -and
                $run.Key -eq 'Software\Microsoft\Windows\CurrentVersion\Run' -and $run.Name -eq 'IDEA Armory') { continue }
            try { Remove-Reg $run $false } catch { Audit ('deferred survivor auto-start: ' + $_.Exception.Message) }
        }
    }
    foreach ($link in $links) {
        if ($link.Startup -and (Belongs $link.Target $survivor)) {
            try { Remove-Item -LiteralPath $link.File -Force; Audit ('removed startup shortcut ' + $link.File) } catch { Audit ('deferred startup shortcut: ' + $_.Exception.Message) }
        }
    }
    foreach ($entry in $tasks) {
        if (Belongs $entry.File $survivor) { try { Remove-TaskAction $entry $survivor } catch { Audit ('deferred survivor task: ' + $_.Exception.Message) } }
    }
    foreach ($copy in @($copies.Values)) {
        try {
            Audit ('found competing install ' + $copy.Root + ' (' + $copy.Kind + ')')
            # Close only processes whose full path is in this verified install, never by name.
            foreach ($p in $processes) {
                if (-not (Belongs $p.ExecutablePath $copy)) { continue }
                $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction SilentlyContinue
                if ($owner.Sid -ne $sid) { continue }
                $live = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
                if ($live -and $live.Path -ieq $p.ExecutablePath) {
                    if (-not $survivorRunning -and $p.ExecutablePath -ieq $copy.Exe) {
                        # The older versions share the user's quit signal. Only use it when
                        # there is no surviving runtime to accidentally signal instead.
                        try {
                            $quit = Start-Uninstaller $copy.Exe '--quit'
                            [void]$quit.WaitForExit(3000)
                            if ($live.HasExited) { Audit ('closed process ' + $p.ProcessId + ' cleanly at ' + $p.ExecutablePath); continue }
                        } catch { Audit ('could not ask the older process to quit: ' + $_.Exception.Message) }
                    }
                    [void]$live.CloseMainWindow()
                    if (-not $live.WaitForExit(1500)) {
                        if (-not $survivorRunning -and $p.ExecutablePath -ieq $copy.Exe) {
                            # AgentLog already recognizes this intentional end. Do not report
                            # retiring an unresponsive old copy as a crash of the next version.
                            [IO.File]::AppendAllText((Join-Path $DataFolder 'logs\agent.log'), [DateTime]::UtcNow.ToString('O') + " quitting`n")
                        }
                        $live.Kill(); [void]$live.WaitForExit(1500)
                    }
                    Audit ('stopped process ' + $p.ProcessId + ' at ' + $p.ExecutablePath)
                }
            }
            foreach ($run in $runs) { if (Belongs $run.File $copy) { try { Remove-Reg $run $false } catch { Audit ('deferred auto-start: ' + $_.Exception.Message) } } }
            foreach ($entry in $tasks) {
                if (Belongs $entry.File $copy) {
                    try { Remove-TaskAction $entry $copy }
                    catch { Audit ('deferred task: ' + $_.Exception.Message) }
                }
            }
            foreach ($link in $links) {
                if (-not (Belongs $link.Target $copy)) { continue }
                try {
                    if ($link.Pin) {
                        # Velopack's uninstaller deletes pins rooted in its install. Repoint the
                        # existing link first, preserving its name and taskbar position. The app
                        # stamps its property store after the inventory finishes.
                        $launcher = Join-Path $currentRoot 'IDEA Armory.exe'
                        if (-not (Test-Path -LiteralPath $launcher)) { $launcher = $CurrentExe }
                        $pin = $shell.CreateShortcut($link.File); $pin.TargetPath = $launcher
                        $pin.Arguments = ''; $pin.WorkingDirectory = Split-Path -Parent $launcher; $pin.IconLocation = $launcher + ',0'; $pin.Save()
                        Audit ('preserved taskbar pin ' + $link.File + ' at ' + $launcher)
                    } else { Remove-Item -LiteralPath $link.File -Force; Audit ('removed shortcut ' + $link.File) }
                } catch { Audit ('deferred shortcut: ' + $_.Exception.Message) }
            }
            # Disable the executable before an old uninstaller's --quit hook can signal the
            # surviving instance. Only our verified executable is renamed, never user data.
            $disabled = $copy.Exe + '.retired'
            if (Test-Path -LiteralPath $copy.Exe) { Move-Item -LiteralPath $copy.Exe -Destination $disabled -Force }
            $setup = Join-Path $copy.Root 'scripts\Setup.ps1'
            $saved = $null
            $uninstaller = $null
            try {
                # Published 0.3.x/0.4.x uninstall scripts delete the shared data directory.
                # Substitute a no-data cleanup hook while Inno removes its recorded payload.
                # Restore the original if the uninstaller leaves it behind.
                $inno = Join-Path $copy.Root 'unins000.exe'
                $usb = Join-Path $copy.Root 'scripts\Uninstall.cmd'
                $registeredInno = @($apps | Where-Object { (CommandFile $_.Command) -ieq $inno }).Count -gt 0
                if ((($registeredInno -and (Test-Path -LiteralPath $inno)) -or (Test-Path -LiteralPath $usb)) -and
                    (Test-Path -LiteralPath $setup) -and (Unmodified-LegacyPayload $copy)) {
                    $saved = [IO.File]::ReadAllBytes($setup)
                    [IO.File]::WriteAllBytes(($setup + '.retire-original'), $saved)
                    [IO.File]::WriteAllText($setup, 'param($Mode, $Source, $LogDir)' + "`r`n" + 'exit 0')
                    if ($registeredInno) { $uninstaller = Start-Uninstaller $inno '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' }
                    else { $uninstaller = Start-Uninstaller $env:ComSpec ('/d /s /c ""' + $usb + '" /quiet"') }
                    if (-not $uninstaller.WaitForExit(10000)) { Audit ('waiting for the slow uninstaller at ' + $copy.Root); $uninstaller.WaitForExit() }
                    Audit ('ran uninstaller at ' + $copy.Root + ', exit ' + $uninstaller.ExitCode + '; data cleanup suppressed')
                    if ($uninstaller.ExitCode -ne 0) { continue }
                } elseif ($copy.Kind -eq 'velopack') {
                    if (Owned-VelopackTree $copy) {
                        $uninstaller = Start-Uninstaller (Join-Path $copy.Root 'Update.exe') '--silent --uninstall'
                        if (-not $uninstaller.WaitForExit(10000)) { Audit ('waiting for the slow updater uninstaller at ' + $copy.Root); $uninstaller.WaitForExit() }
                        Audit ('ran updater uninstaller at ' + $copy.Root + ', exit ' + $uninstaller.ExitCode)
                    } else { Audit ('kept untracked or changed files at ' + $copy.Root + '; updater uninstall deferred, launchers retired') }
                } elseif ($registeredInno -or (Test-Path -LiteralPath $usb)) {
                    Audit ('kept changed payload or cache at ' + $copy.Root + '; uninstaller deferred, launchers retired')
                }
            } finally {
                # A slow uninstaller may invoke its hook later. Never put a destructive hook
                # back while that process is alive; retain the original beside it for recovery.
                if ($saved -and (-not $uninstaller -or $uninstaller.HasExited)) {
                    if (Test-Path -LiteralPath $setup) { [IO.File]::WriteAllBytes($setup, $saved) }
                    Remove-Item -LiteralPath ($setup + '.retire-original') -Force -ErrorAction SilentlyContinue
                }
            }
            # The manifest proves ownership AND bytes. Edited or unlisted files stay put.
            $manifest = Join-Path $copy.Root 'scripts\payload.sha256'
            if (Test-Path -LiteralPath $manifest) {
                foreach ($line in [IO.File]::ReadAllLines($manifest)) {
                    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { continue }
                    $hash = $Matches[1]; $relative = $Matches[2]
                    $file = Full (Join-Path $copy.Root $relative)
                    if (-not (Within $file $copy.Root) -or (Protected $file) -or (Within $CurrentExe $file)) { continue }
                    if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ieq $hash) {
                        Remove-Item -LiteralPath $file -Force; Audit ('removed payload ' + $file)
                    }
                }
            }
            foreach ($file in @($disabled, (Join-Path $copy.Root 'IDEA Armory.exe'))) {
                if ((Test-Path -LiteralPath $file) -and (Binary $file) -and -not (Protected $file)) { Remove-Item -LiteralPath $file -Force; Audit ('removed launcher ' + $file) }
            }
            # Update.exe could reinstall an otherwise retired channel. Disable it too, but
            # leave package caches and all unknown files intact.
            if ($copy.Kind -eq 'velopack') {
                $updater = Join-Path $copy.Root 'Update.exe'
                if ((Test-Path -LiteralPath $updater) -and -not (Protected $updater)) { Remove-Item -LiteralPath $updater -Force; Audit ('removed retired updater ' + $updater) }
            }
            foreach ($app in $apps) {
                if (($app.Location -and (Within $app.Location $copy.Root)) -or (Belongs $app.Icon $copy) -or (Belongs (CommandFile $app.Command) $copy)) {
                    try { Remove-Reg $app $true } catch { Audit ('deferred Apps entry: ' + $_.Exception.Message) }
                }
            }
            Audit ('retired install ' + $copy.Root + '; settings and vaults kept')
        } catch { Audit ('deferred install ' + $copy.Root + ': ' + $_.Exception.Message) }
    }
    # Velopack's uninstaller removes the HKCU key by package ID, even for a different root.
    # Restore the survivor's exact Apps values, including the original install date/version.
    foreach ($app in $survivorApps) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($app.Hive, $app.View)
        try {
            $key = $base.CreateSubKey($app.Key)
            try { foreach ($name in $app.Values.Keys) { $key.SetValue($name, $app.Values[$name].Value, $app.Values[$name].Kind) } }
            finally { $key.Dispose() }
        } finally { $base.Dispose() }
    }
    Audit ('inventory complete; survivor ' + $CurrentExe)
} catch { Audit ('retirement failed: ' + $_.Exception.Message); exit 1 }
finally { if ($owned) { $gate.ReleaseMutex() }; $gate.Dispose() }
