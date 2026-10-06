# The install, launch and uninstall cycle that CI runs on a clean windows-latest runner:
#   -Kind Usb    from the extracted flash-drive zip: Install /quiet twice, Check, Uninstall /quiet,
#                Check again (must fail), and the drive's logs\<COMPUTERNAME>.txt
#   -Kind Setup  with IDEA-Armory-Setup-v<version>.exe /VERYSILENT twice, the installed Check,
#                then unins000.exe /VERYSILENT
#   -Kind Upgrade  for the flash drive and for setup.exe (-Route Both, the default, or one of
#                them): download the published v<From> release (0.1.0 by default) into
#                RUNNER_TEMP and check each asset against its .sha256, install it and wait for
#                it to start, plant the vault's proof files, a settings.json with a non-default
#                theme and a sign-in in the exact DpapiSecretStore format, then install this
#                build over it. This build must run, the Apps entry must show its version, the
#                sign-in, the settings and the proof files must keep every byte, the sign-in
#                must still decrypt for this Windows account, and no file of the old page
#                (wwwroot) may survive. Then uninstall.
# Each cycle checks the exe, the start at sign-in value, the Apps entry, the Start menu
# shortcut, IdeaArmory.exe --check, the running process and agent.log, and that uninstall
# removes all of it while C:\IDEA\Armory\Proof\ keeps the same bytes.
#
# It installs and removes IDEA Armory for the current Windows account and writes into
# C:\IDEA\Armory\Proof. Run it only on a throwaway machine such as a CI runner.
param(
    [Parameter(Mandatory = $true)][ValidateSet('Usb', 'Setup', 'Upgrade')][string]$Kind,
    [string]$Dist = 'dist',
    [string]$Evidence = 'evidence',
    [ValidateSet('Both', 'Usb', 'Setup')][string]$Route = 'Both',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$From = '0.1.0',
    [string]$ReleaseUrl = 'https://github.com/pina-hash/idea-armory/releases/download/v{0}/'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Dist)) { $Dist = Join-Path $root $Dist }
if (-not [IO.Path]::IsPathRooted($Evidence)) { $Evidence = Join-Path $root $Evidence }
[void][IO.Directory]::CreateDirectory($Evidence)
$Log = Join-Path $Evidence ('install-cycle-' + $Kind.ToLowerInvariant() + '.txt')

$zip = Get-ChildItem -LiteralPath $Dist -Filter 'IDEA-Armory-USB-v*.zip' | Select-Object -First 1
if (-not $zip) { throw ('No IDEA-Armory-USB-v*.zip in ' + $Dist + '. Run tools/package-agent.ps1 first.') }
$Version = [regex]::Match($zip.Name, '^IDEA-Armory-USB-v(\d+\.\d+\.\d+)\.zip$').Groups[1].Value
if (-not $Version) { throw ('Unexpected zip name ' + $zip.Name) }
$SetupExe = Join-Path $Dist ('IDEA-Armory-Setup-v' + $Version + '.exe')
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
$script:Kept = [ordered]@{}
$script:SessionJson = $null

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
    $out = Join-Path $Evidence ('{0}-{1:00}-{2}.txt' -f $Kind.ToLowerInvariant(), $script:Runs, $Label)
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
    Note ($when + ': no program folder, Run value, Apps entry, shortcut, data folder or process')
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
            '"RefreshToken":"upgrade-test-refresh-token","ExpiresAt":"2099-01-01T00:00:00+00:00","Email":"upgrade.test@example.com",' +
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
function Save-UpgradeState {
    Initialize-Dpapi
    $private = Join-Path $Vault '.armory'
    if (-not (Test-Path -LiteralPath $private)) { Fail ('The old version did not create ' + $private) }
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
    Note ('planted settings.json (theme spaceWhite), the sign-in ' + $SecretFile + ' and ' + $StalePage)
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
    if (-not (Test-Path -LiteralPath (Join-Path $Vault '.armory'))) { Fail 'The vault lost its .armory folder' }
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
    $wantTop = @('Check IDEA Armory.cmd', 'files', 'Install IDEA Armory.cmd', 'logs', 'README.txt', 'Uninstall IDEA Armory.cmd') | Sort-Object
    if (($top -join '|') -ne ($wantTop -join '|')) { Fail ('The zip top level is ' + ($top -join ', ')) }
    Note ('layout: ' + ($top -join ', '))
}
$UsbInstall = Join-Path $Usb 'Install IDEA Armory.cmd'
$UsbUninstall = Join-Path $Usb 'Uninstall IDEA Armory.cmd'
$UsbCheck = Join-Path $Usb 'Check IDEA Armory.cmd'

