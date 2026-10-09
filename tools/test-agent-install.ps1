# The install, launch and uninstall cycle that CI runs on a clean windows-latest runner (whose
# steps run as an administrator):
#   -Kind Usb    from the extracted flash-drive zip: Install /quiet twice, Check, Uninstall /quiet,
#                Check again (must fail), and the drive's logs\<COMPUTERNAME>.txt
#   -Kind Setup  with IDEA-Armory-Setup-v<version>.exe /VERYSILENT twice, the installed Check,
#                then unins000.exe /VERYSILENT
#   -Kind Upgrade  for the flash drive and for setup.exe (-Route Both, the default, or one of
#                them): download the published v<From> release (0.1.0 by default; CI also runs
#                -From 0.3.2) into RUNNER_TEMP and check each asset against its .sha256, install
#                it and wait for it to start, plant the vault's proof files, a settings.json with
#                a non-default theme and a sign-in in the exact DpapiSecretStore format, then
#                install this build over it. This build must run, the Apps entry must show its
#                version, the sign-in, the settings and the proof files must keep every byte, the
#                sign-in must still decrypt for this Windows account and this build's log must say
#                it loaded that session, its vault runtime must start on the old vault, a 0.1.0
#                read-only intent must not make a file the server does not have read-only, and
#                no file of the old page (wwwroot) may survive. Then uninstall.
#   -Kind Badges  the optional administrator step (docs/agent/EXPLORER.md, T9):
#                IDEA-Armory-Badges-Setup-v<version>.exe /VERYSILENT, then every HKLM key and
#                value with its exact data, the DLL at the registered path with its version,
#                BadgeProbe.exe --attach --com (from the native build) creating all four handlers
#                through CoCreateInstance, ExtractIconEx giving the four icons, and
#                tools\check-overlays.ps1's output kept as evidence and listing Armory's four; a
#                second run through the drive's "Show Armory status on file icons.cmd" (Setup.ps1
#                -Mode Badges) is clean; the uninstaller removes every key, the files and the
#                Apps entry.
#   -Kind SolidWorks  an upgrade with SolidWorks open (docs/agent/SOLIDWORKS.md): with the test
#                fake tests/Armory.FakeSolidWorks running (from the solution's build; without it
#                this cycle says so and passes), install, wait until Armory links to it,
#                install again over it: the fake keeps running and answering, every sink the old
#                Armory advised is unadvised, and the new Armory links again.
# After Armory starts, each install cycle also checks this version's per-user wiring: the
# notification registration and the idea-armory: scheme with exact values, File Explorer's
# right-click items with this install's paths and the vault root in AppliesTo, the Start menu
# shortcut's AppUserModelID, a link launch (idea-armory:act?t=bogus&a=show) reaching the running
# Armory without a second one staying, ArmoryShell.exe and badges\IDEA-Armory-Badges-Setup.exe in
# the payload with the right version resources, and no SolidWorks registry footprint (nothing
# under HKCU\Software\SolidWorks, no Armory CLSID under HKCU\Software\Classes\CLSID, nothing
# changed under HKLM\SOFTWARE\SolidWorks). Each cycle checks the exe, the start at sign-in value,
# the Apps entry, the Start menu shortcut, IdeaArmory.exe --check, the running process and
# agent.log, and that uninstall removes all of it, every per-user key included, while
# C:\IDEA\Armory\Proof\ keeps the same bytes.
#
# It installs and removes IDEA Armory for the current Windows account (and, for -Kind Badges, the
# badges for the computer) and writes into C:\IDEA\Armory\Proof. Run it only on a throwaway
# machine such as a CI runner.
param(
    [Parameter(Mandatory = $true)][ValidateSet('Usb', 'Setup', 'Upgrade', 'Badges', 'SolidWorks')][string]$Kind,
    [string]$Dist = 'dist',
    [string]$Evidence = 'evidence',
    [string]$Native = 'publish/native',
    [ValidateSet('Both', 'Usb', 'Setup')][string]$Route = 'Both',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$From = '0.1.0',
    [string]$ReleaseUrl = 'https://github.com/pina-hash/idea-armory/releases/download/v{0}/'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Dist)) { $Dist = Join-Path $root $Dist }
if (-not [IO.Path]::IsPathRooted($Evidence)) { $Evidence = Join-Path $root $Evidence }
if (-not [IO.Path]::IsPathRooted($Native)) { $Native = Join-Path $root $Native }
[void][IO.Directory]::CreateDirectory($Evidence)
# Evidence names: each upgrade source keeps its own.
$Tag = $Kind.ToLowerInvariant()
if ($Kind -eq 'Upgrade') { $Tag += '-from-' + $From }
$Log = Join-Path $Evidence ('install-cycle-' + $Tag + '.txt')

$zip = Get-ChildItem -LiteralPath $Dist -Filter 'IDEA-Armory-USB-v*.zip' | Select-Object -First 1
if (-not $zip) { throw ('No IDEA-Armory-USB-v*.zip in ' + $Dist + '. Run tools/package-agent.ps1 first.') }
$Version = [regex]::Match($zip.Name, '^IDEA-Armory-USB-v(\d+\.\d+\.\d+)\.zip$').Groups[1].Value
if (-not $Version) { throw ('Unexpected zip name ' + $zip.Name) }
$SetupExe = Join-Path $Dist ('IDEA-Armory-Setup-v' + $Version + '.exe')
$BadgesExe = Join-Path $Dist ('IDEA-Armory-Badges-Setup-v' + $Version + '.exe')
$guid = [regex]::Match((Get-Content -LiteralPath (Join-Path $root 'installer/IdeaArmory.iss') -Raw), '#define AppId "\{\{([0-9A-F-]{36})\}"').Groups[1].Value
if (-not $guid) { throw 'Could not read AppId from installer/IdeaArmory.iss.' }

$Local = [Environment]::GetFolderPath('LocalApplicationData')
$Target = Join-Path $Local 'Programs\IDEA Armory'
$Exe = Join-Path $Target 'IdeaArmory.exe'
$DataDir = Join-Path $Local 'IDEA Armory'
$AgentLog = Join-Path $DataDir 'logs\agent.log'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunCommand = '"' + $Exe + '" --background'
$UsbKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\IDEA Armory'
$InnoKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + $guid + '}_is1'
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'IDEA Armory.lnk'
$Vault = 'C:\IDEA\Armory'
$ProofFiles = @((Join-Path $Vault 'Proof\keep.txt'), (Join-Path $Vault 'Proof\Nested\keep.bin'))
$temp = $env:RUNNER_TEMP
if (-not $temp) { $temp = [IO.Path]::GetTempPath() }
$Usb = Join-Path $temp 'armory-usb'
$script:Runs = 0
$script:ProofHashes = @()
# Upgrade: what the old version leaves and the new one must keep byte for byte.
$SettingsFile = Join-Path $DataDir 'settings.json'
$SecretFile = Join-Path $DataDir 'secrets\armory-session.secret'
$StalePage = Join-Path $Target 'wwwroot\stale-page-file.js'
$SessionEmail = 'upgrade.test@example.com'
$VaultStarted = 'vault runtime started at ' + $Vault
$OldManifest = Join-Path $Vault '.armory\read-only.json'
$script:Kept = [ordered]@{}
$script:SessionJson = $null
# This version's per-user wiring (docs/agent/INSTALL.md, docs/agent/EXPLORER.md), under HKCU.
$Aumid = 'IdeaBosco.Armory'
$IdentityKey = 'Software\Classes\AppUserModelId\' + $Aumid
$SchemeKey = 'Software\Classes\idea-armory'
$FilesVerb = 'Software\Classes\AllFilesystemObjects\shell\IDEAArmory'
$FilesMenu = 'Software\Classes\IDEAArmory.Menu'
$BackgroundVerb = 'Software\Classes\Directory\Background\shell\IDEAArmory'
$BackgroundMenu = 'Software\Classes\IDEAArmory.BackgroundMenu'
$PerUserKeys = @($IdentityKey, $SchemeKey, $FilesVerb, $FilesMenu, $BackgroundVerb, $BackgroundMenu, 'Software\IDEA Armory')
$BogusLink = 'idea-armory:act?t=bogus&a=show'
# The badges (installer\IdeaArmoryBadges.iss), for the whole computer.
$BadgesAppId = '{EA89842F-F4B2-4F97-8FB3-B90F36C40D3C}'
$BadgesFolder = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'IDEA Armory Badges'
$BadgesDll = Join-Path $BadgesFolder ($Version + '\ArmoryBadges.dll')
$Badges = @(
    @{ Name = 'Attention'; Key = ' IDEAArmory1Attention'; Clsid = '{E26E19F2-515F-472F-AD4F-1B0293728CE2}'; Text = 'IDEA Armory badge: needs attention' },
    @{ Name = 'Mine'; Key = ' IDEAArmory2Mine'; Clsid = '{DB040D16-C118-4CDA-B616-DF9340A8BC9F}'; Text = 'IDEA Armory badge: checked out by you' },
    @{ Name = 'Locked'; Key = ' IDEAArmory3Locked'; Clsid = '{DF50E3A9-57B8-44AE-B690-257AFF283F97}'; Text = 'IDEA Armory badge: checked out by someone else' },
    @{ Name = 'Synced'; Key = ' IDEAArmory4Synced'; Clsid = '{58F5F8D8-1041-43B9-B8DE-0EBEBCC29CF0}'; Text = 'IDEA Armory badge: synced' })
$OverlaysKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
$ApprovedKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'
$BadgesSetupKey = 'SOFTWARE\IDEA Armory\Badges'
$BadgesAppsKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $BadgesAppId + '_is1'
$script:SolidWorksBefore = $null

function Note([string]$text) { Add-Content -LiteralPath $Log -Value $text -Encoding utf8; Write-Host $text }
function Step([string]$text) { Note ''; Note ('--- ' + $text) }
function Fail([string]$text) { Note ('FAIL: ' + $text); throw $text }
function Wait-Until([scriptblock]$Done, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (& $Done)) {
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 250
    }
    return $true
}
# Runs a .cmd through cmd.exe with its output in a file, or an .exe directly. It starts through
# ShellExecute (no inherited handles) and waits for that one process only, so an app it starts
# in the tray never holds this step open.
function Invoke-Program([string]$Label, [string]$File, [string]$Arguments, [int]$Seconds = 600) {
    $script:Runs++
    $out = Join-Path $Evidence ('{0}-{1:00}-{2}.txt' -f $Tag, $script:Runs, $Label)
    $info = [Diagnostics.ProcessStartInfo]::new()
    if ($File -like '*.cmd') {
        $info.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
        $info.Arguments = '/d /s /c ""' + $File + '" ' + $Arguments + ' > "' + $out + '" 2>&1"'
    } else {
        $info.FileName = $File
        $info.Arguments = $Arguments
    }
    $info.UseShellExecute = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit($Seconds * 1000)) {
        try { $process.Kill($true) } catch {}
        Fail ($Label + ' did not finish within ' + $Seconds + ' seconds')
    }
    $code = $process.ExitCode
    Note ('{0}: exit {1} in {2:0.0} s' -f $Label, $code, $watch.Elapsed.TotalSeconds)
    $text = ''
    if (Test-Path -LiteralPath $out) {
        $text = Get-Content -LiteralPath $out -Raw
        foreach ($line in ($text -split "`r?`n")) { if ($line.Trim()) { Note ('    ' + $line) } }
    }
    return [pscustomobject]@{ Code = $code; Output = $text }
}
function Expect([string]$Label, [string]$File, [string]$Arguments, [bool]$Success) {
    $run = Invoke-Program $Label $File $Arguments
    if ($Success -and $run.Code -ne 0) { Fail ($Label + ' returned ' + $run.Code + ', expected 0') }
    if (-not $Success -and $run.Code -eq 0) { Fail ($Label + ' returned 0, expected a failure') }
    return $run
}
# Runs a program directly with its output read (a console tool), saved as evidence. Never through
# ShellExecute, so only for programs that start nothing that stays.
function Invoke-Captured([string]$Label, [string]$File, [string[]]$Arguments, [int]$Seconds = 300) {
    $script:Runs++
    $out = Join-Path $Evidence ('{0}-{1:00}-{2}.txt' -f $Tag, $script:Runs, $Label)
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($Seconds * 1000)) {
        try { $process.Kill($true) } catch {}
        Fail ($Label + ' did not finish within ' + $Seconds + ' seconds')
    }
    $process.WaitForExit()
    $text = $stdout.Result + $stderr.Result
    [IO.File]::WriteAllText($out, $text, [Text.UTF8Encoding]::new($false))
    Note ('{0}: exit {1} in {2:0.0} s' -f $Label, $process.ExitCode, $watch.Elapsed.TotalSeconds)
    foreach ($line in ($text -split "`r?`n")) { if ($line.Trim()) { Note ('    ' + $line) } }
    return [pscustomobject]@{ Code = $process.ExitCode; Output = $text; File = $out }
}
function Get-ArmoryPids([switch]$Anywhere) {
    $ids = @()
    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = 'IdeaArmory.exe'")) {
        if (-not $p.ExecutablePath) { continue }
        if ($Anywhere) { if ($p.ExecutablePath.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase)) { $ids += $p.ProcessId } }
        elseif ([string]::Equals($p.ExecutablePath, $Exe, [StringComparison]::OrdinalIgnoreCase)) { $ids += $p.ProcessId }
    }
    return $ids
}
function Read-AgentLog {
    if (-not (Test-Path -LiteralPath $AgentLog)) { return '' }
    $stream = [IO.FileStream]::new($AgentLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}
function Invoke-AppCheck {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe, '--check')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { try { $process.Kill($true) } catch {}; Fail 'IdeaArmory.exe --check did not finish within 60 seconds' }
    $raw = ''
    if ($out.Wait(5000)) { $raw = $out.Result.Trim() }
    if ($err.Wait(5000) -and $err.Result.Trim()) { Note ('    --check stderr: ' + $err.Result.Trim()) }
    $json = $null
    foreach ($line in ($raw -split "`r?`n")) { if ($line.Trim().StartsWith('{')) { try { $json = $line.Trim() | ConvertFrom-Json } catch {}; break } }
    return [pscustomobject]@{ Code = $process.ExitCode; Raw = $raw; Json = $json }
}
function Get-RunValue { return (Get-ItemProperty -LiteralPath $RunKey -Name 'IDEA Armory' -ErrorAction SilentlyContinue).'IDEA Armory' }

