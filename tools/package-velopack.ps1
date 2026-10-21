# Builds the release that updates itself (0.4.0, Velopack; docs/agent/INSTALL.md "Updates") from
# the payload tools/package-agent.ps1 staged (dist\IDEA-Armory-USB-v<version>\files: the app,
# ArmoryShell.exe beside it and badges\). Writes to dist\velopack\:
#   IDEAArmory-win-Setup.exe        the installer that updates itself (per user, no administrator)
#   IDEAArmory-<version>-full.nupkg  the release every installed Armory downloads
#   releases.win.json, RELEASES, assets.win.json   the feed Armory reads
# Every file there goes on the GitHub release: Armory reads the feed at
# https://github.com/pina-hash/idea-armory/releases/latest/download/releases.win.json.
# Full packages only (no deltas): the latest-release address holds one release's files, and a
# delta would need the release before it.
# Needs the vpk tool at the same version as the Velopack package in src/Armory.Agent
# (dotnet tool install -g vpk --version <it>). Runs under PowerShell 7 on Windows.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PayloadDir = '',
    [string]$OutDir = 'dist/velopack'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Resolve-RepoPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path $root $path))
}
if (-not $PayloadDir) { $PayloadDir = 'dist/IDEA-Armory-USB-v' + $Version + '/files' }
$payload = Resolve-RepoPath $PayloadDir
$out = Resolve-RepoPath $OutDir
if (-not (Test-Path -LiteralPath (Join-Path $payload 'IdeaArmory.exe'))) { throw ('No IdeaArmory.exe in ' + $payload + '. Run tools/package-agent.ps1 first.') }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
[void][IO.Directory]::CreateDirectory($out)
$icon = Join-Path $root 'src/Armory.Agent/Assets/armory.ico'
$arguments = @('pack',
    '--packId', 'IDEAArmory',
    '--packVersion', $Version,
    '--packDir', $payload,
    '--mainExe', 'IdeaArmory.exe',
    '--packTitle', 'IDEA Armory',
    '--packAuthors', 'IDEA, Don Bosco Tech',
    '--icon', $icon,
    '--shortcuts', 'StartMenuRoot',
    '--delta', 'None',
    '--noPortable',
    '--outputDir', $out)
& vpk @arguments
if ($LASTEXITCODE -ne 0) { throw ('vpk pack failed with exit code ' + $LASTEXITCODE + '.') }
foreach ($required in @('IDEAArmory-win-Setup.exe', ('IDEAArmory-' + $Version + '-full.nupkg'), 'releases.win.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $out $required))) { throw ('vpk did not write ' + $required + '.') }
}
$feed = Get-Content -LiteralPath (Join-Path $out 'releases.win.json') -Raw | ConvertFrom-Json
if (-not (@($feed.Assets) | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Full' })) { throw ('releases.win.json has no full release ' + $Version + '.') }
Write-Output ('Velopack release ' + $Version + ' in ' + $out + ':')
Get-ChildItem -LiteralPath $out -File | ForEach-Object { '  ' + $_.Name + '  (' + [Math]::Round($_.Length / 1MB, 1) + ' MB)' }
if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value ('velopack_dir=' + $out) -Encoding utf8 }