if ($Kind -eq 'Usb') {
    Step 'before: nothing installed'
    Assert-Clean 'before install' -Before
    Step 'Install IDEA Armory.cmd /quiet'
    [void](Expect 'install-1' $UsbInstall '/quiet' $true)
    Assert-Installed 'Usb'
    Step 'vault files that uninstall must keep'
    Save-Proof
    Step 'Install IDEA Armory.cmd /quiet again (must be harmless)'
    $firstRun = @(Get-ArmoryPids)
    $second = Expect 'install-2' $UsbInstall '/quiet' $true
    if ($second.Output -notmatch 'IDEA Armory closed cleanly') { Fail 'The second install did not report that the running app closed cleanly after --quit' }
    Assert-Installed 'Usb'
    Assert-Restarted $firstRun
    Assert-Proof
    Step 'Check IDEA Armory.cmd /quiet'
    [void](Expect 'check-1' $UsbCheck '/quiet' $true)
    Step 'Uninstall IDEA Armory.cmd /quiet'
    $removed = Expect 'uninstall' $UsbUninstall '/quiet' $true
    if ($removed.Output -notmatch [regex]::Escape($Vault)) { Fail 'Uninstall did not say on screen that the vault folder was kept' }
    Assert-Clean 'after uninstall'
    Assert-Proof
    Step 'Check IDEA Armory.cmd /quiet after uninstall (must fail)'
    [void](Expect 'check-2' $UsbCheck '/quiet' $false)
    Step ('the drive log logs\' + $env:COMPUTERNAME + '.txt')
    $driveLog = Join-Path $Usb ('logs\' + $env:COMPUTERNAME + '.txt')
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
    Step 'before: nothing installed'
    Assert-Clean 'before install' -Before
    Step ((Split-Path -Leaf $SetupExe) + ' /VERYSILENT')
    [void](Expect 'setup-1' $SetupExe ($silent -f (Join-Path $Evidence 'setup-install-1.log')) $true)
    Assert-Installed 'Setup'
    Step 'vault files that uninstall must keep'
    Save-Proof
    Step ((Split-Path -Leaf $SetupExe) + ' /VERYSILENT again (must be harmless)')
    $firstRun = @(Get-ArmoryPids)
    [void](Expect 'setup-2' $SetupExe ($silent -f (Join-Path $Evidence 'setup-install-2.log')) $true)
    Assert-Installed 'Setup'
    Assert-Restarted $firstRun
    Assert-Proof
    Step 'the installed scripts\Check.cmd /quiet'
    [void](Expect 'check-installed' (Join-Path $Target 'scripts\Check.cmd') '/quiet' $true)
    Step 'unins000.exe /VERYSILENT'
    Invoke-SetupUninstall 'unins000' 'setup-uninstall.log'
    Assert-Clean 'after uninstall'
    Assert-Proof
    Step 'Check IDEA Armory.cmd /quiet after uninstall (must fail)'
    [void](Expect 'check-after' $UsbCheck '/quiet' $false)
} else {
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
        Assert-Proof
        Step ($way + ': uninstall')
        if ($way -eq 'Usb') { [void](Expect 'usb-uninstall' $UsbUninstall '/quiet' $true) }
        else { Invoke-SetupUninstall 'unins000' 'setup-upgrade-uninstall.log' }
        Assert-Clean 'after uninstall'
        Assert-Proof
    }
}
Note ''
if ($Kind -eq 'Upgrade') { Note ('PASS: the upgrade from IDEA Armory ' + $From + ' to ' + $Version + ' (' + $Route + ') kept the vault, the settings and the sign-in') }
else { Note ('PASS: the ' + $Kind + ' install, launch and uninstall cycle for IDEA Armory ' + $Version) }
