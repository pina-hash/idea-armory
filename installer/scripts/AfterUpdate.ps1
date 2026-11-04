# Velopack kills processes whose executable is inside the install after every hook. Keep
# the waiter in Windows PowerShell outside that root, then start maintenance after it exits.
param([Parameter(Mandatory = $true)][int]$UpdaterPid,
      [Parameter(Mandatory = $true)][long]$UpdaterStarted,
      [Parameter(Mandatory = $true)][string]$Exe,
      [Parameter(Mandatory = $true)][string]$DataFolder,
      [Parameter(Mandatory = $true)][string]$Argument)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core') {
    $env:PSModulePath = (Join-Path $PSHOME 'Modules') + ';' + (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules')
}
function Audit([string]$text) {
    $folder = Join-Path $DataFolder 'logs'
    [void][IO.Directory]::CreateDirectory($folder)
    [IO.File]::AppendAllText((Join-Path $folder 'install-retirement.jsonl'), (([DateTime]::UtcNow.ToString('O') + ' ' + $text) | ConvertTo-Json -Compress) + "`n")
}
try {
    $updater = Get-Process -Id $UpdaterPid -ErrorAction SilentlyContinue
    if ($updater -and $updater.StartTime.ToUniversalTime().Ticks -eq $UpdaterStarted -and -not $updater.WaitForExit(120000)) {
        Audit 'deferred: installer/updater did not exit within two minutes; the next start reconciles'
        exit 1
    }
    if (-not (Test-Path -LiteralPath $Exe)) { Audit ('after-update install already retired: ' + $Exe); exit 0 }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe; $start.Arguments = '--' + $Argument
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $worker = [Diagnostics.Process]::Start($start)
    if (-not $worker.WaitForExit(120000)) { Audit 'deferred: post-update maintenance still running'; exit 1 }
    if ($worker.ExitCode -ne 0) { Audit ('post-update maintenance exited ' + $worker.ExitCode); exit $worker.ExitCode }
} catch { Audit ('deferred post-update maintenance: ' + $_.Exception.Message); exit 1 }
finally { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue }
