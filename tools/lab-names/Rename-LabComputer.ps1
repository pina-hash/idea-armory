# Renames this lab computer from a list on the flash drive (docs/LAB_COMPUTER_NAMES.md).
#
# Lab computers copied from one Windows image all have the image's name (several report IDEA-06).
# Run "Rename this computer.cmd" from the flash drive once on each computer. This script:
#   1. reads this computer's serial number (from its firmware) and its network hardware addresses,
#   2. looks them up in names.csv beside it (columns Serial,MAC,NewName; either key may be blank),
#   3. when the computer is not on the list, asks for its new name and suggests the next free one,
#   4. refuses a name already given to a different computer in names-done.csv,
#   5. renames the computer (asking for a domain account only when it is joined to a domain),
#   6. writes one row to names-done.csv (date, serial, address, old name, new name), and
#   7. restarts it, which is when Windows takes the new name.
# Nothing else on the computer changes. A computer that already has its name is left alone.
#
#   -ListOnly   record this computer's serial, address and name in names-done.csv and rename nothing
#   -DryRun     say what would happen and change nothing
#   -Prefix     the start of a suggested name (default IDEA-)
#   -NoRestart  rename, then leave the restart to you
#
# Needs an administrator: the .cmd beside it asks Windows for one. Windows PowerShell 5.1 or 7.
param(
    [switch]$ListOnly,
    [switch]$DryRun,
    [string]$Prefix = 'IDEA-',
    [switch]$NoRestart
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$listFile = Join-Path $here 'names.csv'
$doneFile = Join-Path $here 'names-done.csv'

function Say([string]$line) { Write-Host $line }
function Stop-With([string]$line) { Write-Host ''; Write-Host $line -ForegroundColor Yellow; Write-Host ''; Read-Host 'Press Enter to close' | Out-Null; exit 1 }
function Get-Key([string]$text) { if ($null -eq $text) { return '' } return ($text -replace '[^0-9A-Za-z]', '').ToUpperInvariant() }

# A name Windows accepts for a computer: 1 to 15 letters, digits and hyphens, not only digits,
# not starting or ending with a hyphen.
function Test-ComputerName([string]$name) {
    return $name -match '^[A-Za-z0-9-]{1,15}$' -and $name -notmatch '^[0-9]+$' -and $name -notmatch '^-' -and $name -notmatch '-$'
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $DryRun -and -not $ListOnly -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Stop-With 'This needs an administrator. Close this window and double-click "Rename this computer.cmd" instead, then click Yes.'
}

# This computer.
$serial = ''
try { $serial = [string](Get-CimInstance -ClassName Win32_BIOS).SerialNumber } catch { }
$serial = $serial.Trim()
# A serial an image or a builder left generic is no key at all.
if ($serial -match '^(|0+|To be filled by O\.E\.M\.|Default string|System Serial Number|None|N/A)$') { $serial = '' }
$macs = @()
try { $macs = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.MacAddress } | Sort-Object -Property MacAddress | ForEach-Object { Get-Key $_.MacAddress }) } catch { }
if ($macs.Count -eq 0) {
    try { $macs = @(Get-CimInstance -ClassName Win32_NetworkAdapter -Filter 'PhysicalAdapter=True' | Where-Object { $_.MACAddress } | ForEach-Object { Get-Key $_.MACAddress } | Sort-Object) } catch { }
}
$mac = if ($macs.Count -gt 0) { $macs[0] } else { '' }
$system = Get-CimInstance -ClassName Win32_ComputerSystem
$current = $env:COMPUTERNAME
$onDomain = [bool]$system.PartOfDomain

Say ''
Say ('This computer is ' + $current + '.')
Say ('  Serial number: ' + $(if ($serial) { $serial } else { '(none readable)' }))
Say ('  Network address: ' + $(if ($mac) { $mac } else { '(none found)' }))
Say ('  ' + $(if ($onDomain) { 'Joined to the domain ' + $system.Domain + '.' } else { 'Not joined to a domain (workgroup ' + $system.Workgroup + ').' }))
Say ''

