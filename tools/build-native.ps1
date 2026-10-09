# Builds IDEA Armory's native parts with Visual Studio's compilers (docs/agent/EXPLORER.md):
#   <OutDir>\x64\ArmoryBadges.dll     the four File Explorer badge handlers (badges setup)
#   <OutDir>\arm64\ArmoryBadges.dll   the same for Windows on ARM (Explorer is native ARM64 there)
#   <OutDir>\x64\ArmoryShell.exe      what the right-click items run (ships beside IdeaArmory.exe)
#   <OutDir>\x64\BadgeProbe.exe       CI only: checks the DLL the way Explorer uses it
#   <OutDir>\x64\ShellPipeTest.exe    lab only: times forwarders started back to back
# Static runtime (/MT), so nothing needs the Visual C++ runtime; warnings are errors for the two
# shipped binaries; Control Flow Guard everywhere and CET shadow stacks on x64. It then checks
# that each shipped binary imports only the system DLLs it should (no USER32 inside Explorer's
# badge handlers, no Visual C++ runtime) and carries this version.
#
# It finds Visual Studio with vswhere and takes the developer environment from VsDevCmd.bat for
# each architecture, so it does not depend on CMake knowing the image's Visual Studio. Runs on
# windows-latest (Visual Studio with the x64 and ARM64 C++ tools and the Windows SDK) under
# PowerShell 7, and on a developer's computer under Windows PowerShell 5.1.
#
#   pwsh tools/build-native.ps1 [-Version 0.3.3] [-OutDir publish/native] [-Architectures x64,arm64]
param(
    [string]$Version = '',
    [string]$OutDir = 'publish/native',
    [string[]]$Architectures = @('x64', 'arm64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root 'native'
$out = if ([IO.Path]::IsPathRooted($OutDir)) { [IO.Path]::GetFullPath($OutDir) } else { [IO.Path]::GetFullPath((Join-Path $root $OutDir)) }

# The version is the one in src/Armory.Agent.
if (-not $Version) {
    $project = Get-Content -LiteralPath (Join-Path $root 'src/Armory.Agent/Armory.Agent.csproj') -Raw
    $Version = [regex]::Match($project, '<Version>([^<]+)</Version>').Groups[1].Value
}
$parts = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)$')
if (-not $parts.Success) { throw ('Version must look like 0.3.3, not "' + $Version + '".') }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio (vswhere.exe) was not found.' }

function Find-VisualStudio([string]$arch) {
    $component = if ($arch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $found = & $vswhere -latest -products * -requires $component -property installationPath
    if (-not $found) { throw ('Visual Studio with the C++ tools for ' + $arch + ' (' + $component + ') was not found.') }
    return @($found)[0]
}

# Runs one tool and stops the build when it fails.
function Invoke-Tool([string]$tool, [string[]]$arguments) {
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw ($tool + ' failed with exit code ' + $LASTEXITCODE + ': ' + ($arguments -join ' ')) }
}

# rc, run from the .rc file's own folder so the icons and the manifest it names are found there.
function Invoke-Rc([string]$rcFile, [string]$res, [string]$include) {
    Push-Location -LiteralPath (Split-Path -Parent $rcFile)
    try { Invoke-Tool 'rc' @('/nologo', '/I', $include, '/fo', $res, $rcFile) }
    finally { Pop-Location }
}

# The DLLs a binary imports, from dumpbin /dependents, in upper case.
function Get-Imports([string]$file) {
    $lines = & dumpbin /nologo /dependents $file
    if ($LASTEXITCODE -ne 0) { throw ('dumpbin failed on ' + $file) }
    return @($lines | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_.-]+\.dll$' } | ForEach-Object { $_.ToUpperInvariant() })
}

function Assert-Imports([string]$file, [string[]]$allowed) {
    $imports = Get-Imports $file
    $extra = @($imports | Where-Object { $allowed -notcontains $_ })
    if ($extra.Count -gt 0) { throw ((Split-Path -Leaf $file) + ' must not import ' + ($extra -join ', ') + ' (it imports ' + ($imports -join ', ') + ').') }
    Write-Host ((Split-Path -Leaf $file) + ' imports ' + ($imports -join ', '))
}

function Assert-Version([string]$file) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
    $actual = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
    if ($info.ProductName -ne 'IDEA Armory' -or $actual -ne $Version -or $info.ProductVersion -ne $Version) {
        throw ((Split-Path -Leaf $file) + ' carries "' + $info.ProductName + '" ' + $actual + ' (' + $info.ProductVersion + '), expected "IDEA Armory" ' + $Version + '.')
    }
}

