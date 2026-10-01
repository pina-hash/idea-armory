# Builds the two IDEA Armory deliverables from the win-x64 self-contained publish folder:
#   dist\IDEA-Armory-USB-v<version>.zip     the flash-drive folder, laid out exactly as it sits on
#                                           a drive after "Extract All": the three .cmd files,
#                                           README.txt, logs\, and files\ (the app plus
#                                           files\scripts\Setup.ps1, which does the real work)
#   dist\IDEA-Armory-Setup-v<version>.exe   the normal per-user installer (Inno Setup 6.3 or later)
# and a .sha256 file beside each. Both carry the same files\ folder.
#
# Run after:
#   dotnet publish src/Armory.Agent -c Release -r win-x64 --self-contained true -o publish/agent
# That publish adds empty "win-x64" sections to the packages.lock.json of every project the
# agent references, which breaks "dotnet restore --locked-mode". Never commit them: after
# packaging on a working copy, run  git restore -- '*packages.lock.json'  (CI's checkout is
# thrown away).
# -NoSetupExe builds only the zip (for a computer without Inno Setup; CI never uses it).
# Runs under PowerShell 7 (CI) and Windows PowerShell 5.1.
param(
    [string]$PublishDir = 'publish/agent',
    [string]$Version = '',
    [string]$OutDir = 'dist',
    [string]$Iscc = '',
    [switch]$NoSetupExe
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Resolve-RepoPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path $root $path))
}
$publish = Resolve-RepoPath $PublishDir
$dist = Resolve-RepoPath $OutDir
$installer = Join-Path $root 'installer'
$project = Join-Path $root 'src/Armory.Agent/Armory.Agent.csproj'
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

# The version is the one in src/Armory.Agent (Directory.Build.props included).
if (-not $Version) {
    $Version = (& dotnet msbuild $project -nologo -getProperty:Version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the Version of src/Armory.Agent.' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw ('Version must look like 0.1.0, not "' + $Version + '".') }

# --- The publish folder must be the self-contained win-x64 app, and nothing else.
$exe = Join-Path $publish 'IdeaArmory.exe'
foreach ($required in @('IdeaArmory.exe', 'IdeaArmory.dll', 'IdeaArmory.runtimeconfig.json', 'hostfxr.dll', 'wwwroot/index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw ('The publish folder ' + $publish + ' has no ' + $required + '. Run dotnet publish first (see the top of this script).') }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publish 'IdeaArmory.runtimeconfig.json') -Raw
if ($runtimeConfig -notmatch '"includedFrameworks"') { throw 'The publish is not self-contained (IdeaArmory.runtimeconfig.json has no includedFrameworks). Publish with --self-contained true.' }
$assembly = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'IdeaArmory.dll'))
$checks = @(
    @{ Name = 'IdeaArmory.dll product'; Actual = $assembly.ProductName; Wanted = 'IDEA Armory' },
    @{ Name = 'IdeaArmory.dll company'; Actual = $assembly.CompanyName; Wanted = 'IDEA, Don Bosco Tech' },
    @{ Name = 'IdeaArmory.dll version'; Actual = ('{0}.{1}.{2}' -f $assembly.FileMajorPart, $assembly.FileMinorPart, $assembly.FileBuildPart); Wanted = $Version })
if ($onWindows) {
    # The apphost carries the version resource only when it is built on Windows.
    $native = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $checks += @{ Name = 'IdeaArmory.exe product'; Actual = $native.ProductName; Wanted = 'IDEA Armory' }
    $checks += @{ Name = 'IdeaArmory.exe version'; Actual = ('{0}.{1}.{2}' -f $native.FileMajorPart, $native.FileMinorPart, $native.FileBuildPart); Wanted = $Version }
}
foreach ($check in $checks) {
    if ($check.Actual -ne $check.Wanted) { throw ($check.Name + ' is "' + $check.Actual + '", expected "' + $check.Wanted + '".') }
}
$publishFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Force)
$forbidden = @($publishFiles | Where-Object {
        $_.Extension -match '^\.(sldprt|sldasm|slddrw|prtdot|asmdot|drwdot|step|stp|iges|igs|x_t|x_b)$' -or
        $_.Name -like 'SolidWorks.Interop*' -or $_.Name -ieq 'settings.json' })
if ($forbidden.Count -gt 0) { throw ('These must never ship (CAD files, SolidWorks DLLs or per-user settings): ' + (($forbidden | ForEach-Object { $_.Name }) -join ', ')) }
if (Test-Path -LiteralPath (Join-Path $publish 'scripts')) { throw 'The publish folder already has a scripts folder; the installer scripts go there.' }

# --- Installer sources agree with each other.
$iss = Get-Content -LiteralPath (Join-Path $installer 'IdeaArmory.iss') -Raw
$setupSource = Get-Content -LiteralPath (Join-Path $installer 'scripts/Setup.ps1') -Raw
$issGuid = [regex]::Match($iss, '#define AppId "\{\{([0-9A-F-]{36})\}"').Groups[1].Value
$setupGuid = [regex]::Match($setupSource, "\`$InnoAppId = '\{([0-9A-F-]{36})\}'").Groups[1].Value
if (-not $issGuid -or $issGuid -ne $setupGuid) { throw ('AppId in IdeaArmory.iss (' + $issGuid + ') and $InnoAppId in Setup.ps1 (' + $setupGuid + ') must be the same GUID.') }
foreach ($line in @('PrivilegesRequired=lowest', 'PrivilegesRequiredOverridesAllowed=', 'DefaultDirName={localappdata}\Programs\IDEA Armory', 'DisableDirPage=yes')) {
    if (-not ($iss -split "`r?`n" | Where-Object { $_.Trim() -eq $line })) { throw ('IdeaArmory.iss must contain the line ' + $line) }
}

# Text files ship with CRLF (cmd.exe needs it), ASCII only, and the version stamped in.
function Copy-Text([string]$from, [string]$to) {
    $text = [IO.File]::ReadAllText($from).Replace('__VERSION__', $Version)
    $text = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
    foreach ($ch in $text.ToCharArray()) {
        if ([int]$ch -gt 126 -or ([int]$ch -lt 32 -and $ch -ne "`r" -and $ch -ne "`n" -and $ch -ne "`t")) { throw ($from + ' must be plain ASCII; it has character U+' + ([int]$ch).ToString('X4') + '.') }
    }
    if ($text.Contains('__VERSION' + '__')) { throw ($from + ' still has an unstamped version token.') }
    [IO.File]::WriteAllText($to, $text, (New-Object Text.ASCIIEncoding))
}
function Write-Sha256([string]$file) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($file + '.sha256', $hash + '  ' + (Split-Path -Leaf $file) + "`n", (New-Object Text.ASCIIEncoding))
    return $hash
}

# --- Stage the flash-drive folder.
$name = 'IDEA-Armory-USB-v' + $Version
$stage = Join-Path $dist $name
$files = Join-Path $stage 'files'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
foreach ($dir in @($files, (Join-Path $files 'scripts'), (Join-Path $stage 'logs'))) { [void][IO.Directory]::CreateDirectory($dir) }
foreach ($item in @(Get-ChildItem -LiteralPath $publish -Force)) { Copy-Item -LiteralPath $item.FullName -Destination $files -Recurse -Force }
foreach ($script in @('Setup.ps1', 'Uninstall.cmd', 'Check.cmd')) { Copy-Text (Join-Path $installer ('scripts/' + $script)) (Join-Path $files ('scripts/' + $script)) }
foreach ($top in @('Install IDEA Armory.cmd', 'Uninstall IDEA Armory.cmd', 'Check IDEA Armory.cmd', 'README.txt')) { Copy-Text (Join-Path $installer ('usb/' + $top)) (Join-Path $stage $top) }
Copy-Text (Join-Path $installer 'usb/logs/_about-this-folder.txt') (Join-Path $stage 'logs/_about-this-folder.txt')