# What has been done so far, on this drive.
$done = @()
if (Test-Path -LiteralPath $doneFile) { $done = @(Import-Csv -LiteralPath $doneFile) }
function Write-Done([string]$newName, [string]$what) {
    $row = [pscustomobject]@{ Date = (Get-Date).ToString('yyyy-MM-dd HH:mm'); Serial = $serial; MAC = $mac; OldName = $current; NewName = $newName; What = $what }
    if ($DryRun) { Say ('(dry run) would record: ' + ($row | ConvertTo-Csv -NoTypeInformation | Select-Object -Last 1)); return }
    $row | Export-Csv -LiteralPath $doneFile -Append -NoTypeInformation -Encoding UTF8
}

if ($ListOnly) {
    Write-Done '' 'listed'
    Say ('Recorded in ' + $doneFile + '. Nothing was renamed.')
    if (-not $DryRun) { Start-Sleep -Seconds 3 }
    exit 0
}

# The list: by serial first, then by any of this computer's addresses.
$wanted = $null
if (Test-Path -LiteralPath $listFile) {
    $list = @(Import-Csv -LiteralPath $listFile)
    if ($serial) { $wanted = $list | Where-Object { $_.Serial -and (Get-Key $_.Serial) -eq (Get-Key $serial) } | Select-Object -First 1 }
    if (-not $wanted -and $macs.Count -gt 0) { $wanted = $list | Where-Object { $_.MAC -and $macs -contains (Get-Key $_.MAC) } | Select-Object -First 1 }
}
$name = if ($wanted -and $wanted.NewName) { $wanted.NewName.Trim() } else { '' }
if ($name) {
    Say ('names.csv says this computer is ' + $name + '.')
} else {
    # The next free number after the highest name with the prefix in names-done.csv or names.csv.
    $taken = @($done | ForEach-Object { $_.NewName }) + @(if (Test-Path -LiteralPath $listFile) { Import-Csv -LiteralPath $listFile | ForEach-Object { $_.NewName } })
    $numbers = @($taken | Where-Object { $_ -and $_.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $digits = $_.Substring($Prefix.Length); if ($digits -match '^\d+$') { [int]$digits } })
    $next = if ($numbers.Count -gt 0) { ($numbers | Measure-Object -Maximum).Maximum + 1 } else { 1 }
    $suggested = $Prefix + $next.ToString('00')
    $typed = Read-Host ('This computer is not in names.csv. Type its new name and press Enter (Enter alone uses ' + $suggested + ')')
    $name = if ($typed.Trim()) { $typed.Trim() } else { $suggested }
}
$name = $name.ToUpperInvariant()
if (-not (Test-ComputerName $name)) { Stop-With ('"' + $name + '" cannot be a computer name: use 1 to 15 letters, numbers and hyphens, not only numbers.') }
if ($name -eq $current.ToUpperInvariant()) {
    Say ('This computer is already ' + $name + '. Nothing to do.')
    Start-Sleep -Seconds 3
    exit 0
}
# The same name for a different computer is the problem this exists to end.
$clash = $done | Where-Object { $_.NewName -and $_.NewName.ToUpperInvariant() -eq $name -and $_.What -eq 'renamed' -and -not (($serial -and (Get-Key $_.Serial) -eq (Get-Key $serial)) -or ($mac -and (Get-Key $_.MAC) -eq $mac)) } | Select-Object -First 1
if ($clash) { Stop-With ($name + ' was already given to another computer (serial ' + $clash.Serial + ', ' + $clash.Date + '). Pick another name and run this again.') }

if ($DryRun) {
    Say ('(dry run) would rename ' + $current + ' to ' + $name + $(if ($onDomain) { ', with a domain account' } else { '' }) + ', then restart.')
    Write-Done $name 'renamed'
    exit 0
}

try {
    if ($onDomain) {
        Say ('This computer is on a domain, so Windows needs a domain account that may rename computers.')
        $credential = Get-Credential -Message ('A domain account that may rename computers in ' + $system.Domain)
        Rename-Computer -NewName $name -DomainCredential $credential -Force -ErrorAction Stop
    } else {
        Rename-Computer -NewName $name -Force -ErrorAction Stop
    }
} catch {
    Stop-With ('Windows did not rename this computer: ' + $_.Exception.Message)
}
Write-Done $name 'renamed'
Say ''
Say ('Renamed ' + $current + ' to ' + $name + '. Windows uses the new name after a restart.')
if ($NoRestart) { Read-Host 'Press Enter to close' | Out-Null; exit 0 }
Say 'Restarting in 10 seconds. Close this window to restart later yourself.'
Start-Sleep -Seconds 10
Restart-Computer -Force
