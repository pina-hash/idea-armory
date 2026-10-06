param([string]$OutputDirectory = 'artifacts/mutations')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repo
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$cases = @(
    @{
        Name = 'open-file'
        File = 'src/Armory.Core/Reconciler.cs'
        Filter = 'Seeded_scenarios'
        Before = 'input.IsOpen ? SyncActionKind.NotifyNewerVersionWaiting : SyncActionKind.Download'
        After = 'false ? SyncActionKind.NotifyNewerVersionWaiting : SyncActionKind.Download'
    },
    @{
        Name = 'conflict-side-version'
        File = 'src/Armory.Core/Reconciler.cs'
        Filter = 'Seeded_scenarios'
        Before = 'return Actions(preserve, Refresh()); // MUTATION: conflict preservation'
        After = 'return Actions(Refresh()); // MUTATION: conflict preservation'
    },
    @{
        Name = 'lock-before-upload'
        File = 'src/Armory.Core/Reconciler.cs'
        Filter = 'Seeded_scenarios'
        Before = 'input.Lock == LockOwnership.ThisDevice ? SyncActionKind.Upload : SyncActionKind.AcquireLockThenUpload'
        After = 'SyncActionKind.Upload'
    },
    @{
        Name = 'torn-journal'
        File = 'src/Armory.Core/OfflineJournal.cs'
        Filter = 'Seeded_scenarios'
        Before = 'if (remaining < frameLength) break; // MUTATION: torn entry dropped'
        After = 'if (remaining < frameLength) { entries.Add(JsonSerializer.Deserialize<JournalEntry>(bytes.AsSpan(position + HeaderSize, Math.Min(length, remaining - HeaderSize)))!); return new(entries, bytes.Length, false); } // MUTATION: torn entry treated as committed'
    },
    @{
        Name = 'explicit-change-without-check-out'
        File = 'src/Armory.Core/Reconciler.cs'
        Filter = 'Seeded_explicit_checkout_scenarios'
        Before = 'if (input.Lock == LockOwnership.Free) return Actions(Keep(SideVersionReason.ChangedWithoutCheckOut), Refresh()); // MUTATION: change without a check out'
        After = 'if (input.Lock == LockOwnership.Free) return Actions(new SyncAction(SyncActionKind.AcquireLockThenUpload, null, releaseNotChecked)); // MUTATION: change without a check out'
    },
    @{
        Name = 'explicit-shared-before-check-in'
        File = 'src/Armory.Core/Reconciler.cs'
        Filter = 'Seeded_explicit_checkout_scenarios'
        Before = '_ => Actions(Keep(SideVersionReason.SavedWhileCheckedOut)), // MUTATION: shared only at check in'
        After = '_ => Actions(new SyncAction(SyncActionKind.Upload, null, releaseNotChecked)), // MUTATION: shared only at check in'
    },
    @{
        Name = 'explicit-undo-without-keeping'
        File = 'src/Armory.Core/Reconciler.cs'
        Filter = 'Seeded_explicit_checkout_scenarios'
        Before = 'CheckoutRequest.Undo => Actions(Keep(SideVersionReason.UndoCheckOut), Refresh()),'
        After = 'CheckoutRequest.Undo => Actions(Refresh()),'
    }
)
$results = @()
foreach ($case in $cases) {
    $path = Join-Path $repo $case.File
    $original = [IO.File]::ReadAllBytes($path)
    $beforeHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $source = [Text.Encoding]::UTF8.GetString($original)
    $occurrences = ($source.Length - $source.Replace($case.Before, '').Length) / $case.Before.Length
    if ($occurrences -ne 1) { throw "Mutation anchor for $($case.Name) occurs $occurrences times; it must occur exactly once." }
    try {
        [IO.File]::WriteAllText($path, $source.Replace($case.Before, $case.After), [Text.UTF8Encoding]::new($false))
        $log = (& dotnet test tests/Armory.Core.Tests --filter $case.Filter --logger 'console;verbosity=normal' 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
        [IO.File]::WriteAllText((Join-Path $OutputDirectory "$($case.Name).log"), $log)
        $match = [regex]::Match($log, 'REPRO: ARMORY_SEED=(\d+) dotnet test --filter ' + [regex]::Escape($case.Filter) + '[^\r\n]*')
        if ($exitCode -eq 0 -or -not $match.Success) { throw "Simulation did not catch $($case.Name) with a reproduction seed. See its log." }
        $results += [pscustomobject]@{ Name = $case.Name; Filter = $case.Filter; Seed = [int]$match.Groups[1].Value; Reproduction = $match.Value; OriginalSha256 = $beforeHash; Caught = $true }
        Write-Output "$($case.Name): CAUGHT seed=$($match.Groups[1].Value)"
    }
    finally {
        [IO.File]::WriteAllBytes($path, $original)
        $afterHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($afterHash -ne $beforeHash) { throw "Byte-identical restoration failed for $path" }
    }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
Write-Output "All $($cases.Count) breaks caught. Every source restored byte-identical. Rebuild before further tests."