$saved = @{}
Get-ChildItem Env: | ForEach-Object { $saved[$_.Name] = $_.Value }
foreach ($arch in $Architectures) {
    if ($arch -notin @('x64', 'arm64')) { throw ('Unknown architecture ' + $arch + '; use x64 or arm64.') }
    $bin = Join-Path $out $arch
    $obj = Join-Path $out ('obj\' + $arch)
    New-Item -ItemType Directory -Force -Path $bin, $obj | Out-Null
    $header = Get-Content -LiteralPath (Join-Path $native 'ArmoryVersion.h.in') -Raw
    $header = $header.Replace('@ARMORY_VERSION_MAJOR@', $parts.Groups[1].Value)
    $header = $header.Replace('@ARMORY_VERSION_MINOR@', $parts.Groups[2].Value)
    $header = $header.Replace('@ARMORY_VERSION_PATCH@', $parts.Groups[3].Value)
    [IO.File]::WriteAllText((Join-Path $obj 'ArmoryVersion.h'), $header, (New-Object Text.UTF8Encoding($false)))

    # This architecture's developer environment, on a clean copy of the original one.
    $vs = Find-VisualStudio $arch
    $devcmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
    @(Get-ChildItem Env: | Where-Object { -not $saved.ContainsKey($_.Name) }) | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
    foreach ($name in $saved.Keys) { Set-Item -LiteralPath ('Env:' + $name) -Value $saved[$name] }
    $environment = & cmd.exe /d /c "`"$devcmd`" -no_logo -arch=$arch -host_arch=amd64 && set"
    if ($LASTEXITCODE -ne 0) { throw ('VsDevCmd.bat failed for ' + $arch + '.') }
    foreach ($line in $environment) {
        if ($line -match '^([^=]+)=(.*)$') { Set-Item -LiteralPath ('Env:' + $Matches[1]) -Value $Matches[2] }
    }
    $cet = @()
    if ($arch -eq 'x64') { $cet = @('/CETCOMPAT') }
    $hardened = @('/guard:cf', '/DYNAMICBASE', '/NXCOMPAT') + $cet

    # The badge handlers: loaded into Explorer and every file dialog.
    Invoke-Rc (Join-Path $native 'badges\ArmoryBadges.rc') (Join-Path $obj 'ArmoryBadges.res') $obj
    Invoke-Tool 'cl' @('/nologo', '/c', '/O2', '/W4', '/WX', '/permissive-', '/sdl', '/GS', '/guard:cf', '/MT', '/EHsc', '/std:c++17',
        '/DUNICODE', '/D_UNICODE', ('/Fo' + (Join-Path $obj 'ArmoryBadges.obj')), (Join-Path $native 'badges\ArmoryBadges.cpp'))
    Invoke-Tool 'link' (@('/nologo', '/WX', '/DLL', ('/DEF:' + (Join-Path $native 'badges\ArmoryBadges.def')), ('/OUT:' + (Join-Path $bin 'ArmoryBadges.dll')),
        (Join-Path $obj 'ArmoryBadges.obj'), (Join-Path $obj 'ArmoryBadges.res'), '/MANIFEST:NO') + $hardened + @('kernel32.lib', 'advapi32.lib', 'uuid.lib'))
    Assert-Imports (Join-Path $bin 'ArmoryBadges.dll') @('KERNEL32.DLL', 'ADVAPI32.DLL')
    Assert-Version (Join-Path $bin 'ArmoryBadges.dll')

    if ($arch -ne 'x64') { continue }

    # The right-click forwarder: a plain Windows program with no window.
    Invoke-Rc (Join-Path $native 'shell\ArmoryShell.rc') (Join-Path $obj 'ArmoryShell.res') $obj
    Invoke-Tool 'cl' @('/nologo', '/c', '/O2', '/W4', '/WX', '/sdl', '/GS', '/guard:cf', '/MT', '/DUNICODE', '/D_UNICODE',
        ('/Fo' + (Join-Path $obj 'ArmoryShell.obj')), (Join-Path $native 'shell\ArmoryShell.c'))
    Invoke-Tool 'link' (@('/nologo', '/WX', ('/OUT:' + (Join-Path $bin 'ArmoryShell.exe')), (Join-Path $obj 'ArmoryShell.obj'), (Join-Path $obj 'ArmoryShell.res'),
        '/SUBSYSTEM:WINDOWS', '/MANIFEST:NO') + $hardened + @('kernel32.lib', 'advapi32.lib', 'shell32.lib', 'user32.lib', 'bcrypt.lib'))
    Assert-Imports (Join-Path $bin 'ArmoryShell.exe') @('KERNEL32.DLL', 'ADVAPI32.DLL', 'SHELL32.DLL', 'USER32.DLL', 'BCRYPT.DLL')
    Assert-Version (Join-Path $bin 'ArmoryShell.exe')

    # Test and lab tools, never shipped.
    Invoke-Tool 'cl' @('/nologo', '/c', '/O2', '/W4', '/MT', '/EHsc', '/std:c++17', '/DUNICODE', '/D_UNICODE', '/D_CRT_SECURE_NO_WARNINGS',
        ('/Fo' + (Join-Path $obj 'BadgeProbe.obj')), (Join-Path $native 'badges\BadgeProbe.cpp'))
    Invoke-Tool 'link' @('/nologo', ('/OUT:' + (Join-Path $bin 'BadgeProbe.exe')), (Join-Path $obj 'BadgeProbe.obj'), '/SUBSYSTEM:CONSOLE',
        'kernel32.lib', 'advapi32.lib', 'ole32.lib', 'uuid.lib')
    Invoke-Tool 'cl' @('/nologo', '/c', '/O2', '/W4', '/MT', '/DUNICODE', '/D_UNICODE', '/D_CRT_SECURE_NO_WARNINGS',
        ('/Fo' + (Join-Path $obj 'ShellPipeTest.obj')), (Join-Path $native 'shell\ShellPipeTest.c'))
    Invoke-Tool 'link' @('/nologo', ('/OUT:' + (Join-Path $bin 'ShellPipeTest.exe')), (Join-Path $obj 'ShellPipeTest.obj'), '/SUBSYSTEM:CONSOLE', 'kernel32.lib')
}
@(Get-ChildItem Env: | Where-Object { -not $saved.ContainsKey($_.Name) }) | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
foreach ($name in $saved.Keys) { Set-Item -LiteralPath ('Env:' + $name) -Value $saved[$name] }

Write-Host ('IDEA Armory native parts ' + $Version + ' in ' + $out + ':')
Get-ChildItem -LiteralPath $out -Recurse -File -Include *.dll, *.exe | ForEach-Object { Write-Host ('  ' + $_.FullName.Substring($out.Length + 1) + '  ' + $_.Length + ' bytes') }
