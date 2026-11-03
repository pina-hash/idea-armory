# Isolated safety checks against the production functions, without running its machine inventory.
param([Parameter(Mandatory = $true)][string]$Package)
$ErrorActionPreference = 'Stop'
$scriptFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/scripts/Retire.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptFile, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$names = @('Audit', 'Initialize-NativePaths', 'Full', 'Within', 'Linked', 'Binary', 'Protected', 'OverlapsProtected',
    'Add-Copy', 'Stream-Hash', 'Owned-VelopackTree', 'Unmodified-LegacyPayload')
foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($function.Name -in $names) { Invoke-Expression $function.Extent.Text }
}
function Require([bool]$ok, [string]$text) { if (-not $ok) { throw $text }; Write-Output ('PASS: ' + $text) }
$sandbox = Join-Path $env:TEMP ('Armory-retirement-safety-' + [Guid]::NewGuid().ToString('N'))
$DataFolder = Join-Path $sandbox 'data'
$currentRoot = Join-Path $sandbox 'survivor'
$CurrentExe = Join-Path $currentRoot 'IdeaArmory.exe'
$protected = New-Object 'System.Collections.Generic.List[string]'; $protected.Add($DataFolder)
$copies = @{}
try {
    [void][IO.Directory]::CreateDirectory($currentRoot)
    [void][IO.Directory]::CreateDirectory($DataFolder)
    Require (Within (Join-Path $currentRoot 'child\file') $currentRoot) 'containment accepts a real child'
    Require (-not (Within ($currentRoot + '-other\file') $currentRoot)) 'a shared path prefix is not containment'
    Require (Within $currentRoot ([IO.Path]::GetPathRoot($currentRoot))) 'drive roots normalize without becoming drive-relative'
    $fakeRoot = Join-Path $sandbox 'other-install'
    $current = Join-Path $fakeRoot 'current'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unpacked = Join-Path $sandbox 'unpacked'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package, $unpacked)
    [void][IO.Directory]::CreateDirectory($fakeRoot)
    Move-Item -LiteralPath (Join-Path $unpacked 'lib\app') -Destination $current
    [void][IO.Directory]::CreateDirectory((Join-Path $fakeRoot 'packages'))
    [xml]$manifest = [IO.File]::ReadAllText((Join-Path $current 'sq.version'))
    Copy-Item -LiteralPath $Package -Destination (Join-Path $fakeRoot ('packages\IDEAArmory-' + $manifest.package.metadata.version + '-full.nupkg'))
    Copy-Item -LiteralPath (Join-Path $current 'Squirrel.exe') -Destination (Join-Path $fakeRoot 'Update.exe')
    Copy-Item -LiteralPath (Join-Path $current 'IDEA Armory_ExecutionStub.exe') -Destination (Join-Path $fakeRoot 'IDEA Armory.exe')
    $copy = [pscustomobject]@{ Root = $fakeRoot; Exe = (Join-Path $current 'IdeaArmory.exe'); Kind = 'velopack' }
    Move-Item -LiteralPath $copy.Exe -Destination ($copy.Exe + '.retired')
    Require (Owned-VelopackTree $copy) 'a pristine package-owned tree permits the real updater uninstaller'
    $studentFile = Join-Path $fakeRoot 'student-work.sldprt'
    [IO.File]::WriteAllText($studentFile, 'never delete this')
    Require (-not (Owned-VelopackTree $copy)) 'an untracked student file prevents recursive updater uninstall'
    Require ([IO.File]::ReadAllText($studentFile) -ceq 'never delete this') 'ownership checks leave student bytes unchanged'
    Remove-Item -LiteralPath $studentFile
    $changed = Join-Path $current 'IdeaArmory.runtimeconfig.json'
    $original = [IO.File]::ReadAllBytes($changed)
    [IO.File]::AppendAllText($changed, 'changed')
    Require (-not (Owned-VelopackTree $copy)) 'a changed payload prevents recursive updater uninstall'
    [IO.File]::WriteAllBytes($changed, $original)
    $junction = Join-Path $fakeRoot 'linked-data'
    New-Item -ItemType Junction -Path $junction -Target $DataFolder | Out-Null
    Require (-not (Owned-VelopackTree $copy)) 'a junction prevents recursive updater uninstall'
    Require (Protected (Join-Path $junction 'settings.json')) 'protected paths include a junction before its missing leaf'
    [IO.Directory]::Delete($junction)
    Require (Owned-VelopackTree $copy) 'removing only the test junction restores the ownership proof'
    Add-Copy $copy.Exe
    Require ($copies.Count -eq 1) 'an install whose executable was removed is still found by its remaining payload'
    $copies = @{}
    $protected.Add($fakeRoot)
    Add-Copy $copy.Exe
    Require ($copies.Count -eq 0) 'an install inside protected data is never retired'
    [void]$protected.Remove($fakeRoot)
    [void][IO.Directory]::CreateDirectory((Join-Path $fakeRoot '.armory'))
    Add-Copy $copy.Exe
    Require ($copies.Count -eq 0) 'a vault marker protects the entire ancestor tree'
    [IO.Directory]::Delete((Join-Path $fakeRoot '.armory'))
    $protected.Add((Join-Path $fakeRoot 'nested-vault'))
    Require (OverlapsProtected $fakeRoot) 'a parent of a configured vault cannot be handed to an uninstaller'
    [void]$protected.Remove((Join-Path $fakeRoot 'nested-vault'))
    # A legacy manifest may preserve unlisted user files, but never discard edited payloads.
    $legacy = Join-Path $sandbox 'legacy'
    [void][IO.Directory]::CreateDirectory((Join-Path $legacy 'scripts'))
    $legacyExe = Join-Path $legacy 'IdeaArmory.exe'
    Copy-Item -LiteralPath ($copy.Exe + '.retired') -Destination $legacyExe
    $hash = (Get-FileHash -LiteralPath $legacyExe -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $legacy 'scripts\payload.sha256'), $hash + '  IdeaArmory.exe')
    $legacyCopy = [pscustomobject]@{ Root = $legacy; Exe = $legacyExe }
    Require (Unmodified-LegacyPayload $legacyCopy) 'the old uninstaller may remove its unchanged recorded payload'
    [IO.File]::AppendAllText($legacyExe, 'changed')
    Require (-not (Unmodified-LegacyPayload $legacyCopy)) 'an edited payload defers the old uninstaller'
    [IO.File]::WriteAllText((Join-Path $legacy 'scripts\payload.sha256'), $hash + '  ..\data\settings.json')
    Require (-not (Unmodified-LegacyPayload $legacyCopy)) 'a traversal in a manifest cannot reach settings'
    Write-Output 'PASS: retirement safety checks used only a throwaway folder.'
} catch {
    $audit = Join-Path $DataFolder 'logs\install-retirement.jsonl'
    if (Test-Path -LiteralPath $audit) { Get-Content -LiteralPath $audit | Write-Output }
    throw
} finally {
    # No junction is followed by cleanup, including when an assertion failed.
    $junction = Join-Path $sandbox 'other-install\linked-data'
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