# Every byte of files\ is listed, so Setup can prove a copy from a worn flash drive is whole.
$manifest = Join-Path $files 'scripts/payload.sha256'
$lines = New-Object System.Collections.Generic.List[string]
$payload = @(Get-ChildItem -LiteralPath $files -Recurse -File -Force | ForEach-Object {
        [pscustomobject]@{ Full = $_.FullName; Relative = $_.FullName.Substring($files.Length + 1).Replace('/', '\') } } |
    Sort-Object -Property Relative -CaseSensitive)
foreach ($entry in $payload) {
    $lines.Add((Get-FileHash -LiteralPath $entry.Full -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $entry.Relative)
}
[IO.File]::WriteAllText($manifest, (($lines -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding $false))

# --- The zip: its top level is the folder's contents, so Extract All makes IDEA-Armory-USB-v<version>\.
$zip = Join-Path $dist ($name + '.zip')
foreach ($old in @($zip, ($zip + '.sha256'))) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force } }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$zipHash = Write-Sha256 $zip

# --- The normal installer.
$setupExe = Join-Path $dist ('IDEA-Armory-Setup-v' + $Version + '.exe')
$setupHash = $null
foreach ($old in @($setupExe, ($setupExe + '.sha256'))) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force } }
if (-not $NoSetupExe) {
    if (-not $onWindows) { throw 'IDEA-Armory-Setup needs Inno Setup on Windows. Use -NoSetupExe to build only the flash-drive zip here.' }
    . (Join-Path $PSScriptRoot 'inno-setup.ps1')
    $compiler = Find-Iscc @($Iscc, $env:ARMORY_ISCC)
    if (-not $compiler) { throw 'Inno Setup 6.3 or later is not installed (ISCC.exe not found). Install it, or pass -Iscc <path>.' }
    $found = Get-IsccVersion $compiler
    $isccVersion = $found.Version
    if ($isccVersion -and $isccVersion -lt [version]'6.3') { throw ('Inno Setup ' + $isccVersion + ' is too old; IdeaArmory.iss needs 6.3 or later.') }
    if (-not $isccVersion) {
        Write-Warning ('The Inno Setup version could not be read (' + ($found.Seen -join '; ') + '); ISCC itself refuses directives it does not know.')
        $isccVersion = 'unknown version'
    }
    $arguments = @('/DAppVersion=' + $Version, '/DPayloadDir=' + $files, '/DOutputDir=' + $dist)
    $icon = Join-Path $root 'src/Armory.Agent/Assets/armory.ico'
    if (Test-Path -LiteralPath $icon) { $arguments += '/DIconFile=' + $icon }
    Write-Output ('Inno Setup ' + $isccVersion + ': ' + $compiler)
    & $compiler @arguments (Join-Path $installer 'IdeaArmory.iss')
    if ($LASTEXITCODE -ne 0) { throw ('ISCC failed with exit code ' + $LASTEXITCODE + '.') }
    if (-not (Test-Path -LiteralPath $setupExe)) { throw ('ISCC did not produce ' + $setupExe) }
    $setupHash = Write-Sha256 $setupExe
}

# --- Summary, and outputs for the workflow.
Write-Output ('Version:  ' + $Version)
Write-Output ('Zip:      ' + $zip + '  (' + [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1) + ' MB)')
Write-Output ('SHA-256:  ' + $zipHash)
if ($setupHash) {
    Write-Output ('Setup:    ' + $setupExe + '  (' + [Math]::Round((Get-Item -LiteralPath $setupExe).Length / 1MB, 1) + ' MB)')
    Write-Output ('SHA-256:  ' + $setupHash)
}
Write-Output ('Flash drive layout (' + $name + '):')
Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { '  ' + $_.Name + $(if ($_.PSIsContainer) { '\' } else { '' }) }
Write-Output ('  files\ holds ' + $payload.Count + ' files, listed with SHA-256 in files\scripts\payload.sha256')
if ($env:GITHUB_OUTPUT) {
    $out = @('version=' + $Version, 'usb_zip=' + $zip, 'usb_sha256=' + $zipHash)
    if ($setupHash) { $out += @('setup_exe=' + $setupExe, 'setup_sha256=' + $setupHash) }
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value $out -Encoding utf8
}
