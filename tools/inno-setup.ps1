# Finds ISCC.exe and reads its version. Dot-sourced by tools/package-agent.ps1 and by the
# package-agent CI action. Runs under PowerShell 7 and Windows PowerShell 5.1.
#
# ISCC.exe's version resource is not a reliable source: on windows-latest, Inno Setup 6.7.1's
# ISCC.exe read as 0.0.0.0. So the version comes from the first source that names one:
# the compiler's own banner, the file's version resource, then the Inno Setup uninstall entry
# whose install folder holds this ISCC.exe.

function ConvertTo-InnoVersion([string]$text) {
    if ($text -match '(\d+)\.(\d+)(?:\.(\d+))?') {
        $build = 0
        if ($Matches[3]) { $build = [int]$Matches[3] }
        $version = New-Object System.Version ([int]$Matches[1]), ([int]$Matches[2]), $build
        if ($version.Major -ge 1) { return $version }
    }
    return $null
}

function Find-Iscc([string[]]$extra = @()) {
    $candidates = @($extra)
    foreach ($pair in @(@(${env:ProgramFiles(x86)}, 'Inno Setup 6\ISCC.exe'), @($env:ProgramFiles, 'Inno Setup 6\ISCC.exe'),
            @($env:LOCALAPPDATA, 'Programs\Inno Setup 6\ISCC.exe'))) {
        if ($pair[0]) { $candidates += (Join-Path $pair[0] $pair[1]) }
    }
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    return $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

# Returns @{ Version = [version] or $null; Source = text; Seen = every reading }.
function Get-IsccVersion([string]$path) {
    $seen = @()

    # ISCC /? prints its usage and exits with a nonzero code; neither may stop the caller.
    $banner = ''
    $preference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $banner = (& $path '/?' 2>&1 | Out-String) } catch { $banner = '' }
    finally { $ErrorActionPreference = $preference; $global:LASTEXITCODE = 0 }
    $line = ($banner -split "`r?`n" | Where-Object { $_ -match 'Inno Setup' } | Select-Object -First 1)
    $seen += ('banner: ' + $line)
    if ($banner -match 'Inno Setup[^\r\n\d]*(\d+\.\d+(?:\.\d+)?)') {
        $version = ConvertTo-InnoVersion $Matches[1]
        if ($version) { return @{ Version = $version; Source = 'banner'; Seen = $seen } }
    }

    $info = (Get-Item -LiteralPath $path).VersionInfo
    $readings = @(
        @('ProductVersion', [string]$info.ProductVersion),
        @('FileVersion', [string]$info.FileVersion),
        @('FileVersion parts', ('{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart)),
        @('ProductVersion parts', ('{0}.{1}.{2}' -f $info.ProductMajorPart, $info.ProductMinorPart, $info.ProductBuildPart)))
    foreach ($reading in $readings) {
        $seen += ($reading[0] + ': ' + $reading[1])
        $version = ConvertTo-InnoVersion $reading[1]
        if ($version) { return @{ Version = $version; Source = $reading[0]; Seen = $seen } }
    }

    $folder = [IO.Path]::GetFullPath((Split-Path -Parent $path)).TrimEnd('\', '/')
    $keys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')
    foreach ($key in $keys) {
        $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if (-not $entry) { continue }
        $location = ([string]$entry.InstallLocation).TrimEnd('\', '/')
        $seen += ($key + ': ' + [string]$entry.DisplayVersion + ' at ' + $location)
        if ($location -and ($location -ieq $folder)) {
            $version = ConvertTo-InnoVersion ([string]$entry.DisplayVersion)
            if ($version) { return @{ Version = $version; Source = 'uninstall entry'; Seen = $seen } }
        }
    }

    return @{ Version = $null; Source = 'none'; Seen = $seen }
}
