# Lists every File Explorer icon overlay (badge) handler in the order Explorer reads them, marks
# the ones within Windows' limit, and says whether IDEA Armory's four badges are loaded for the
# person signed in, ending with one verdict in the same words as Armory's Settings
# (docs/agent/EXPLORER.md, lab check L3). Read-only: it changes nothing. Windows PowerShell 5.1
# or PowerShell 7, no administrator needed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-overlays.ps1 [-Limit 11]
#
# Windows has 15 overlay slots and keeps 4 for itself, so only the first 11 handlers in this list
# are ever shown (learn.microsoft.com/previous-versions/troubleshoot/windows/win32/icon-overlay-handlers-windows-shell).
param([int]$Limit = 11)
$ErrorActionPreference = 'Stop'
# Started from PowerShell 7, Windows PowerShell 5.1 inherits PowerShell 7's module folders ahead
# of its own: it keeps only its own, its own first (as installer\scripts\Setup.ps1 does).
if ($PSVersionTable.PSEdition -ne 'Core') {
    $ownModules = Join-Path $PSHOME 'Modules'
    $keptModules = @($env:PSModulePath -split ';' | Where-Object { $_ -and ($_ -notmatch '\\PowerShell\\(7[^\\]*\\)?Modules\\?$') -and ($_ -ne $ownModules) })
    $env:PSModulePath = (@($ownModules) + $keptModules) -join ';'
}

# The same four as installer\IdeaArmoryBadges.iss and BadgeHealth.Badges.
$Armory = @(
    @{ Key = ' IDEAArmory1Attention'; Clsid = '{E26E19F2-515F-472F-AD4F-1B0293728CE2}'; Badge = 'Attention' },
    @{ Key = ' IDEAArmory2Mine'; Clsid = '{DB040D16-C118-4CDA-B616-DF9340A8BC9F}'; Badge = 'Mine' },
    @{ Key = ' IDEAArmory3Locked'; Clsid = '{DF50E3A9-57B8-44AE-B690-257AFF283F97}'; Badge = 'Locked' },
    @{ Key = ' IDEAArmory4Synced'; Clsid = '{58F5F8D8-1041-43B9-B8DE-0EBEBCC29CF0}'; Badge = 'Synced' })
function Get-ArmoryBadge([string]$name, [string]$clsid) {
    foreach ($a in $Armory) {
        if ($name -ieq $a.Key -or $clsid.Trim() -ieq $a.Clsid) { return $a.Badge }
    }
    return $null
}

$base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$classes = $base.OpenSubKey('SOFTWARE\Classes\CLSID')
$list = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers')
$setup = $base.OpenSubKey('SOFTWARE\IDEA Armory\Badges')
$installedVersion = $null
$installedFormat = $null
$installedAt = $null
if ($setup) {
    $installedVersion = $setup.GetValue('Version')
    $installedFormat = $setup.GetValue('Format')
    $when = $setup.GetValue('InstalledAt')
    if ($when) { $installedAt = [DateTime]::FromFileTimeUtc([long]$when) }
}

# GetSubKeyNames is the registry's own enumeration order, which is what Explorer reads. It is
# alphabetical without regard to case, so leading spaces sort first.
$names = @()
if ($list) { $names = @($list.GetSubKeyNames()) }
$sorted = @($names | Sort-Object { $_.ToUpperInvariant() } -CaseSensitive)
if (($names -join "`n") -cne ($sorted -join "`n")) { Write-Host 'Note: the registry order differs from a plain sort; the registry order is shown.' -ForegroundColor Yellow }

$rows = @()
for ($i = 0; $i -lt $names.Count; $i++) {
    $name = $names[$i]
    $clsid = ''
    $entry = $list.OpenSubKey($name)
    if ($entry) { $clsid = ([string]$entry.GetValue('')).Trim() }
    $dll = $null
    if ($clsid -and $classes) {
        $server = $classes.OpenSubKey($clsid + '\InprocServer32')
        if ($server) {
            $registered = [string]$server.GetValue('')
            if ($registered.Trim()) { $dll = [Environment]::ExpandEnvironmentVariables($registered.Trim().Trim('"')) }
        }
    }
    $rows += New-Object psobject -Property ([ordered]@{
            Position = $i + 1
            Shown = ($i -lt $Limit)
            Name = $name
            LeadingSpaces = $name.Length - $name.TrimStart(' ').Length
            Clsid = $clsid
            Dll = $dll
            DllFound = [bool]($dll -and (Test-Path -LiteralPath $dll))
            Armory = (Get-ArmoryBadge $name $clsid)
        })
}

Write-Host ''
Write-Host ('Icon overlay handlers on ' + $env:COMPUTERNAME + ' (Windows shows the first ' + $Limit + '):')
if ($rows.Count -eq 0) { Write-Host '  none' }
foreach ($r in $rows) {
    $mark = 'HIDDEN'
    if ($r.Shown) { $mark = 'SHOWN ' }
    $color = 'DarkYellow'
    if ($r.Armory) { $color = 'Cyan' } elseif ($r.Shown) { $color = 'Gray' }
    $dllNote = 'no DLL registered'
    if ($r.Dll -and $r.DllFound) { $dllNote = $r.Dll } elseif ($r.Dll) { $dllNote = 'DLL MISSING: ' + $r.Dll }
    $ours = ''
    if ($r.Armory) { $ours = '  <- Armory ' + $r.Armory }
    Write-Host ('{0,3}  {1}  [{2}]  spaces={3}  {4}  {5}{6}' -f $r.Position, $mark, $r.Name, $r.LeadingSpaces, $r.Clsid, $dllNote, $ours) -ForegroundColor $color
}

# The heartbeat: ArmoryBadges.dll writes HKCU\Software\IDEA Armory\Badges\Seen<Badge> when THIS
# person's explorer.exe asks for that badge's icon (Explorer asks only the handlers it loads).
$session = (Get-Process -Id $PID).SessionId
$explorer = Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session } | Sort-Object StartTime | Select-Object -First 1
$explorerStarted = $null
if ($explorer) { $explorerStarted = $explorer.StartTime.ToUniversalTime() }
$seen = Get-ItemProperty -LiteralPath 'HKCU:\Software\IDEA Armory\Badges' -ErrorAction SilentlyContinue
$loaded = $false
Write-Host ''
if ($installedVersion) { Write-Host ('Badges setup: version ' + $installedVersion + ', format ' + $installedFormat + ', installed ' + $installedAt + ' UTC') }
if ($explorerStarted) { Write-Host ('This session''s explorer.exe started ' + $explorerStarted + ' UTC') } else { Write-Host 'No explorer.exe in this session.' }
foreach ($a in $Armory) {
    $row = $rows | Where-Object { $_.Armory -eq $a.Badge } | Select-Object -First 1
    $value = $null
    if ($seen) { $value = $seen.('Seen' + $a.Badge) }
    $when = $null
    if ($value) { $when = [DateTime]::FromFileTimeUtc([long]$value) }
    $state = 'not registered'
    if ($row) {
        if ($when -and $explorerStarted -and $when -ge $explorerStarted) { $state = 'LOADED by Explorer at ' + $when + ' UTC'; $loaded = $true }
        elseif ($row.Shown) { $state = 'within the limit, not loaded by this Explorer yet' }
        else { $state = 'NOT LOADED: past the limit' }
        $state = 'position ' + $row.Position + ', ' + $state
    }
    Write-Host ('  ' + $a.Badge + ': ' + $state)
}

# The verdict, decided as BadgeHealth.Decide does and said in Settings' words.
$ours = @($rows | Where-Object { $_.Armory })
$first = -1
for ($i = 0; $i -lt $rows.Count; $i++) { if ($rows[$i].Armory) { $first = $i; break } }
$ahead = @()
if ($first -gt 0) { $ahead = @($rows[0..($first - 1)] | Where-Object { -not $_.Armory }) }
$apps = @()
foreach ($r in $ahead) {
    $app = (($r.Name.Trim() -replace '[0-9]+$', '') -replace '(Ext|Ico)$', '').Trim()
    if ($app -and -not ($apps | Where-Object { $_ -ieq $app })) { $apps += $app }
}
$shown = @($ours | Where-Object { $_.Shown }).Count
$problems = @()
foreach ($a in $Armory) {
    $row = $rows | Where-Object { $_.Name -ieq $a.Key } | Select-Object -First 1
    if (-not $row) { $problems += ('the ' + $a.Badge + ' badge is not registered') }
    elseif ($row.Clsid -ine $a.Clsid) { $problems += ('the ' + $a.Badge + ' badge names another CLSID') }
    elseif (-not $row.Dll) { $problems += ('the ' + $a.Badge + ' badge has no DLL registered') }
    elseif (-not $row.DllFound) { $problems += ('the ' + $a.Badge + ' badge''s DLL is missing') }
}
if (-not $installedVersion) { $problems += 'the badges setup''s version is not recorded' }
if ($installedFormat -and $installedFormat -ne '1') { $problems += ('the badges installed read format ' + $installedFormat) }

Write-Host ''
if (-not $installedVersion -and $ours.Count -eq 0) {
    $verdict = 'off'; $line = "Armory's status isn't shown on file icons on this computer. Turning it on needs an administrator once."
} elseif ($problems.Count -gt 0) {
    $verdict = 'broken'; $line = "Armory's badges are installed, but a file is missing. Ask an administrator to turn them on again."
    Write-Host ('Why: ' + ($problems -join '; '))
} elseif ($shown -eq 0) {
    $verdict = 'crowded'; $line = "Windows isn't showing Armory's badges because " + $ahead.Count + ' badges from other apps come first (' + ($apps -join ', ') + '). Windows shows only ' + $Limit + '.'
} elseif ($shown -lt $Armory.Count) {
    $verdict = 'partial'; $line = "Windows shows only some of Armory's badges because " + $ahead.Count + ' badges from other apps come first (' + ($apps -join ', ') + ').'
} elseif (-not $explorerStarted) {
    $verdict = 'afterSignIn'; $line = "Armory's status shows on file icons after you sign out of Windows and back in."
} elseif ($loaded) {
    $verdict = 'on'; $line = "Armory's status shows on file icons."
} elseif ($installedAt -and $installedAt -gt $explorerStarted) {
    $verdict = 'afterSignIn'; $line = "Armory's status shows on file icons after you sign out of Windows and back in."
} elseif (((Get-Date).ToUniversalTime() - $explorerStarted).TotalMinutes -lt 10) {
    $verdict = 'on'; $line = "Armory's status shows on file icons."
    Write-Host 'Why: explorer.exe has not asked for Armory''s badge icons yet.'
} else {
    $verdict = 'broken'; $line = "Armory's badges are installed, but a file is missing. Ask an administrator to turn them on again."
    Write-Host 'Why: explorer.exe did not ask for Armory''s badge icons.'
}
$colors = @{ on = 'Green'; off = 'Gray'; afterSignIn = 'Yellow'; crowded = 'Yellow'; partial = 'Yellow'; broken = 'Red' }
Write-Host ('Verdict (' + $verdict + '): ' + $line) -ForegroundColor $colors[$verdict]
exit 0
