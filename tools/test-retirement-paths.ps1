# Test the production command boundary without inventorying or changing this computer.
# Works in PowerShell 5.1 and 7. The incident's quote must never reach a path API.
$ErrorActionPreference = 'Stop'
$scriptFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/scripts/Retire.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptFile, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($function.Name -in @('CommandFile', 'InventoryPath', 'Belongs')) { Invoke-Expression $function.Extent.Text }
}
$script:deferred = @()
function Audit([string]$text) { $script:deferred += $text }
function Require([bool]$ok, [string]$text) { if (-not $ok) { throw $text }; Write-Output ('PASS: ' + $text) }
# Real Run value shape in incidents 3a4bd4fc, 405f4cb6, 56d42b89 and 87c989be.
$wrapped = 'cmd /c "C:\WINDOWS\System32\MoNotificationUxStub.exe"'
if ($env:OS -eq 'Windows_NT') {
    $rejected = $false
    try { $null = [IO.Path]::GetFullPath('cmd /c "C:\WINDOWS\System32\MoNotificationUxStub.exe') }
    catch { $rejected = $true }
    Require $rejected 'the real Windows path API rejects the logged command text'
}
Require ($null -eq (CommandFile $wrapped)) 'a shell command is not a file path'
Require ($null -eq (CommandFile 'cmd /c "C:\WINDOWS\System32\MoNotificationUxStub.exe')) 'the logged truncated command is not a file path'
Require ((CommandFile '"C:\Users\IDEA\AppData\Local\IDEAArmory\IDEA Armory.exe" --silent') -ceq 'C:\Users\IDEA\AppData\Local\IDEAArmory\IDEA Armory.exe') 'quoted Armory commands keep the entire filename'
Require ((CommandFile 'C:\Program Files\IDEA Armory\IdeaArmory.exe,0') -ceq 'C:\Program Files\IDEA Armory\IdeaArmory.exe') 'an icon index is not part of the path'
Require ((InventoryPath '  "C:\Program Files\IDEA Armory"  ') -ceq 'C:\Program Files\IDEA Armory') 'surrounding registry quotes and whitespace are normalized'
foreach ($bad in @('C:\bad"name\IdeaArmory.exe', 'C:\bad|name\IdeaArmory.exe', 'C:\bad<name\IdeaArmory.exe', "C:\bad`0name\IdeaArmory.exe", 'C:\file:stream', 'relative\IdeaArmory.exe', '"C:\missing-quote')) {
    Require ($null -eq (InventoryPath $bad)) 'an invalid inventory target is deferred without changing its filename'
}
Require ($script:deferred.Count -ge 7) 'unusable registrations leave audit evidence'
# The survivor loop used to reparse a rejected Run value and abort the entire inventory.
function Full([string]$path) { if ($path -match '["|<>\x00]') { throw 'invalid characters reached the path API' }; return $path }
function Join-Path([string]$path, [string]$child) { return $path.TrimEnd('\') + '\' + $child }
$copy = [pscustomobject]@{ Root = 'C:\survivor' }
Require (-not (Belongs $wrapped $copy)) 'ownership comparisons also reject command text'
Require (Belongs 'C:\survivor\IdeaArmory.exe' $copy) 'valid ownership is still recognized after a malformed registration'
Write-Output 'PASS: retirement path checks changed no files or registrations.'