# The app's --check needs the Evergreen WebView2 Runtime, which the runner image does not list.
function Confirm-WebView2 {
    $client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $keys = @(('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + $client), ('HKCU:\Software\Microsoft\EdgeUpdate\Clients\' + $client))
    $find = { foreach ($key in $keys) { $pv = (Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue).pv; if ($pv -and $pv -ne '0.0.0.0') { return $pv } } }
    $pv = & $find
    if (-not $pv) {
        Note 'WebView2 runtime missing on the runner; installing it with the Evergreen Bootstrapper.'
        $bootstrapper = Join-Path $temp 'MicrosoftEdgeWebview2Setup.exe'
        Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
        # No -Wait: it would also wait for the updater it leaves running.
        $process = Start-Process -FilePath $bootstrapper -ArgumentList '/silent', '/install' -PassThru
        if (-not $process.WaitForExit(600000)) { Fail 'The WebView2 bootstrapper did not finish within 10 minutes' }
        $pv = & $find
    }
    if (-not $pv) { Fail 'The WebView2 runtime is still missing on the runner' }
    Note ('WebView2 runtime on the runner: ' + $pv)
}
function Save-Proof {
    if (-not (Test-Path -LiteralPath $ProofFiles[0])) {
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $ProofFiles[1]))
        [IO.File]::WriteAllText($ProofFiles[0], 'IDEA Armory uninstall must keep this file. ' + (Get-Date -Format o) + "`r`n")
        $bytes = [byte[]]::new(65536)
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        [IO.File]::WriteAllBytes($ProofFiles[1], $bytes)
        Note ('created ' + ($ProofFiles -join ' and '))
    }
    $script:ProofHashes = @($ProofFiles | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
    Note ('vault proof SHA-256: ' + ($script:ProofHashes -join ', '))
}
function Assert-Proof {
    for ($i = 0; $i -lt $ProofFiles.Count; $i++) {
        if (-not (Test-Path -LiteralPath $ProofFiles[$i])) { Fail ('The vault file ' + $ProofFiles[$i] + ' is gone') }
        $hash = (Get-FileHash -LiteralPath $ProofFiles[$i] -Algorithm SHA256).Hash
        if ($hash -ne $script:ProofHashes[$i]) { Fail ('The vault file ' + $ProofFiles[$i] + ' changed') }
    }
    Note ('vault files intact: ' + ($ProofFiles -join ', '))
}
function Assert-Clean([string]$when, [switch]$Before) {
    $found = @()
    if (Test-Path -LiteralPath $Target) { $found += 'program folder' }
    if (Get-RunValue) { $found += 'Run value' }
    if (Test-Path -LiteralPath $UsbKey) { $found += 'flash-drive Apps entry' }
    if (Test-Path -LiteralPath $InnoKey) { $found += 'setup Apps entry' }
    if (Test-Path -LiteralPath $Shortcut) { $found += 'Start menu shortcut' }
    if (Test-Path -LiteralPath $DataDir) { $found += ('data folder ' + $DataDir) }
    foreach ($key in $PerUserKeys) { if (Test-UserKey $key) { $found += ('HKCU\' + $key) } }
    if (@(Get-ArmoryPids -Anywhere).Count -gt 0) { $found += 'running IdeaArmory.exe' }
    if ($Before -and $found.Count -gt 0 -and -not (Test-Path -LiteralPath $Target) -and @(Get-ArmoryPids -Anywhere).Count -eq 0) {
        # Earlier steps (the agent's tests) may leave settings or a Run value; uninstall must still remove them.
        Note ($when + ': present already, and uninstall must remove it: ' + ($found -join ', '))
        return
    }
    if ($found.Count -gt 0) {
        if (Test-Path -LiteralPath $Target) { Get-ChildItem -LiteralPath $Target -Recurse -Force | Select-Object -First 20 | ForEach-Object { Note ('    left: ' + $_.FullName) } }
        Fail ($when + ': ' + ($found -join ', ') + ' still present')
    }
    Note ($when + ': no program folder, Run value, Apps entry, shortcut, data folder, per-user key or process')
}
# The second install closes the running copy and starts a new one.
function Assert-Restarted($Before) {
    $now = @(Get-ArmoryPids)
    $same = @($now | Where-Object { $Before -contains $_ })
    if ($same.Count -gt 0) { Fail ('The app was not restarted: process ' + ($same -join ', ') + ' is still the one from before') }
    Note ('restarted: process ' + ($Before -join ', ') + ' closed, ' + ($now -join ', ') + ' running')
}
function Assert-Installed([string]$Entry, [string]$Expect = $Version) {
    if (-not (Test-Path -LiteralPath $Exe)) { Fail ('IdeaArmory.exe is missing at ' + $Exe) }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe)
    $exeVersion = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
    if ($exeVersion -ne $Expect) { Fail ('IdeaArmory.exe is ' + $exeVersion + ', expected ' + $Expect) }
    Note ('exe: ' + $Exe + ' ' + $exeVersion + ' (' + $info.ProductName + ', ' + $info.CompanyName + ')')
    $run = Get-RunValue
    if ($run -cne $RunCommand) { Fail ('Run value is "' + $run + '", expected "' + $RunCommand + '"') }
    Note ('Run value: IDEA Armory = ' + $run)
    $key = $UsbKey; $other = $InnoKey
    if ($Entry -eq 'Setup') { $key = $InnoKey; $other = $UsbKey }
    if (-not (Test-Path -LiteralPath $key)) { Fail ('The Apps entry is missing: ' + $key) }
    if (Test-Path -LiteralPath $other) { Fail ('A second Apps entry exists: ' + $other) }
    $u = Get-ItemProperty -LiteralPath $key
    $wanted = [ordered]@{ DisplayName = 'IDEA Armory'; Publisher = 'IDEA, Don Bosco Tech'; DisplayVersion = $Expect }
    foreach ($name in $wanted.Keys) { if ($u.$name -cne $wanted[$name]) { Fail ('Apps entry ' + $name + ' is "' + $u.$name + '", expected "' + $wanted[$name] + '"') } }
    foreach ($name in @('DisplayIcon', 'UninstallString', 'QuietUninstallString')) { if (-not $u.$name) { Fail ('Apps entry has no ' + $name) } }
    foreach ($name in @('NoModify', 'NoRepair')) { if ($u.$name -ne 1) { Fail ('Apps entry ' + $name + ' is "' + $u.$name + '", expected 1') } }
    if (-not ($u.EstimatedSize -gt 0)) { Fail 'Apps entry has no EstimatedSize' }
    if ($u.DisplayIcon -notlike '*IdeaArmory.exe*') { Fail ('Apps entry DisplayIcon is ' + $u.DisplayIcon) }
    Note ('Apps entry: ' + $u.DisplayName + ' ' + $u.DisplayVersion + ' by ' + $u.Publisher + '; icon ' + $u.DisplayIcon + '; uninstall ' + $u.UninstallString + '; quiet ' + $u.QuietUninstallString + '; ' + $u.EstimatedSize + ' KB')
    if (-not (Test-Path -LiteralPath $Shortcut)) { Fail ('The Start menu shortcut is missing: ' + $Shortcut) }
    Note ('Start menu: ' + $Shortcut)
    $check = Invoke-AppCheck
    Note ('IdeaArmory.exe --check: exit ' + $check.Code + ': ' + $check.Raw)
    if ($check.Code -ne 0) { Fail 'IdeaArmory.exe --check did not exit 0' }
    if (-not $check.Json) { Fail 'IdeaArmory.exe --check printed no JSON line' }
    if ($check.Json.version -ne $Expect) { Fail ('--check version is ' + $check.Json.version + ', expected ' + $Expect) }
    if ($check.Json.wwwroot -ne $true) { Fail '--check reports wwwroot missing' }
    if (-not $check.Json.webView2Runtime) { Fail '--check reports no WebView2 runtime' }
    if (-not $check.Json.vaultRoot) { Fail '--check reports no vaultRoot' }
    if (-not (Wait-Until { @(Get-ArmoryPids).Count -gt 0 } 30)) { Fail ('IdeaArmory.exe is not running from ' + $Exe) }
    Note ('running: process ' + (@(Get-ArmoryPids) -join ', ') + ' from ' + $Exe)
    $started = 'started ' + $Expect
    if (-not (Wait-Until { (Read-AgentLog).Contains($started) } 30)) { Fail ('agent.log has no "' + $started + '" line') }
    Note ('agent.log: "' + $started + '" found in ' + $AgentLog)
}

# ---- This version's per-user wiring ----------------------------------------------------------

function Test-UserKey([string]$key) {
    $open = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
    if (-not $open) { return $false }
    $open.Close()
    return $true
}
# A value under HKCU with its kind, or $null.
function Get-UserValue([string]$key, [string]$name) {
    $open = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
    if (-not $open) { return $null }
    try {
        $value = $open.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($null -eq $value) { return $null }
        return [pscustomobject]@{ Value = $value; Kind = $open.GetValueKind($name) }
    } finally { $open.Close() }
}
function Assert-UserValue([string]$key, [string]$name, $want) {
    $kind = [Microsoft.Win32.RegistryValueKind]::String
    if ($want -is [int]) { $kind = [Microsoft.Win32.RegistryValueKind]::DWord }
    $got = Get-UserValue $key $name
    $shown = 'HKCU\' + $key + ' ' + $(if ($name) { $name } else { '(Default)' })
    if (-not $got) { Fail ($shown + ' is missing') }
    if ($got.Kind -ne $kind -or $got.Value -cne $want) { Fail ($shown + ' is ' + $got.Kind + ' "' + $got.Value + '", expected ' + $kind + ' "' + $want + '"') }
}
# The notification registration and the idea-armory: scheme, as ShellIdentity.Layout and both
# installers write them.
function Assert-Identity {
    $quotedExe = '"' + $Exe + '"'
    Assert-UserValue $IdentityKey 'DisplayName' 'IDEA Armory'
    Assert-UserValue $IdentityKey 'IconUri' (Join-Path $Target 'Assets\armory.ico')
    Assert-UserValue $SchemeKey '' 'URL:IDEA Armory'
    Assert-UserValue $SchemeKey 'URL Protocol' ''
    Assert-UserValue ($SchemeKey + '\DefaultIcon') '' ($quotedExe + ',0')
    Assert-UserValue ($SchemeKey + '\shell\open\command') '' ($quotedExe + ' "%1"')
    if (-not (Test-Path -LiteralPath (Join-Path $Target 'Assets\armory.ico'))) { Fail 'The registration names Assets\armory.ico, which is not in the program folder' }
    Note ('notification registration and idea-armory: scheme: exact, for ' + $Exe)
}
# File Explorer's right-click items, as ShellVerbs.Layout writes them for this install and the
# vault root (docs/agent/EXPLORER.md 1.1). Armory writes them after it starts, off its window's
# thread, so this waits for them.
function Assert-RightClickItems([string]$VaultRoot = $Vault) {
    $inside = 'System.ItemPathDisplay:~<"' + $VaultRoot + '\"'
    if (-not (Wait-Until { $v = Get-UserValue $FilesVerb 'AppliesTo'; $v -and $v.Value -ceq $inside } 60)) {
        Fail ('Armory did not write its right-click items for ' + $VaultRoot + ' within 60 seconds (AppliesTo is "' + (Get-UserValue $FilesVerb 'AppliesTo').Value + '")')
    }
    $icon = '"' + $Exe + '",0'
    $forwarder = '"' + (Join-Path $Target 'ArmoryShell.exe') + '"'
    Assert-UserValue $FilesVerb 'MUIVerb' 'IDEA Armory'
    Assert-UserValue $FilesVerb 'Icon' $icon
    Assert-UserValue $FilesVerb 'ExtendedSubCommandsKey' 'IDEAArmory.Menu'
    Assert-UserValue $FilesVerb 'MultiSelectModel' 'Player'
    $items = @(
        @{ Key = '01checkout'; Label = 'Check out'; Verb = 'checkout'; Select = 'Player' },
        @{ Key = '02checkoutopen'; Label = 'Check out and open'; Verb = 'checkoutopen'; Select = 'Single' },
        @{ Key = '03checkin'; Label = 'Check in'; Verb = 'checkin'; Select = 'Player' },
        @{ Key = '04undo'; Label = 'Undo check out'; Verb = 'undo'; Select = 'Player' },
        @{ Key = '05show'; Label = 'Show in Armory'; Verb = 'show'; Select = 'Single' })
    foreach ($item in $items) {
        $at = $FilesMenu + '\shell\' + $item.Key
        Assert-UserValue $at 'MUIVerb' $item.Label
        Assert-UserValue $at 'MultiSelectModel' $item.Select
        Assert-UserValue ($at + '\command') '' ($forwarder + ' ' + $item.Verb + ' "%1"')
    }
    Assert-UserValue ($FilesMenu + '\shell\05show') 'CommandFlags' 0x20
    # Nobody is signed in to a server here, so no project allows a force check in.
    if (Test-UserKey ($FilesMenu + '\shell\06forcecheckin')) { Fail 'Force check in is on the menu with no project that allows it' }
    Assert-UserValue $BackgroundVerb 'MUIVerb' 'IDEA Armory'
    Assert-UserValue $BackgroundVerb 'Icon' $icon
    Assert-UserValue $BackgroundVerb 'AppliesTo' ('System.ItemPathDisplay:="' + $VaultRoot + '" OR ' + $inside)
    Assert-UserValue $BackgroundVerb 'ExtendedSubCommandsKey' 'IDEAArmory.BackgroundMenu'
    Assert-UserValue ($BackgroundMenu + '\shell\01checkin') 'MUIVerb' 'Check in'
    Assert-UserValue ($BackgroundMenu + '\shell\01checkin\command') '' ($forwarder + ' checkin "%V"')
    Assert-UserValue ($BackgroundMenu + '\shell\02show') 'MUIVerb' 'Show in Armory'
    Assert-UserValue ($BackgroundMenu + '\shell\02show\command') '' ($forwarder + ' show "%V"')
    Note ('right-click items: exact, run ' + $forwarder + ', inside ' + $VaultRoot)
}
# System.AppUserModel.ID on the Start menu shortcut, read the way Windows documents it (the
# shell's link object and its property store), as ShortcutAppId does.
function Initialize-ShortcutReader {
    if ('ArmoryInstallShortcut' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class ArmoryInstallShortcut
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public int PropertyId; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
    public static string AppId(string shortcut)
    {
        object link = new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcut, 0);
            PropertyKey key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            PropVariant value;
            if (((IPropertyStore)link).GetValue(ref key, out value) != 0) return null;
            // VT_LPWSTR (Armory's own stamp) or VT_BSTR (Inno Setup's [Icons] AppUserModelID).
            try { return (value.Type == 31 || value.Type == 8) && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : (value.Type == 0 ? null : "(a value of type " + value.Type + ")"); }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }
}
'@
}
function Assert-ShortcutAppId {
    Initialize-ShortcutReader
    $script:ShortcutId = $null
    $read = { try { $script:ShortcutId = [ArmoryInstallShortcut]::AppId($Shortcut) } catch { $script:ShortcutId = 'unreadable: ' + $_.Exception.Message }; $script:ShortcutId -ceq $Aumid }
    if (-not (Wait-Until $read 60)) {
        $said = @((Read-AgentLog) -split "`r?`n" | Where-Object { $_ -match 'shortcut' }) -join ' / '
        Fail ('The Start menu shortcut''s AppUserModelID is "' + $script:ShortcutId + '", expected ' + $Aumid + '; agent.log about the shortcut: ' + $said)
    }
    Note ('Start menu shortcut: System.AppUserModel.ID = ' + $Aumid)
}
# A notification's link as Windows starts it (the scheme's registered command): handed to the
# running Armory, which takes it, and no second Armory stays.
function Assert-LinkLaunch {
    $command = [string](Get-UserValue ($SchemeKey + '\shell\open\command') '').Value
    $match = [regex]::Match($command, '^"([^"]+)" "%1"$')
    if (-not $match.Success -or -not [string]::Equals($match.Groups[1].Value, $Exe, [StringComparison]::OrdinalIgnoreCase)) { Fail ('The idea-armory: command is ' + $command) }
    $before = @(Get-ArmoryPids)
    if ($before.Count -ne 1) { Fail ('Expected one running IdeaArmory.exe before the link, found ' + $before.Count) }
    $taken = 'shell: uri, 1 item'
    $seen = ([regex]::Matches((Read-AgentLog), [regex]::Escape($taken))).Count
    $run = Invoke-Captured 'link-launch' $match.Groups[1].Value @($BogusLink) 60
    if ($run.Code -ne 0) { Fail ('IdeaArmory.exe "' + $BogusLink + '" returned ' + $run.Code + ', expected 0 (the running Armory took it)') }
    if (-not (Wait-Until { ([regex]::Matches((Read-AgentLog), [regex]::Escape($taken))).Count -gt $seen } 30)) { Fail ('The running Armory did not log "' + $taken + '" for the link') }
    if (-not (Wait-Until { @(Get-ArmoryPids).Count -eq 1 } 15)) { Fail ('A second IdeaArmory.exe stayed after the link: ' + (@(Get-ArmoryPids) -join ', ')) }
    $after = @(Get-ArmoryPids)
    if ($after[0] -ne $before[0]) { Fail ('The running Armory changed from ' + $before[0] + ' to ' + $after[0] + ' after the link') }
    Note ('link "' + $BogusLink + '": exit 0, the running Armory (' + $after[0] + ') logged "' + $taken + '", no second one stayed')
}
# The payload as both routes ship it: the forwarder beside IdeaArmory.exe and the badges setup,
# each with this version's resources; never a test tool or a SolidWorks DLL.
function Assert-Payload([string]$Folder, [string]$Label) {
    foreach ($relative in @('IdeaArmory.exe', 'ArmoryShell.exe', 'badges\IDEA-Armory-Badges-Setup.exe')) {
        $file = Join-Path $Folder $relative
        if (-not (Test-Path -LiteralPath $file)) { Fail ($Label + ' has no ' + $relative) }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
        $actual = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
        # A setup built by Inno Setup pads its strings with spaces.
        $product = ([string]$info.ProductName).TrimEnd()
        if ($product -cne 'IDEA Armory' -or $actual -ne $Version) { Fail ($Label + '\' + $relative + ' carries "' + $product + '" ' + $actual + ', expected "IDEA Armory" ' + $Version) }
        Note ($Label + '\' + $relative + ': ' + $product + ' ' + $actual + ' by ' + ([string]$info.CompanyName).TrimEnd())
    }
    $never = @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Force | Where-Object { $_.Name -in @('BadgeProbe.exe', 'ShellPipeTest.exe') -or $_.Name -like 'SolidWorks.Interop*' })
    if ($never.Count -gt 0) { Fail ($Label + ' ships ' + (($never | ForEach-Object { $_.Name }) -join ', ')) }
}

# ---- No SolidWorks registry footprint (docs/agent/SOLIDWORKS.md) ------------------------------

# Every value under a key, one line each, in a stable order; $null when the key is not there.
function Get-RegistryDump([Microsoft.Win32.RegistryHive]$Hive, [Microsoft.Win32.RegistryView]$View, [string]$Path) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
    try {
        $key = $base.OpenSubKey($Path)
        if (-not $key) { return $null }
        $lines = [Collections.Generic.List[string]]::new()
        $walk = {
            param($at, [string]$name)
            foreach ($value in @($at.GetValueNames() | Sort-Object)) {
                $data = @($at.GetValue($value, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)) -join ','
                $lines.Add($name + '|' + $value + '|' + $at.GetValueKind($value) + '|' + $data)
            }
            foreach ($sub in @($at.GetSubKeyNames() | Sort-Object)) {
                $lines.Add($name + '\' + $sub)
                $child = $at.OpenSubKey($sub)
                if ($child) { try { & $walk $child ($name + '\' + $sub) } finally { $child.Close() } }
            }
        }
        try { & $walk $key $Path } finally { $key.Close() }
        return ($lines -join "`n")
    } finally { $base.Close() }
}
function Get-SolidWorksRegistry {
    return @{
        User = Get-RegistryDump CurrentUser Registry64 'Software\SolidWorks'
        Machine = Get-RegistryDump LocalMachine Registry64 'SOFTWARE\SolidWorks'
        Machine32 = Get-RegistryDump LocalMachine Registry32 'SOFTWARE\SolidWorks'
    }
}
function Save-SolidWorksRegistry {
    $script:SolidWorksBefore = Get-SolidWorksRegistry
    $there = @(foreach ($name in @('User', 'Machine', 'Machine32')) { if ($null -ne $script:SolidWorksBefore[$name]) { $name } })
    Note ('SolidWorks registry before: ' + $(if ($there.Count) { 'present (' + ($there -join ', ') + '), kept as it is' } else { 'none' }))
}
function Assert-NoSolidWorksFootprint([string]$When) {
    $now = Get-SolidWorksRegistry
    if ($null -eq $script:SolidWorksBefore.User -and $null -ne $now.User) { Fail ($When + ': something wrote HKCU\Software\SolidWorks') }
    if ($now.User -cne $script:SolidWorksBefore.User) { Fail ($When + ': HKCU\Software\SolidWorks changed') }
    if ($now.Machine -cne $script:SolidWorksBefore.Machine) { Fail ($When + ': HKLM\SOFTWARE\SolidWorks changed') }
    if ($now.Machine32 -cne $script:SolidWorksBefore.Machine32) { Fail ($When + ': HKLM\SOFTWARE\WOW6432Node\SolidWorks changed') }
    # No class of Armory's registered for this account: none of the badge CLSIDs (those are for the
    # whole computer, by the badges setup) and nothing that names Armory.
    $classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\CLSID')
    if ($classes) {
        try {
            foreach ($clsid in $classes.GetSubKeyNames()) {
                if ($Badges | Where-Object { $_.Clsid -ieq $clsid }) { Fail ($When + ': HKCU\Software\Classes\CLSID\' + $clsid + ' is a badge class registered for this account') }
                $texts = @()
                foreach ($sub in @('', 'InprocServer32', 'LocalServer32')) {
                    $key = $classes.OpenSubKey($(if ($sub) { $clsid + '\' + $sub } else { $clsid }))
                    if ($key) { $texts += [string]$key.GetValue(''); $key.Close() }
                }
                if (($texts -join ' ') -match '(?i)armory') { Fail ($When + ': HKCU\Software\Classes\CLSID\' + $clsid + ' names Armory: ' + ($texts -join ' | ')) }
            }
        } finally { $classes.Close() }
    }
    Note ($When + ': no SolidWorks registry footprint (HKCU\Software\SolidWorks, HKLM\SOFTWARE\SolidWorks as before; no Armory class under HKCU\Software\Classes\CLSID)')
}

# Everything an install of this version wires up for the person, after Armory has started.
function Assert-Wiring([string]$When) {
    Assert-Identity
    Assert-RightClickItems
    Assert-ShortcutAppId
    Assert-Payload $Target 'the program folder'
    Assert-NoSolidWorksFootprint $When
}

# ---- The badges, for the whole computer ------------------------------------------------------

function Get-MachineValue([string]$Path, [string]$Name) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey($Path)
        if (-not $key) { return $null }
        try {
            $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($null -eq $value) { return $null }
            return [pscustomobject]@{ Value = $value; Kind = $key.GetValueKind($Name) }
        } finally { $key.Close() }
    } finally { $base.Close() }
}
function Test-MachineKey([string]$Path) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try { $key = $base.OpenSubKey($Path); if ($key) { $key.Close(); return $true }; return $false } finally { $base.Close() }
}
function Assert-MachineString([string]$Path, [string]$Name, [string]$Want) {
    $got = Get-MachineValue $Path $Name
    $shown = 'HKLM\' + $Path + ' ' + $(if ($Name) { $Name } else { '(Default)' })
    if (-not $got) { Fail ($shown + ' is missing') }
    if ($got.Kind -ne [Microsoft.Win32.RegistryValueKind]::String -or [string]$got.Value -cne $Want) { Fail ($shown + ' is ' + $got.Kind + ' "' + $got.Value + '", expected String "' + $Want + '"') }
}
# Files Windows was asked to delete or replace at the next restart that are the badges'.
function Get-PendingBadgeRenames {
    $pending = Get-MachineValue 'SYSTEM\CurrentControlSet\Control\Session Manager' 'PendingFileRenameOperations'
    if (-not $pending) { return @() }
    return @(@($pending.Value) | Where-Object { $_ -and $_.IndexOf('IDEA Armory Badges', [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}
# Every key and value of installer\IdeaArmoryBadges.iss with its exact data, the DLL where they
# point, one version folder, and nothing waiting for a restart.
function Assert-BadgesInstalled([string]$When) {
    foreach ($badge in $Badges) {
        $class = 'SOFTWARE\Classes\CLSID\' + $badge.Clsid
        Assert-MachineString $class '' $badge.Text
        Assert-MachineString ($class + '\InprocServer32') '' $BadgesDll
        Assert-MachineString ($class + '\InprocServer32') 'ThreadingModel' 'Apartment'
        Assert-MachineString ($OverlaysKey + '\' + $badge.Key) '' $badge.Clsid
        Assert-MachineString $ApprovedKey $badge.Clsid $badge.Text
    }
    Assert-MachineString $BadgesSetupKey 'Version' $Version
    Assert-MachineString $BadgesSetupKey 'Format' '1'
    $installedAt = Get-MachineValue $BadgesSetupKey 'InstalledAt'
    if (-not $installedAt -or $installedAt.Kind -ne [Microsoft.Win32.RegistryValueKind]::QWord) { Fail ('HKLM\' + $BadgesSetupKey + ' InstalledAt is not a REG_QWORD') }
    $when = [DateTime]::FromFileTimeUtc([long]$installedAt.Value)
    $age = [DateTime]::UtcNow - $when
    if ($age.TotalHours -gt 2 -or $age.TotalMinutes -lt -5) { Fail ('HKLM\' + $BadgesSetupKey + ' InstalledAt is ' + $when.ToString('o') + ', not the time of this install') }
    Assert-MachineString $BadgesAppsKey 'DisplayName' 'IDEA Armory badges (status on file icons)'
    Assert-MachineString $BadgesAppsKey 'DisplayVersion' $Version
    Assert-MachineString $BadgesAppsKey 'Publisher' 'IDEA, Don Bosco Tech'
    if (-not (Test-Path -LiteralPath $BadgesDll)) { Fail ('The registered DLL ' + $BadgesDll + ' is missing') }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($BadgesDll)
    $actual = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
    if ($info.ProductName -cne 'IDEA Armory' -or $actual -ne $Version) { Fail ($BadgesDll + ' carries "' + $info.ProductName + '" ' + $actual) }
    $folders = @(Get-ChildItem -LiteralPath $BadgesFolder -Directory -Force | ForEach-Object { $_.Name })
    if (($folders -join ',') -ne $Version) { Fail ('Version folders in ' + $BadgesFolder + ': ' + ($folders -join ', ') + ', expected only ' + $Version) }
    $pending = @(Get-PendingBadgeRenames)
    if ($pending.Count -gt 0) { Fail ('Files of the badges wait for a restart: ' + ($pending -join ', ')) }
    Note ($When + ': every badges key and value exact (4 classes, 4 overlay identifiers, 4 approvals, Version ' + $Version + ', Format 1, InstalledAt ' + $when.ToString('u') + ', the Apps entry); ' + $BadgesDll + ' ' + $actual + '; nothing waits for a restart')
}
function Assert-NoBadges([string]$When) {
    $found = @()
    foreach ($badge in $Badges) {
        if (Test-MachineKey ('SOFTWARE\Classes\CLSID\' + $badge.Clsid)) { $found += ('class ' + $badge.Clsid) }
        if (Test-MachineKey ($OverlaysKey + '\' + $badge.Key)) { $found += ('overlay identifier "' + $badge.Key + '"') }
        if (Get-MachineValue $ApprovedKey $badge.Clsid) { $found += ('approval ' + $badge.Clsid) }
    }
    if (Test-MachineKey 'SOFTWARE\IDEA Armory') { $found += 'HKLM\SOFTWARE\IDEA Armory' }
    if (Test-MachineKey $BadgesAppsKey) { $found += 'the Apps entry' }
    $queued = @()
    if (Test-Path -LiteralPath $BadgesFolder) {
        # A DLL that a running Explorer loaded is queued for deletion at the next restart
        # (uninsrestartdelete); anything else left is a failure.
        $pending = @(Get-PendingBadgeRenames)
        foreach ($file in @(Get-ChildItem -LiteralPath $BadgesFolder -Recurse -File -Force)) {
            if (@($pending | Where-Object { $_.EndsWith($file.FullName, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { $queued += $file.FullName }
            else { $found += $file.FullName }
        }
    }
    if ($found.Count -gt 0) { Fail ($When + ': ' + ($found -join '; ') + ' still present') }
    if ($queued.Count -gt 0) { Note ($When + ': ' + ($queued -join ', ') + ' still loaded somewhere, queued for deletion when Windows next starts') }
    Note ($When + ': no badges key, value, file or Apps entry')
}
function Initialize-IconReader {
    if ('ArmoryInstallIcons' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ArmoryInstallIcons
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    public static uint Count(string file) { return ExtractIconExW(file, -1, null, null, 0); }
    // True when the icon at index gives both a large and a small icon.
    public static bool Has(string file, int index)
    {
        IntPtr[] large = new IntPtr[1], small = new IntPtr[1];
        uint got = ExtractIconExW(file, index, large, small, 1);
        bool ok = got > 0 && large[0] != IntPtr.Zero && small[0] != IntPtr.Zero;
        if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        return ok;
    }
}
'@
}

# ---- The fake SolidWorks (tests/Armory.FakeSolidWorks), for an upgrade with SolidWorks open ---

# The fake from the solution's build, or $null when this build has none.
function Find-FakeSolidWorks {
    $folder = Join-Path $root 'tests\Armory.FakeSolidWorks\bin'
    if (-not (Test-Path -LiteralPath $folder)) { return $null }
    return (Get-ChildItem -LiteralPath $folder -Recurse -File -Filter 'Armory.FakeSolidWorks.exe' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
}
# Starts it with its commands on a pipe and its lines in an evidence file.
function Start-FakeSolidWorks([string]$File) {
    $out = Join-Path $Evidence ($Tag + '-fake-solidworks.txt')
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\cmd.exe'))
    $info.Arguments = '/d /s /c ""' + $File + '" --revision 33.5.0 --start-after 500 > "' + $out + '" 2>&1"'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $process = [Diagnostics.Process]::Start($info)
    return [pscustomobject]@{ Process = $process; Out = $out; Pid = 0 }
}
function Read-FakeLines($Fake) {
    if (-not (Test-Path -LiteralPath $Fake.Out)) { return @() }
    $stream = [IO.FileStream]::new($Fake.Out, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try { return @([IO.StreamReader]::new($stream).ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $stream.Dispose() }
}
function Send-Fake($Fake, [string]$Command) {
    $Fake.Process.StandardInput.WriteLine($Command)
    $Fake.Process.StandardInput.Flush()
}
# The first line from index From on that matches, waiting up to Seconds.
function Wait-FakeLine($Fake, [scriptblock]$Match, [int]$Seconds, [int]$From = 0) {
    $script:FakeLine = $null
    $found = Wait-Until {
        $lines = @(Read-FakeLines $Fake)
        for ($i = $From; $i -lt $lines.Count; $i++) { if (& $Match $lines[$i]) { $script:FakeLine = $lines[$i]; return $true } }
        return $false
    } $Seconds
    if (-not $found) { Fail ('The fake SolidWorks never wrote the line waited for. It wrote: ' + ((Read-FakeLines $Fake) -join ' / ')) }
    return $script:FakeLine
}
# Its own count of the references and sinks others hold ("refs app=<n> docs=<n> sinks=<n> ...").
function Get-FakeSinks($Fake) {
    $from = @(Read-FakeLines $Fake).Count
    Send-Fake $Fake 'refs'
    $line = Wait-FakeLine $Fake { param($l) $l.StartsWith('refs ') } 30 $from
    return [int]([regex]::Match($line, ' sinks=(\d+)').Groups[1].Value)
}
function Stop-FakeSolidWorks($Fake) {
    if (-not $Fake -or $Fake.Process.HasExited) { return }
    try { Send-Fake $Fake 'exit' } catch {}
    if (-not $Fake.Process.WaitForExit(30000)) { try { $Fake.Process.Kill($true) } catch {} }
}

# unins000.exe /VERYSILENT from the Apps entry, waiting for its copy in TEMP to finish.
function Invoke-SetupUninstall([string]$Label, [string]$InnoLog) {
    $uninstallString = [string](Get-ItemProperty -LiteralPath $InnoKey).UninstallString
    $uninstaller = [regex]::Match($uninstallString, '^"([^"]+)"').Groups[1].Value
    if (-not $uninstaller) { $uninstaller = ($uninstallString -split ' ')[0] }
    if (-not [string]::Equals($uninstaller, (Join-Path $Target 'unins000.exe'), [StringComparison]::OrdinalIgnoreCase)) { Fail ('UninstallString points at ' + $uninstaller) }
    [void](Expect $Label $uninstaller ($silent -f (Join-Path $Evidence $InnoLog)) $true)
    # The uninstaller returns while its copy in TEMP is still removing files.
    $done = Wait-Until { -not (Test-Path -LiteralPath $InnoKey) -and -not (Test-Path -LiteralPath $Target) -and @(Get-Process -Name '_iu*' -ErrorAction SilentlyContinue).Count -eq 0 } 120
    if (-not $done) { Note 'the uninstaller was still finishing after 120 seconds' }
}

# Downloads the published release's flash-drive zip and setup.exe (the repository is public, so
# no token) into RUNNER_TEMP, never into dist, and checks each against its .sha256 file.
function Get-OldRelease {
    $folder = Join-Path $temp ('armory-release-v' + $From)
    [void][IO.Directory]::CreateDirectory($folder)
    $base = $ReleaseUrl -f $From
    $saved = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        foreach ($name in @(('IDEA-Armory-USB-v' + $From + '.zip'), ('IDEA-Armory-Setup-v' + $From + '.exe'))) {
            foreach ($file in @($name, ($name + '.sha256'))) {
                $target = Join-Path $folder $file
                if (Test-Path -LiteralPath $target) { continue }
                for ($attempt = 1; ; $attempt++) {
                    try { Invoke-WebRequest -Uri ($base + $file) -OutFile $target -UseBasicParsing; break }
                    catch {
                        Remove-Item -LiteralPath $target -ErrorAction SilentlyContinue
                        if ($attempt -ge 3) { Fail ('Could not download ' + $base + $file + ': ' + $_.Exception.Message) }
                        Start-Sleep -Seconds (10 * $attempt)
                    }
                }
            }
            $line = (Get-Content -LiteralPath (Join-Path $folder ($name + '.sha256')) -Raw).Trim()
            $parts = @($line -split '\s+', 2)
            if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[0-9a-fA-F]{64}$' -or $parts[1].TrimStart('*') -cne $name) { Fail ('Unexpected ' + $name + '.sha256: ' + $line) }
            $actual = (Get-FileHash -LiteralPath (Join-Path $folder $name) -Algorithm SHA256).Hash
            if ($actual -ne $parts[0].ToUpperInvariant()) {
                Remove-Item -LiteralPath (Join-Path $folder $name) -ErrorAction SilentlyContinue
                Fail ($name + ' has SHA-256 ' + $actual + ', but its .sha256 says ' + $parts[0])
            }
            Note ('downloaded ' + $base + $name + ', SHA-256 ' + $actual + ' matches its .sha256')
        }
    } finally { $ProgressPreference = $saved }
    return $folder
}

# DPAPI exactly as src/Armory.Platform.Windows/DpapiSecretStore.cs calls it: CurrentUser,
# CRYPTPROTECT_UI_FORBIDDEN, description "IDEA Armory", entropy "IDEA Armory secret store v1/<name>".
function Initialize-Dpapi {
    if ('ArmoryUpgradeDpapi' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ArmoryUpgradeDpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] plain, byte[] entropy) { return Run(plain, entropy, true); }
    public static byte[] Unprotect(byte[] blob, byte[] entropy) { return Run(blob, entropy, false); }
    private static byte[] Run(byte[] data, byte[] entropy, bool protect)
    {
        GCHandle input = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            Blob dataIn = new Blob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            Blob extra = new Blob { Size = entropy.Length, Data = salt.AddrOfPinnedObject() };
            Blob output;
            bool ok = protect
                ? CryptProtectData(ref dataIn, "IDEA Armory", ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) return null;
            try
            {
                byte[] bytes = new byte[output.Size];
                if (output.Size > 0) Marshal.Copy(output.Data, bytes, 0, output.Size);
                return bytes;
            }
            finally { LocalFree(output.Data); }
        }
        finally { input.Free(); salt.Free(); }
    }
}
'@
}
$SecretHeader = [Text.Encoding]::ASCII.GetBytes("ARMORY-DPAPI-1`n")
$SecretEntropy = [Text.Encoding]::UTF8.GetBytes('IDEA Armory secret store v1/armory-session')

# The sign-in as src/Armory.Client/Session.cs stores it (SessionStorage.Stored, default JSON
# names). Its Supabase address answers nothing (port 9) and its token lasts until 2099, so
# neither version can refresh or refuse it: a missing or changed file after the upgrade can
# only be the installer's or the new version's doing.
function Get-TestSession {
    if (-not $script:SessionJson) {
        $script:SessionJson = '{"SupabaseUrl":"http://127.0.0.1:9","AnonKey":"upgrade-test-anon-key","AccessToken":"upgrade-test-access-token",' +
            '"RefreshToken":"upgrade-test-refresh-token","ExpiresAt":"2099-01-01T00:00:00+00:00","Email":"' + $SessionEmail + '",' +
            '"DeviceId":"' + [guid]::NewGuid().ToString() + '","DeviceName":"' + $env:COMPUTERNAME + '"}'
    }
    return $script:SessionJson
}
function Read-SignIn {
    $stored = [IO.File]::ReadAllBytes($SecretFile)
    if ($stored.Length -le $SecretHeader.Length) { return $null }
    for ($i = 0; $i -lt $SecretHeader.Length; $i++) { if ($stored[$i] -ne $SecretHeader[$i]) { return $null } }
    $blob = [byte[]]::new($stored.Length - $SecretHeader.Length)
    [Buffer]::BlockCopy($stored, $SecretHeader.Length, $blob, 0, $blob.Length)
    $plain = [ArmoryUpgradeDpapi]::Unprotect($blob, $SecretEntropy)
    if ($null -eq $plain) { return $null }
    return [Text.Encoding]::UTF8.GetString($plain)
}
# The log text written since the last "started <version>" line, or $null before that line.
function Get-LogSince([string]$version) {
    $text = Read-AgentLog
    $at = $text.LastIndexOf('started ' + $version)
    if ($at -lt 0) { return $null }
    return $text.Substring($at)
}
function Save-UpgradeState {
    Initialize-Dpapi
    # Earlier cycles may have made .armory already: the old version's own log line is the proof
    # that it opened this vault.
    if (-not (Wait-Until { [string](Get-LogSince $From) -match [regex]::Escape($VaultStarted) } 60)) { Fail ('IDEA Armory ' + $From + ' did not log "' + $VaultStarted + '"') }
    Note ('agent.log: ' + $From + ' logged "' + $VaultStarted + '"')
    # A read-only intent in the 0.1.0 format ({"<path>": n}, 0 = Free, which 0.1.0 left
    # writable) for a file the server does not have: 0.2.0 must never apply it.
    [IO.File]::WriteAllText($OldManifest, '{"Proof/keep.txt":0}', [Text.UTF8Encoding]::new($false))
    (Get-Item -LiteralPath $ProofFiles[0]).IsReadOnly = $false
    [IO.File]::WriteAllText($SettingsFile, '{"vaultRoot":"C:\\IDEA\\Armory","startAtSignIn":true,"theme":"spaceWhite"}', [Text.UTF8Encoding]::new($false))
    $blob = [ArmoryUpgradeDpapi]::Protect([Text.Encoding]::UTF8.GetBytes((Get-TestSession)), $SecretEntropy)
    if ($null -eq $blob) { Fail 'Windows could not protect the test sign-in' }
    $bytes = [byte[]]::new($SecretHeader.Length + $blob.Length)
    [Buffer]::BlockCopy($SecretHeader, 0, $bytes, 0, $SecretHeader.Length)
    [Buffer]::BlockCopy($blob, 0, $bytes, $SecretHeader.Length, $blob.Length)
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $SecretFile))
    [IO.File]::WriteAllBytes($SecretFile, $bytes)
    if ((Read-SignIn) -cne (Get-TestSession)) { Fail 'The planted sign-in does not read back' }
    [IO.File]::WriteAllText($StalePage, '// a page file only the old version shipped', [Text.UTF8Encoding]::new($false))
    $script:Kept = [ordered]@{}
    foreach ($file in @($SettingsFile, $SecretFile)) { $script:Kept[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    Note ('planted settings.json (theme spaceWhite), the sign-in ' + $SecretFile + ', a 0.1.0 read-only intent ' + $OldManifest + ' and ' + $StalePage)
    foreach ($file in $script:Kept.Keys) { Note ('    ' + $file + ' SHA-256 ' + $script:Kept[$file]) }
}
function Assert-UpgradeKept {
    foreach ($file in $script:Kept.Keys) {
        if (-not (Test-Path -LiteralPath $file)) { Fail ($file + ' is gone after the upgrade') }
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if ($hash -ne $script:Kept[$file]) { Fail ($file + ' changed during the upgrade') }
    }
    Note ('settings.json and the sign-in kept every byte: ' + (@($script:Kept.Keys) -join ', '))
    if ((Read-SignIn) -cne (Get-TestSession)) { Fail 'The sign-in no longer decrypts for this Windows account' }
    Note 'the sign-in still decrypts for this Windows account and holds the same session'
    # The new version itself, not only this script, read that sign-in, and opened the old vault.
    $loaded = 'session loaded for ' + $SessionEmail
    $since = [string](Get-LogSince $Version)
    if (-not $since.Contains($loaded)) { Fail ('IDEA Armory ' + $Version + ' did not load the kept sign-in: no "' + $loaded + '" after "started ' + $Version + '" in agent.log') }
    Note ('agent.log: ' + $Version + ' logged "' + $loaded + '"')
    if (-not (Wait-Until { [string](Get-LogSince $Version) -match [regex]::Escape($VaultStarted) } 60)) { Fail ('IDEA Armory ' + $Version + ' did not start its vault runtime on the old vault: no "' + $VaultStarted + '"') }
    Note ('agent.log: ' + $Version + ' logged "' + $VaultStarted + '"')
    if ((Get-Item -LiteralPath $ProofFiles[0]).IsReadOnly) { Fail ('A 0.1.0 read-only intent made ' + $ProofFiles[0] + ', a file the server does not have, read-only') }
    Note ('the 0.1.0 read-only intent was not applied: ' + $ProofFiles[0] + ' is still writable')
    if (Test-Path -LiteralPath $StalePage) { Fail ('A file of the old page survived the upgrade: ' + $StalePage) }
    Note 'no file of the old page survived (wwwroot)'
}

Remove-Item -LiteralPath $Log -ErrorAction SilentlyContinue
Note ('IDEA Armory ' + $Version + ' install cycle (' + $Kind + ') on ' + $env:COMPUTERNAME + ' as ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name + ', ' + [Environment]::OSVersion.VersionString + ', PowerShell ' + $PSVersionTable.PSVersion)
Confirm-WebView2

if ($Kind -eq 'Usb' -or -not (Test-Path -LiteralPath (Join-Path $Usb 'Check IDEA Armory.cmd'))) {
    Step ('extract ' + $zip.Name + ' as Extract All does')
    if (Test-Path -LiteralPath $Usb) { Remove-Item -LiteralPath $Usb -Recurse -Force }
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $Usb
    $top = @(Get-ChildItem -LiteralPath $Usb -Force | ForEach-Object { $_.Name } | Sort-Object)
    $wantTop = @('Check IDEA Armory.cmd', 'files', 'Install IDEA Armory.cmd', 'logs', 'README.txt', 'Show Armory status on file icons.cmd', 'Uninstall IDEA Armory.cmd') | Sort-Object
    if (($top -join '|') -ne ($wantTop -join '|')) { Fail ('The zip top level is ' + ($top -join ', ')) }
    Note ('layout: ' + ($top -join ', '))
    Assert-Payload (Join-Path $Usb 'files') 'the drive''s files'
}
$UsbInstall = Join-Path $Usb 'Install IDEA Armory.cmd'
$UsbUninstall = Join-Path $Usb 'Uninstall IDEA Armory.cmd'
$UsbCheck = Join-Path $Usb 'Check IDEA Armory.cmd'
$UsbBadges = Join-Path $Usb 'Show Armory status on file icons.cmd'
$DriveLog = Join-Path $Usb ('logs\' + $env:COMPUTERNAME + '.txt')
# Check's report of this version's wiring (Setup.ps1 -Mode Check).
function Assert-CheckReport($Run) {
    foreach ($line in @('Notifications:      registered', 'Link scheme:        registered', ('Right-click items:  present, inside ' + $Vault), 'File icons:         ')) {
        if (-not ([string]$Run.Output).Contains($line)) { Fail ('Check did not report "' + $line.Trim() + '"') }
    }
    Note 'Check reported the notification registration, the link scheme, the right-click items and the file icons'
}

if ($Kind -eq 'Usb') {
    Save-SolidWorksRegistry
    Step 'before: nothing installed'
    Assert-Clean 'before install' -Before
    Step 'Install IDEA Armory.cmd /quiet'
    [void](Expect 'install-1' $UsbInstall '/quiet' $true)
    Assert-Installed 'Usb'
    Step 'this version''s wiring: notifications, links, right-click items, shortcut, payload'
    Assert-Wiring 'after install'
    Assert-LinkLaunch
    Step 'vault files that uninstall must keep'
    Save-Proof
    Step 'Install IDEA Armory.cmd /quiet again (must be harmless)'
    $firstRun = @(Get-ArmoryPids)
    $second = Expect 'install-2' $UsbInstall '/quiet' $true
    if ($second.Output -notmatch 'IDEA Armory closed cleanly') { Fail 'The second install did not report that the running app closed cleanly after --quit' }
    Assert-Installed 'Usb'
    Assert-Restarted $firstRun
    Assert-Wiring 'after the second install'
    Assert-Proof
    Step 'Check IDEA Armory.cmd /quiet'
    Assert-CheckReport (Expect 'check-1' $UsbCheck '/quiet' $true)
    Step 'Uninstall IDEA Armory.cmd /quiet'
    $removed = Expect 'uninstall' $UsbUninstall '/quiet' $true
    if ($removed.Output -notmatch [regex]::Escape($Vault)) { Fail 'Uninstall did not say on screen that the vault folder was kept' }
    Assert-Clean 'after uninstall'
    Assert-NoSolidWorksFootprint 'after uninstall'
    Assert-Proof
    Step 'Check IDEA Armory.cmd /quiet after uninstall (must fail)'
    [void](Expect 'check-2' $UsbCheck '/quiet' $false)
    Step ('the drive log logs\' + $env:COMPUTERNAME + '.txt')
    $driveLog = $DriveLog
    if (-not (Test-Path -LiteralPath $driveLog)) { Fail 'The drive log is missing' }
    $lines = @(Get-Content -LiteralPath $driveLog)
    foreach ($line in $lines) { Note ('    ' + $line) }
    $wantLines = @(' INSTALL PASS ', ' INSTALL PASS ', ' CHECK PASS ', ' UNINSTALL PASS ', ' CHECK FAIL ')
    if ($lines.Count -ne $wantLines.Count) { Fail ('Expected ' + $wantLines.Count + ' drive log lines, found ' + $lines.Count) }
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if (-not $line.Contains($wantLines[$i])) { Fail ('Drive log line ' + ($i + 1) + ' should contain "' + $wantLines[$i].Trim() + '": ' + $line) }
        if ($line -notmatch '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} ' -or -not $line.Contains(' v' + $Version + ' ') -or $line -notmatch ' user=\S+' -or $line -notmatch ' seconds=([\d.]+) ') { Fail ('Drive log line ' + ($i + 1) + ' lacks the date, version, user or seconds: ' + $line) }
        if ($line.Contains(' INSTALL ') -and [double]($line -replace '^.* seconds=([\d.]+) .*$', '$1') -gt 120) { Fail ('An install took over 120 seconds: ' + $line) }
    }
} elseif ($Kind -eq 'Setup') {
    if (-not (Test-Path -LiteralPath $SetupExe)) { Fail ('Missing ' + $SetupExe) }
    $silent = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="{0}"'
    Save-SolidWorksRegistry
    Step 'before: nothing installed'
    Assert-Clean 'before install' -Before
    Step ((Split-Path -Leaf $SetupExe) + ' /VERYSILENT')
    [void](Expect 'setup-1' $SetupExe ($silent -f (Join-Path $Evidence 'setup-install-1.log')) $true)
    Assert-Installed 'Setup'
    Step 'this version''s wiring: notifications, links, right-click items, shortcut, payload'
    Assert-Wiring 'after install'
    Assert-LinkLaunch
    Step 'vault files that uninstall must keep'
    Save-Proof
    Step ((Split-Path -Leaf $SetupExe) + ' /VERYSILENT again (must be harmless)')
    $firstRun = @(Get-ArmoryPids)
    [void](Expect 'setup-2' $SetupExe ($silent -f (Join-Path $Evidence 'setup-install-2.log')) $true)
    Assert-Installed 'Setup'
    Assert-Restarted $firstRun
    Assert-Wiring 'after the second install'
    Assert-Proof
    Step 'the installed scripts\Check.cmd /quiet'
    Assert-CheckReport (Expect 'check-installed' (Join-Path $Target 'scripts\Check.cmd') '/quiet' $true)
    Step 'unins000.exe /VERYSILENT'
    Invoke-SetupUninstall 'unins000' 'setup-uninstall.log'
    Assert-Clean 'after uninstall'
    Assert-NoSolidWorksFootprint 'after uninstall'
    Assert-Proof
    Step 'Check IDEA Armory.cmd /quiet after uninstall (must fail)'
    [void](Expect 'check-after' $UsbCheck '/quiet' $false)
} elseif ($Kind -eq 'Upgrade') {
    if ([version]$From -ge [version]$Version) { Fail ('This build is ' + $Version + '; an upgrade from ' + $From + ' must go up') }
    if (-not (Test-Path -LiteralPath $SetupExe)) { Fail ('Missing ' + $SetupExe) }
    $silent = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="{0}"'
    Step ('download IDEA Armory ' + $From + ' from the published release')
    $old = Get-OldRelease
    $oldUsb = Join-Path $temp ('armory-usb-v' + $From)
    if (Test-Path -LiteralPath $oldUsb) { Remove-Item -LiteralPath $oldUsb -Recurse -Force }
    Expand-Archive -LiteralPath (Join-Path $old ('IDEA-Armory-USB-v' + $From + '.zip')) -DestinationPath $oldUsb
    $routes = @('Usb', 'Setup')
    if ($Route -ne 'Both') { $routes = @($Route) }
    Save-SolidWorksRegistry
    foreach ($way in $routes) {
        Step ($way + ': before, nothing installed')
        Assert-Clean 'before install' -Before
        if ($way -eq 'Usb') {
            Step ('IDEA Armory ' + $From + ': Install IDEA Armory.cmd /quiet from its flash-drive zip')
            [void](Expect ('usb-' + $From) (Join-Path $oldUsb 'Install IDEA Armory.cmd') '/quiet' $true)
        } else {
            Step ('IDEA Armory ' + $From + ': IDEA-Armory-Setup-v' + $From + '.exe /VERYSILENT')
            [void](Expect ('setup-' + $From) (Join-Path $old ('IDEA-Armory-Setup-v' + $From + '.exe')) ($silent -f (Join-Path $Evidence ('setup-upgrade-from-' + $From + '.log'))) $true)
        }
        Assert-Installed $way $From
        Step 'the vault, the settings and the sign-in the upgrade must keep'
        Save-Proof
        Save-UpgradeState
        $firstRun = @(Get-ArmoryPids)
        if ($way -eq 'Usb') {
            Step ('IDEA Armory ' + $Version + ': Install IDEA Armory.cmd /quiet over ' + $From)
            $upgraded = Expect ('usb-' + $Version) $UsbInstall '/quiet' $true
            if (-not ([string]$upgraded.Output).Contains('Upgraded IDEA Armory ' + $From + ' to ' + $Version)) { Fail ('The flash drive install did not report "Upgraded IDEA Armory ' + $From + ' to ' + $Version + '"') }
        } else {
            Step ('IDEA Armory ' + $Version + ': ' + (Split-Path -Leaf $SetupExe) + ' /VERYSILENT over ' + $From)
            [void](Expect ('setup-' + $Version) $SetupExe ($silent -f (Join-Path $Evidence ('setup-upgrade-to-' + $Version + '.log'))) $true)
        }
        Assert-Installed $way
        Assert-Restarted $firstRun
        # Let the new version run with the sign-in for a while before looking at it.
        Start-Sleep -Seconds 15
        if (@(Get-ArmoryPids).Count -eq 0) { Fail ('IDEA Armory ' + $Version + ' stopped running after the upgrade') }
        Assert-UpgradeKept
        Assert-Wiring 'after the upgrade'
        Assert-Proof
        Step ($way + ': uninstall')
        if ($way -eq 'Usb') { [void](Expect 'usb-uninstall' $UsbUninstall '/quiet' $true) }
        else { Invoke-SetupUninstall 'unins000' 'setup-upgrade-uninstall.log' }
        Assert-Clean 'after uninstall'
        Assert-NoSolidWorksFootprint 'after uninstall'
        Assert-Proof
    }
} elseif ($Kind -eq 'Badges') {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Fail 'The badges cycle installs for the whole computer, so it runs as an administrator (CI''s steps do)' }
    if (-not (Test-Path -LiteralPath $BadgesExe)) { Fail ('Missing ' + $BadgesExe) }
    $probe = Join-Path $Native 'x64\BadgeProbe.exe'
    if (-not (Test-Path -LiteralPath $probe)) { Fail ('Missing ' + $probe + ' (tools/build-native.ps1 builds it)') }
    $silent = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="{0}"'
    Step 'before: no badges on this computer'
    Assert-NoBadges 'before'
    Step ((Split-Path -Leaf $BadgesExe) + ' /VERYSILENT (as IT runs it)')
    [void](Expect 'badges-1' $BadgesExe ($silent -f (Join-Path $Evidence 'badges-install-1.log')) $true)
    Assert-BadgesInstalled 'after install'
    Step 'BadgeProbe.exe --attach --com: Windows creates the four handlers from the registration'
    $created = Invoke-Captured 'badgeprobe-com' $probe @('--attach', '--com') 120
    if ($created.Code -ne 0 -or -not ([string]$created.Output).Contains('PASS')) { Fail 'BadgeProbe.exe --attach --com did not pass' }
    for ($i = 0; $i -lt $Badges.Count; $i++) {
        $line = @(([string]$created.Output) -split "`r?`n" | Where-Object { $_.StartsWith('handler ' + $Badges[$i].Name + ':') }) | Select-Object -First 1
        if (-not $line -or -not $line.Contains('GetOverlayInfo 0x00000000 index ' + $i + ' ') -or $line.Contains('WRONG') -or $line.IndexOf('icon ' + $BadgesDll, [StringComparison]::OrdinalIgnoreCase) -lt 0) { Fail ('The ' + $Badges[$i].Name + ' handler answered: ' + $line) }
    }
    Note ('CoCreateInstance created all four handlers from ' + $BadgesDll + ', each with its icon index and priority')
    Step 'ExtractIconEx: the four badge icons'
    Initialize-IconReader
    $icons = [ArmoryInstallIcons]::Count($BadgesDll)
    if ($icons -lt $Badges.Count) { Fail ($BadgesDll + ' has ' + $icons + ' icons, expected ' + $Badges.Count) }
    for ($i = 0; $i -lt $Badges.Count; $i++) { if (-not [ArmoryInstallIcons]::Has($BadgesDll, $i)) { Fail ('ExtractIconEx gave no icon ' + $i + ' (' + $Badges[$i].Name + ')') } }
    Note ('ExtractIconEx: ' + $icons + ' icons, 0 to 3 each large and small')
    Step 'tools\check-overlays.ps1 under Windows PowerShell 5.1 (kept as evidence\check-overlays.txt)'
    $overlays = Invoke-Captured 'check-overlays' (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\check-overlays.ps1')) 120
    Copy-Item -LiteralPath $overlays.File -Destination (Join-Path $Evidence 'check-overlays.txt') -Force
    if ($overlays.Code -ne 0) { Fail 'check-overlays.ps1 failed' }
    foreach ($badge in $Badges) { if (-not ([string]$overlays.Output).Contains('<- Armory ' + $badge.Name)) { Fail ('check-overlays.ps1 does not list Armory''s ' + $badge.Name + ' badge') } }
    if (-not ([string]$overlays.Output).Contains('Badges setup: version ' + $Version)) { Fail 'check-overlays.ps1 does not show the badges setup''s version' }
    $verdict = [regex]::Match([string]$overlays.Output, 'Verdict \((\w+)\)').Groups[1].Value
    if ($verdict -in @('', 'off', 'broken')) { Fail ('check-overlays.ps1''s verdict is "' + $verdict + '"') }
    Note ('check-overlays.ps1 lists Armory''s four badges; verdict ' + $verdict)
    Step 'Show Armory status on file icons.cmd /quiet (Setup.ps1 -Mode Badges): a second run is clean'
    $drive = @()
    if (Test-Path -LiteralPath $DriveLog) { $drive = @(Get-Content -LiteralPath $DriveLog) }
    $again = Expect 'badges-2' $UsbBadges '/quiet' $true
    if (-not ([string]$again.Output).Contains('PASS')) { Fail 'The file icons step did not end on PASS' }
    $lines = @(Get-Content -LiteralPath $DriveLog)
    if ($lines.Count -ne $drive.Count + 1 -or -not $lines[-1].Contains(' BADGES PASS ') -or -not $lines[-1].Contains(' v' + $Version + ' ')) { Fail ('The drive log did not get one BADGES PASS line: ' + ($lines | Select-Object -Last 2)) }
    Note ('drive log: ' + $lines[-1])
    Assert-BadgesInstalled 'after the second run'
    Step 'the badges uninstaller /VERYSILENT'
    $uninstallString = [string](Get-MachineValue $BadgesAppsKey 'UninstallString').Value
    $uninstaller = [regex]::Match($uninstallString, '^"([^"]+)"').Groups[1].Value
    if (-not [string]::Equals($uninstaller, (Join-Path $BadgesFolder 'unins000.exe'), [StringComparison]::OrdinalIgnoreCase)) { Fail ('The badges UninstallString points at ' + $uninstallString) }
    [void](Expect 'badges-uninstall' $uninstaller ($silent -f (Join-Path $Evidence 'badges-uninstall.log')) $true)
    # The uninstaller returns while its copy in TEMP is still removing files.
    $done = Wait-Until { -not (Test-MachineKey $BadgesAppsKey) -and -not (Test-Path -LiteralPath $BadgesFolder) -and @(Get-Process -Name '_iu*' -ErrorAction SilentlyContinue).Count -eq 0 } 120
    if (-not $done) { Note 'the badges uninstaller was still finishing after 120 seconds' }
    Assert-NoBadges 'after uninstall'
} else {
    # SolidWorks: an upgrade with SolidWorks open, against the link's test fake.
    $fakeExe = Find-FakeSolidWorks
    if (-not $fakeExe) {
        Note 'SKIPPED: this build has no tests\Armory.FakeSolidWorks (the SolidWorks link''s test fake), so there is no SolidWorks to keep open.'
        Note ('PASS (nothing to run): the SolidWorks cycle for IDEA Armory ' + $Version)
        exit 0
    }
    # The link looks for processes by this name instead of SLDWORKS (a variable only tests set); the
    # installed Armory inherits it from the install.
    $env:ARMORY_SOLIDWORKS_PROCESS = [IO.Path]::GetFileNameWithoutExtension($fakeExe.FullName)
    Note ('fake SolidWorks: ' + $fakeExe.FullName + ' (ARMORY_SOLIDWORKS_PROCESS=' + $env:ARMORY_SOLIDWORKS_PROCESS + ')')
    Save-SolidWorksRegistry
    Step 'before: nothing installed'
    Assert-Clean 'before install' -Before
    Step 'Install IDEA Armory.cmd /quiet'
    [void](Expect 'install-1' $UsbInstall '/quiet' $true)
    Assert-Installed 'Usb'
    $fake = $null
    try {
        Step 'start the fake SolidWorks 2025 (revision 33.5.0) and wait for Armory to link to it'
        $fake = Start-FakeSolidWorks $fakeExe.FullName
        $ready = Wait-FakeLine $fake { param($l) $l.StartsWith('ready ') } 60
        $fake.Pid = [int]($ready.Substring(6).Trim())
        [void](Wait-FakeLine $fake { param($l) $l -eq 'started' } 30)
        $attached = 'solidworks link attached pid=' + $fake.Pid + ' revision=33.5.0'
        if (-not (Wait-Until { [string](Get-LogSince $Version) -match [regex]::Escape($attached) } 90)) { Fail ('Armory did not log "' + $attached + '"') }
        Note ('agent.log: "' + $attached + '"')
        $sinks = Get-FakeSinks $fake
        if ($sinks -lt 1) { Fail ('The linked Armory holds ' + $sinks + ' sinks in the fake SolidWorks') }
        $mark = @(Read-FakeLines $fake).Count
        $advised = @(Read-FakeLines $fake | Select-Object -First $mark | Where-Object { $_ -match '^advise \S+ \S+$' -and -not $_.StartsWith('advise refused') } | ForEach-Object { $_.Substring(7) })
        Note ('the old Armory advised ' + $advised.Count + ' sinks; the fake counts ' + $sinks + ' now')
        Step 'Install IDEA Armory.cmd /quiet again, with SolidWorks open'
        $firstRun = @(Get-ArmoryPids)
        [void](Expect 'install-2' $UsbInstall '/quiet' $true)
        Assert-Installed 'Usb'
        Assert-Restarted $firstRun
        if ($fake.Process.HasExited -or @(Read-FakeLines $fake) -contains 'exited') { Fail 'The fake SolidWorks closed during the install' }
        $lines = @(Read-FakeLines $fake)
        $unadvised = @($lines | Where-Object { $_ -match '^unadvise \S+ \S+$' } | ForEach-Object { $_.Substring(9) })
        $kept = @($advised | Where-Object { $unadvised -notcontains $_ })
        if ($kept.Count -gt 0) { Fail ('The old Armory left sinks advised in SolidWorks: ' + ($kept -join ', ')) }
        Note ('every sink the old Armory advised was unadvised (' + $advised.Count + ')')
        if (-not (Wait-Until { [string](Get-LogSince $Version) -match [regex]::Escape($attached) } 90)) { Fail ('The new Armory did not link to SolidWorks again: no "' + $attached + '" after its "started ' + $Version + '"') }
        $again = Get-FakeSinks $fake
        if ($again -lt 1) { Fail 'The new Armory holds no sink in the fake SolidWorks' }
        $errors = @(Read-FakeLines $fake | Where-Object { $_.StartsWith('error') -or $_.StartsWith('call CloseDoc') })
        if ($errors.Count -gt 0) { Fail ('The fake SolidWorks saw: ' + ($errors -join ' / ')) }
        Note ('the fake SolidWorks kept running and answering; the new Armory linked again and holds ' + $again + ' sinks')
    } finally { Stop-FakeSolidWorks $fake }
    Assert-NoSolidWorksFootprint 'after the upgrade with SolidWorks open'
    Step 'Uninstall IDEA Armory.cmd /quiet'
    [void](Expect 'uninstall' $UsbUninstall '/quiet' $true)
    Assert-Clean 'after uninstall'
    Assert-NoSolidWorksFootprint 'after uninstall'
}
Note ''
if ($Kind -eq 'Upgrade') { Note ('PASS: the upgrade from IDEA Armory ' + $From + ' to ' + $Version + ' (' + $Route + ') kept the vault, the settings and the sign-in') }
elseif ($Kind -eq 'Badges') { Note ('PASS: the badges setup ' + $Version + ' installed every key and file, Windows created all four handlers, a second run was clean, and its uninstaller removed everything') }
elseif ($Kind -eq 'SolidWorks') { Note ('PASS: IDEA Armory ' + $Version + ' was installed over itself with SolidWorks open; SolidWorks kept running and was linked again') }
else { Note ('PASS: the ' + $Kind + ' install, launch and uninstall cycle for IDEA Armory ' + $Version) }
