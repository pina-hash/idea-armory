# Builds the IDEA Armory deliverables from the win-x64 self-contained publish folder and the
# native parts:
#   dist\IDEA-Armory-USB-v<version>.zip            the flash-drive folder, laid out exactly as it
#                                                  sits on a drive after "Extract All": the four .cmd
#                                                  files, README.txt, logs\, and files\ (the app plus
#                                                  files\scripts\Setup.ps1, which does the real work)
#   dist\IDEA-Armory-Setup-v<version>.exe          the normal per-user installer (Inno Setup 6.3 or later)
#   dist\IDEA-Armory-Badges-Setup-v<version>.exe   the optional administrator step that shows Armory's
#                                                  status on file icons (docs/agent/EXPLORER.md)
# and a .sha256 file beside each. The zip and IDEA-Armory-Setup carry the same files\ folder:
# the app, ArmoryShell.exe beside IdeaArmory.exe (what File Explorer's right-click items run) and
# badges\IDEA-Armory-Badges-Setup.exe (the same badges setup, which Settings' Turn on and the
# drive's "Show Armory status on file icons.cmd" run).
#
# Run after:
#   pwsh tools/build-native.ps1
#   dotnet publish src/Armory.Agent -c Release -r win-x64 --self-contained true -o publish/agent
# That publish adds empty "win-x64" sections to the packages.lock.json of every project the
# agent references, which breaks "dotnet restore --locked-mode". Never commit them: after
# packaging on a working copy, run  git restore -- '*packages.lock.json'  (CI's checkout is
# thrown away).
# -NoSetupExe builds only the zip, without either setup and so without badges\ (for a computer
# without Inno Setup, such as a developer's Linux box with tools/build-native.sh's parts; CI never
# uses it, and such a zip is never shipped).
# Runs under PowerShell 7 (CI) and Windows PowerShell 5.1.
param(
    [string]$PublishDir = 'publish/agent',
    [string]$NativeDir = 'publish/native',
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
$native = Resolve-RepoPath $NativeDir
$dist = Resolve-RepoPath $OutDir
$installer = Join-Path $root 'installer'
$project = Join-Path $root 'src/Armory.Agent/Armory.Agent.csproj'
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$Product = 'IDEA Armory'
$Company = 'IDEA, Don Bosco Tech'
$BadgesInPayload = 'badges/IDEA-Armory-Badges-Setup.exe'

# The version is the one in src/Armory.Agent (Directory.Build.props included).
if (-not $Version) {
    $Version = (& dotnet msbuild $project -nologo -getProperty:Version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the Version of src/Armory.Agent.' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw ('Version must look like 0.1.0, not "' + $Version + '".') }

# A Windows binary's version resource: product, company and version, as IdeaArmory.exe's. Only
# Windows reads a native file's resources, so elsewhere this says so and checks nothing.
function Assert-VersionResource([string]$file, [string]$label) {
    if (-not $onWindows) { Write-Output ($label + ': version resource not read (only Windows reads it)'); return }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
    $actual = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
    if ($info.ProductName -ne $Product -or $info.CompanyName -ne $Company -or $actual -ne $Version) {
        throw ($label + ' carries "' + $info.ProductName + '" by "' + $info.CompanyName + '" ' + $actual + ', expected "' + $Product + '" by "' + $Company + '" ' + $Version + '.')
    }
    Write-Output ($label + ': ' + $info.ProductName + ' ' + $actual + ' by ' + $info.CompanyName)
}

# CAD files, SolidWorks DLLs (the SolidWorks link uses none: docs/agent/SOLIDWORKS.md), per-user
# settings and the native test tools never ship.
function Assert-NothingForbidden([string]$folder, [string]$what) {
    $forbidden = @(Get-ChildItem -LiteralPath $folder -Recurse -File -Force | Where-Object {
            $_.Extension -match '^\.(sldprt|sldasm|slddrw|prtdot|asmdot|drwdot|step|stp|iges|igs|x_t|x_b)$' -or
            $_.Name -like 'SolidWorks.Interop*' -or $_.Name -ieq 'settings.json' -or
            $_.Name -ieq 'BadgeProbe.exe' -or $_.Name -ieq 'ShellPipeTest.exe' })
    if ($forbidden.Count -gt 0) { throw ('These must never ship in ' + $what + ' (CAD files, SolidWorks DLLs, per-user settings or test tools): ' + (($forbidden | ForEach-Object { $_.Name }) -join ', ')) }
}

# --- The publish folder must be the self-contained win-x64 app, and nothing else.
$exe = Join-Path $publish 'IdeaArmory.exe'
foreach ($required in @('IdeaArmory.exe', 'IdeaArmory.dll', 'IdeaArmory.runtimeconfig.json', 'hostfxr.dll', 'wwwroot/index.html', 'Assets/armory.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw ('The publish folder ' + $publish + ' has no ' + $required + '. Run dotnet publish first (see the top of this script).') }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publish 'IdeaArmory.runtimeconfig.json') -Raw
if ($runtimeConfig -notmatch '"includedFrameworks"') { throw 'The publish is not self-contained (IdeaArmory.runtimeconfig.json has no includedFrameworks). Publish with --self-contained true.' }
$assembly = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'IdeaArmory.dll'))
$checks = @(
    @{ Name = 'IdeaArmory.dll product'; Actual = $assembly.ProductName; Wanted = $Product },
    @{ Name = 'IdeaArmory.dll company'; Actual = $assembly.CompanyName; Wanted = $Company },
    @{ Name = 'IdeaArmory.dll version'; Actual = ('{0}.{1}.{2}' -f $assembly.FileMajorPart, $assembly.FileMinorPart, $assembly.FileBuildPart); Wanted = $Version })
if ($onWindows) {
    # The apphost carries the version resource only when it is built on Windows.
    $apphost = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $checks += @{ Name = 'IdeaArmory.exe product'; Actual = $apphost.ProductName; Wanted = $Product }
    $checks += @{ Name = 'IdeaArmory.exe version'; Actual = ('{0}.{1}.{2}' -f $apphost.FileMajorPart, $apphost.FileMinorPart, $apphost.FileBuildPart); Wanted = $Version }
}
foreach ($check in $checks) {
    if ($check.Actual -ne $check.Wanted) { throw ($check.Name + ' is "' + $check.Actual + '", expected "' + $check.Wanted + '".') }
}
Assert-NothingForbidden $publish 'the publish folder'
foreach ($taken in @('scripts', 'badges', 'ArmoryShell.exe')) {
    if (Test-Path -LiteralPath (Join-Path $publish $taken)) { throw ('The publish folder already has ' + $taken + '; packaging puts it there.') }
}

# --- The native parts (tools/build-native.ps1): the forwarder ships beside IdeaArmory.exe, the
# badge handlers go into the badges setup. ARM64 is there when the build had its C++ tools.
$shellExe = Join-Path $native 'x64/ArmoryShell.exe'
$badgesDll = Join-Path $native 'x64/ArmoryBadges.dll'
$arm64Dll = Join-Path $native 'arm64/ArmoryBadges.dll'
foreach ($required in @($shellExe, $badgesDll)) {
    if (-not (Test-Path -LiteralPath $required)) { throw ('The native build has no ' + $required + '. Run tools/build-native.ps1 first (tools/build-native.sh on Linux, for a zip that is never shipped).') }
}
$hasArm64 = Test-Path -LiteralPath $arm64Dll
Assert-VersionResource $shellExe 'ArmoryShell.exe'
Assert-VersionResource $badgesDll 'ArmoryBadges.dll (x64)'
if ($hasArm64) { Assert-VersionResource $arm64Dll 'ArmoryBadges.dll (ARM64)' }
else { Write-Output 'ArmoryBadges.dll (ARM64): not built, so the badges setup installs on x64 Windows only' }

# --- Installer sources agree with each other and with the app.
$iss = Get-Content -LiteralPath (Join-Path $installer 'IdeaArmory.iss') -Raw
$badgesIss = Get-Content -LiteralPath (Join-Path $installer 'IdeaArmoryBadges.iss') -Raw
$setupSource = Get-Content -LiteralPath (Join-Path $installer 'scripts/Setup.ps1') -Raw
$identitySource = Get-Content -LiteralPath (Join-Path $root 'src/Armory.Agent/ShellIdentity.cs') -Raw
$issGuid = [regex]::Match($iss, '#define AppId "\{\{([0-9A-F-]{36})\}"').Groups[1].Value
$setupGuid = [regex]::Match($setupSource, "\`$InnoAppId = '\{([0-9A-F-]{36})\}'").Groups[1].Value
if (-not $issGuid -or $issGuid -ne $setupGuid) { throw ('AppId in IdeaArmory.iss (' + $issGuid + ') and $InnoAppId in Setup.ps1 (' + $setupGuid + ') must be the same GUID.') }
# One AppUserModelID for the notifications: the app's (ShellIdentity.AppId), the shortcut's and
# the registration's in IdeaArmory.iss, and Setup.ps1's.
$appAumid = [regex]::Match($identitySource, 'internal const string AppId = "([^"]+)";').Groups[1].Value
$issAumids = @([regex]::Matches($iss, '#define Aumid "([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$setupAumid = [regex]::Match($setupSource, "\`$Aumid = '([^']+)'").Groups[1].Value
if (-not $appAumid -or $issAumids.Count -ne 1 -or $issAumids[0] -cne $appAumid -or $setupAumid -cne $appAumid) {
    throw ('The AppUserModelID must be the same in ShellIdentity.cs (' + $appAumid + '), IdeaArmory.iss (' + ($issAumids -join ', ') + ') and Setup.ps1 (' + $setupAumid + ').')
}
if ($iss -notmatch 'AppUserModelID: "\{#Aumid\}"') { throw 'The Start menu shortcut in IdeaArmory.iss must carry AppUserModelID: "{#Aumid}".' }
foreach ($line in @('PrivilegesRequired=lowest', 'PrivilegesRequiredOverridesAllowed=', 'DefaultDirName={localappdata}\Programs\IDEA Armory', 'DisableDirPage=yes')) {
    if (-not ($iss -split "`r?`n" | Where-Object { $_.Trim() -eq $line })) { throw ('IdeaArmory.iss must contain the line ' + $line) }
}
# The per-user setup never writes HKLM (the badges setup has its own uninstaller).
if ($iss -match '(?m)^\s*Root:\s*HK(LM|EY_LOCAL_MACHINE)') { throw 'IdeaArmory.iss must not write HKLM; the badges setup is the one administrator step.' }
foreach ($line in @('PrivilegesRequired=admin', 'PrivilegesRequiredOverridesAllowed=')) {
    if (-not ($badgesIss -split "`r?`n" | Where-Object { $_.Trim() -eq $line })) { throw ('IdeaArmoryBadges.iss must contain the line ' + $line) }
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
[void][IO.Directory]::CreateDirectory($dist)

# --- Inno Setup, for both setups.
$compiler = $null
if (-not $NoSetupExe) {
    if (-not $onWindows) { throw 'The setups need Inno Setup on Windows. Use -NoSetupExe to build only the flash-drive zip here.' }
    . (Join-Path $PSScriptRoot 'inno-setup.ps1')
    $compiler = Find-Iscc @($Iscc, $env:ARMORY_ISCC)
    if (-not $compiler) { throw 'Inno Setup 6.3 or later is not installed (ISCC.exe not found). Install it, or pass -Iscc <path>.' }
    $found = Get-IsccVersion $compiler
    $isccVersion = $found.Version
    if ($isccVersion -and $isccVersion -lt [version]'6.3') { throw ('Inno Setup ' + $isccVersion + ' is too old; the setups need 6.3 or later.') }
    if (-not $isccVersion) {
        Write-Warning ('The Inno Setup version could not be read (' + ($found.Seen -join '; ') + '); ISCC itself refuses directives it does not know.')
        $isccVersion = 'unknown version'
    }
    Write-Output ('Inno Setup ' + $isccVersion + ': ' + $compiler)
}
$icon = Join-Path $root 'src/Armory.Agent/Assets/armory.ico'
function Invoke-Iscc([string]$script, [string[]]$defines) {
    $arguments = @($defines) + @(('/DOutputDir=' + $dist))
    if (Test-Path -LiteralPath $icon) { $arguments += '/DIconFile=' + $icon }
    & $compiler @arguments (Join-Path $installer $script)
    if ($LASTEXITCODE -ne 0) { throw ('ISCC failed on ' + $script + ' with exit code ' + $LASTEXITCODE + '.') }
}

# --- The badges setup first: the payload carries it.
$badgesExe = Join-Path $dist ('IDEA-Armory-Badges-Setup-v' + $Version + '.exe')
$badgesHash = $null
foreach ($old in @($badgesExe, ($badgesExe + '.sha256'))) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force } }
if ($compiler) {
    Invoke-Iscc 'IdeaArmoryBadges.iss' @(('/DAppVersion=' + $Version), ('/DNativeDir=' + $native))
    if (-not (Test-Path -LiteralPath $badgesExe)) { throw ('ISCC did not produce ' + $badgesExe) }
    Assert-VersionResource $badgesExe (Split-Path -Leaf $badgesExe)
    $badgesHash = Write-Sha256 $badgesExe
}

# --- Stage the flash-drive folder.
$name = 'IDEA-Armory-USB-v' + $Version
$stage = Join-Path $dist $name
$files = Join-Path $stage 'files'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
foreach ($dir in @($files, (Join-Path $files 'scripts'), (Join-Path $stage 'logs'))) { [void][IO.Directory]::CreateDirectory($dir) }
foreach ($item in @(Get-ChildItem -LiteralPath $publish -Force)) { Copy-Item -LiteralPath $item.FullName -Destination $files -Recurse -Force }
Copy-Item -LiteralPath $shellExe -Destination (Join-Path $files 'ArmoryShell.exe') -Force
if ($badgesHash) {
    [void][IO.Directory]::CreateDirectory((Join-Path $files 'badges'))
    Copy-Item -LiteralPath $badgesExe -Destination (Join-Path $files $BadgesInPayload) -Force
}
foreach ($script in @('Setup.ps1', 'Uninstall.cmd', 'Check.cmd')) { Copy-Text (Join-Path $installer ('scripts/' + $script)) (Join-Path $files ('scripts/' + $script)) }
$driveTop = @('Install IDEA Armory.cmd', 'Uninstall IDEA Armory.cmd', 'Check IDEA Armory.cmd', 'Show Armory status on file icons.cmd', 'README.txt')
foreach ($top in $driveTop) { Copy-Text (Join-Path $installer ('usb/' + $top)) (Join-Path $stage $top) }
Copy-Text (Join-Path $installer 'usb/logs/_about-this-folder.txt') (Join-Path $stage 'logs/_about-this-folder.txt')

# The payload as both installers ship it: the app, the forwarder beside it, the badges setup.
Assert-NothingForbidden $files 'the payload (files\)'
$payloadNeeds = @('IdeaArmory.exe', 'ArmoryShell.exe', 'Assets/armory.ico', 'wwwroot/index.html', 'scripts/Setup.ps1')
if ($badgesHash) { $payloadNeeds += $BadgesInPayload }
foreach ($required in $payloadNeeds) {
    if (-not (Test-Path -LiteralPath (Join-Path $files $required))) { throw ('The payload has no ' + $required + '.') }
}
Assert-VersionResource (Join-Path $files 'ArmoryShell.exe') 'files\ArmoryShell.exe'
if ($badgesHash) { Assert-VersionResource (Join-Path $files $BadgesInPayload) ('files\' + $BadgesInPayload.Replace('/', '\')) }

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
if ($compiler) {
    Invoke-Iscc 'IdeaArmory.iss' @(('/DAppVersion=' + $Version), ('/DPayloadDir=' + $files))
    if (-not (Test-Path -LiteralPath $setupExe)) { throw ('ISCC did not produce ' + $setupExe) }
    Assert-VersionResource $setupExe (Split-Path -Leaf $setupExe)
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
if ($badgesHash) {
    $arches = 'x64'
    if ($hasArm64) { $arches = 'x64 and ARM64' }
    Write-Output ('Badges:   ' + $badgesExe + '  (' + [Math]::Round((Get-Item -LiteralPath $badgesExe).Length / 1MB, 1) + ' MB, ' + $arches + ')')
    Write-Output ('SHA-256:  ' + $badgesHash)
}
Write-Output ('Flash drive layout (' + $name + '):')
Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { '  ' + $_.Name + $(if ($_.PSIsContainer) { '\' } else { '' }) }
Write-Output ('  files\ holds ' + $payload.Count + ' files, listed with SHA-256 in files\scripts\payload.sha256')
if ($env:GITHUB_OUTPUT) {
    $out = @(('version=' + $Version), ('usb_zip=' + $zip), ('usb_sha256=' + $zipHash), ('arm64=' + $hasArm64.ToString().ToLowerInvariant()))
    if ($setupHash) { $out += @(('setup_exe=' + $setupExe), ('setup_sha256=' + $setupHash)) }
    if ($badgesHash) { $out += @(('badges_exe=' + $badgesExe), ('badges_sha256=' + $badgesHash)) }
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value $out -Encoding utf8
}
