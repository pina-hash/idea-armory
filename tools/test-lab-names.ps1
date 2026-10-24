# CI's check of Rename-LabComputer.ps1 on a Windows runner (docs/LAB_COMPUTER_NAMES.md). Nothing
# is renamed: -ListOnly records the runner, -DryRun finds it in names.csv by its serial number
# and network address and says the rename it would make, a name another computer already got is
# refused, and an invalid name is refused. Runs under PowerShell 7 on Windows.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'lab-names'
$work = Join-Path $env:RUNNER_TEMP ('lab-names-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
Copy-Item -Path (Join-Path $source '*') -Destination $work
$script = Join-Path $work 'Rename-LabComputer.ps1'
$done = Join-Path $work 'names-done.csv'
$list = Join-Path $work 'names.csv'
function Run([string[]]$arguments) {
    $output = & pwsh -NoProfile -NonInteractive -File $script @arguments 2>&1 | Out-String
    Write-Output $output
    return $output
}
function Fail([string]$line) { throw ('Lab names: ' + $line) }

# 1. Listed only: one row, this computer's name, nothing renamed.
$out = Run @('-ListOnly')
$rows = @(Import-Csv -LiteralPath $done)
if ($rows.Count -ne 1 -or $rows[0].OldName -ne $env:COMPUTERNAME -or $rows[0].What -ne 'listed') { Fail ('-ListOnly wrote ' + ($rows | ConvertTo-Json -Compress)) }
$serial = $rows[0].Serial; $mac = $rows[0].MAC
Write-Output ('runner: serial "' + $serial + '", address "' + $mac + '"')
if (-not $serial -and -not $mac) { Fail 'the runner has neither a serial number nor a network address to key on' }

# 2. On the list (by serial, or by address when the runner has no serial): the dry run names it.
Set-Content -LiteralPath $list -Value @('Serial,MAC,NewName', ($serial + ',,LABTEST-07')) -Encoding utf8
if (-not $serial) { Set-Content -LiteralPath $list -Value @('Serial,MAC,NewName', (',' + $mac + ',LABTEST-07')) -Encoding utf8 }
$out = Run @('-DryRun')
if ($out -notmatch ('would rename ' + [regex]::Escape($env:COMPUTERNAME) + ' to LABTEST-07')) { Fail 'the dry run did not find the runner in names.csv' }
if ($env:COMPUTERNAME -eq 'LABTEST-07') { Fail 'the runner was renamed' }
# By address alone, too.
if ($mac) {
    Set-Content -LiteralPath $list -Value @('Serial,MAC,NewName', ('NOT-THIS-ONE,' + ($mac -replace '(..)(?!$)', '$1-') + ',LABTEST-08')) -Encoding utf8
    $out = Run @('-DryRun')
    if ($out -notmatch 'would rename .* to LABTEST-08') { Fail 'the dry run did not find the runner by its address written with hyphens' }
}

# 3. A name another computer already got is refused.
Set-Content -LiteralPath $list -Value @('Serial,MAC,NewName', ($serial + ',' + $mac + ',LABTEST-09')) -Encoding utf8
[pscustomobject]@{ Date = '2026-10-23 08:00'; Serial = 'ANOTHER-SERIAL'; MAC = '001122334455'; OldName = 'IDEA-06'; NewName = 'LABTEST-09'; What = 'renamed' } |
    Export-Csv -LiteralPath $done -Append -NoTypeInformation -Encoding UTF8
$out = Run @('-DryRun')
if ($out -notmatch 'LABTEST-09 was already given to another computer') { Fail 'a name already given to another computer was not refused' }

# 4. A name Windows refuses is refused first.
Set-Content -LiteralPath $list -Value @('Serial,MAC,NewName', ($serial + ',' + $mac + ',THIS-NAME-IS-FAR-TOO-LONG')) -Encoding utf8
$out = Run @('-DryRun')
if ($out -notmatch 'cannot be a computer name') { Fail 'a name longer than 15 characters was not refused' }

Remove-Item -LiteralPath $work -Recurse -Force
Write-Output 'Lab names: listed, found by serial and by address, a taken name and a bad name refused; nothing renamed.'
exit 0
