param([string]$OutputDirectory = 'artifacts/mutations')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repo
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$cases = @(
    @{
        Name = 'open-file'
        File = 'src/Armory.Core/Reconciler.cs'
        Before = 'input.IsOpen ? SyncActionKind.NotifyNewerVersionWaiting : SyncActionKind.Download'
        After = 'false ? SyncActionKind.NotifyNewerVersionWaiting : SyncActionKind.Download'
    },
    @{
        Name = 'conflict-side-version'
        File = 'src/Armory.Core/Reconciler.cs'
        Before = 'return Actions(preserve, Refresh()); // MUTATION: conflict preservation'
        After = 'return Actions(Refresh()); // MUTATION: conflict preservation'
    },
    @{
        Name = 'lock-before-upload'
        File = 'src/Armory.Core/Reconciler.cs'
        Before = 'return Actions(Action(input.Lock == LockOwnership.ThisDevice ? SyncActionKind.Upload : SyncActionKind.AcquireLockThenUpload));'
        After = 'return Actions(Action(SyncActionKind.Upload));'
    },
    @{
        Name = 'torn-journal'
        File = 'src/Armory.Core/OfflineJournal.cs'
        Before = 'if (remaining < frameLength) break; // MUTATION: torn entry dropped'
        After = 'if (remaining < frameLength) { entries.Add(JsonSerializer.Deserialize<JournalEntry>(bytes.AsSpan(position + HeaderSize, Math.Min(length, remaining - HeaderSize)))!); return new(entries, bytes.Length, false); } // MUTATION: torn entry treated as committed'
    }
)
$results = @()
foreach ($case in $cases) {
    $path = Join-Path $repo $case.File
    $original = [IO.File]::ReadAllBytes($path)
    $beforeHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $source = [Text.Encoding]::UTF8.GetString($original)
    if (-not $source.Contains($case.Before)) { throw "Mutation anchor missing: $($case.Name)" }
    try {
        [IO.File]::WriteAllText($path, $source.Replace($case.Before, $case.After), [Text.UTF8Encoding]::new($false))
        $log = (& dotnet test --filter Seeded_scenarios --logger 'console;verbosity=normal' 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
        [IO.File]::WriteAllText((Join-Path $OutputDirectory "$($case.Name).log"), $log)
        $match = [regex]::Match($log, 'REPRO: ARMORY_SEED=(\d+) dotnet test --filter Seeded_scenarios[^\r\n]*')
        if ($exitCode -eq 0 -or -not $match.Success) { throw "Simulation did not catch $($case.Name) with a reproduction seed. See its log." }
        $results += [pscustomobject]@{ Name = $case.Name; Seed = [int]$match.Groups[1].Value; Reproduction = $match.Value; OriginalSha256 = $beforeHash; Caught = $true }
        Write-Output "$($case.Name): CAUGHT seed=$($match.Groups[1].Value)"
    }
    finally {
        [IO.File]::WriteAllBytes($path, $original)
        $afterHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($afterHash -ne $beforeHash) { throw "Byte-identical restoration failed for $path" }
    }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
Write-Output 'All four breaks caught. Every source restored byte-identical. Rebuild before further tests.'
