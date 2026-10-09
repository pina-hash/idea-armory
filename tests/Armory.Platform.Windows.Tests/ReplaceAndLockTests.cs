using Armory.Core;

namespace Armory.Platform.Windows.Tests;

public sealed class ReplaceAndLockTests
{
    [WindowsFact]
    public void Atomic_replace_succeeds_and_private_staging_is_hidden_and_empty()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1, 2, 3]);
        using var replace = new SafeFileReplace(vault.Paths);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1, 2, 3]), new MemoryStream([4, 5, 6]));
        Assert.True(result.Succeeded, result.Problem);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(vault.File("part.txt")));
        Assert.True((File.GetAttributes(vault.File(".armory")) & FileAttributes.Hidden) != 0);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public async Task Other_process_exclusive_handle_refuses_replace_and_reports_process()
    {
        using var vault = new TestVault();
        var file = vault.File("part.txt");
        File.WriteAllBytes(file, [1, 2, 3]);
        using var child = new ChildProcess("hold", file);
        Assert.Equal("READY", await child.ReadLine());
        var status = new OpenFileDetector().Inspect(file);
        Assert.True(status.IsOpen);
        Assert.Contains(status.Processes, p => p.Id == child.Process.Id && !string.IsNullOrWhiteSpace(p.Name));
        using var replace = new SafeFileReplace(vault.Paths);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1, 2, 3]), new MemoryStream([4]));
        Assert.False(result.Succeeded);
        child.Kill();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public void Destination_saved_between_plan_and_replace_is_preserved()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using var replace = new SafeFileReplace(vault.Paths);
        replace.AfterStaging = _ => File.WriteAllBytes(vault.File("part.txt"), [2]);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1]), new MemoryStream([3]));
        Assert.False(result.Succeeded);
        Assert.Contains("changed", result.Problem);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(vault.File("part.txt")));
    }

    [WindowsFact]
    public void Unexpected_new_destination_is_never_overwritten()
    {
        using var vault = new TestVault();
        using var replace = new SafeFileReplace(vault.Paths);
        Assert.True(replace.Replace(TestVault.PathValue(), null, new MemoryStream([1])).Succeeded);
        Assert.False(replace.Replace(TestVault.PathValue(), null, new MemoryStream([2])).Succeeded);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(vault.File("part.txt")));
    }

    // A read-only destination (a file this computer has not checked out) is replaced without its
    // bit ever being cleared: it is still read-only while the new bytes are staged and at the
    // last moment before the rename, and the new bytes arrive read-only. A rename that fails
    // keeps the old bytes, the bit and no staged copy. Both rename paths: FileRenameInfoEx
    // ignoring the read-only attribute, and the fallback for file systems without it, which
    // clears the bit only after every check, for MoveFileEx itself.
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_native_replace_preserves_readonly_destination_and_cleans_stage(bool fallback)
    {
        using var vault = new TestVault();
        var file = vault.File("part.txt");
        File.WriteAllBytes(file, [1]);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        bool ReadOnly() => File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly);
        using var replace = new SafeFileReplace(vault.Paths) { ForceRenameFallback = fallback };
        List<bool> seen = [];
        replace.AfterStaging = _ => seen.Add(ReadOnly());
        replace.BeforeRename = _ => seen.Add(ReadOnly());
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1]), new MemoryStream([2]));
        Assert.True(result.Succeeded, result.Problem);
        Assert.Equal([true, true], seen);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(file));
        Assert.True(ReadOnly());
        Assert.Equal(fallback ? 1 : 0, replace.FallbackRenames);

        // The destination is opened in the last instant, so the rename itself fails.
        FileStream? holder = null;
        replace.BeforeRename = target => holder ??= new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { Assert.False(replace.Replace(TestVault.PathValue(), TestVault.Hash([2]), new MemoryStream([3])).Succeeded); }
        finally { holder?.Dispose(); }
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(file));
        Assert.True(ReadOnly());
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public async Task Process_crash_after_staging_preserves_old_bytes_and_restart_cleans_temp()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using (var child = new ChildProcess("stage", vault.Root, TestVault.Hash([1])))
        { Assert.Equal("STAGED", await child.ReadLine()); child.Kill(); }
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(vault.File("part.txt")));
        using var restart = new SafeFileReplace(vault.Paths);
        Assert.Equal(1, restart.CleanedOrphans);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    // The v2 rule itself, on every host, so a deliberate break of ReadOnlyPolicy's copy turns a
    // Linux run red too (until the integration makes it call Armory.Core's
    // CheckoutRules.IsReadOnlyOnDisk): read-only unless THIS device holds the check out.
    [Fact]
    public void The_rule_is_read_only_unless_this_device_holds_the_check_out()
    {
        Assert.True(ReadOnlyPolicy.IsReadOnly(LockOwnership.Free));
        Assert.False(ReadOnlyPolicy.IsReadOnly(LockOwnership.ThisDevice));
        Assert.True(ReadOnlyPolicy.IsReadOnly(LockOwnership.MyOtherDevice));
        Assert.True(ReadOnlyPolicy.IsReadOnly(LockOwnership.OtherPerson));
        Assert.Equal(4, Enum.GetValues<LockOwnership>().Length);
    }

    // The v2 rule: read-only unless THIS device holds the check out.
    [WindowsFact]
    public void Readonly_attributes_follow_all_lock_ownership_states()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        File.SetAttributes(vault.File("part.txt"), FileAttributes.Hidden);
        using var policy = new ReadOnlyPolicy(vault.Paths);
        foreach (var ownership in Enum.GetValues<LockOwnership>().Concat(Enum.GetValues<LockOwnership>().Reverse()))
        {
            policy.Apply(TestVault.PathValue(), ownership);
            Assert.Equal(ownership != LockOwnership.ThisDevice, (File.GetAttributes(vault.File("part.txt")) & FileAttributes.ReadOnly) != 0);
            Assert.Equal(ownership != LockOwnership.ThisDevice, ReadOnlyPolicy.IsReadOnly(ownership));
            Assert.True(File.GetAttributes(vault.File("part.txt")).HasFlag(FileAttributes.Hidden), "Other attributes are kept.");
        }
    }

    [WindowsFact]
    public void A_batch_of_read_only_intents_is_persisted_once_and_one_failure_never_stops_the_rest()
    {
        using var vault = new TestVault();
        var names = Enumerable.Range(0, 200).Select(i => $"part-{i:D3}.txt").ToArray();
        foreach (var name in names) File.WriteAllBytes(vault.File(name), [1]);
        bool ReadOnly(string name) => File.GetAttributes(vault.File(name)).HasFlag(FileAttributes.ReadOnly);
        (VaultPath, LockOwnership)[] Batch(bool flipped) => names.Select((name, i) => (TestVault.PathValue(name),
            (i % 2 == 0) != flipped ? LockOwnership.ThisDevice : LockOwnership.Free)).ToArray();
        using var policy = new ReadOnlyPolicy(vault.Paths);
        var persisted = 0;
        policy.AfterIntentPersisted = () => persisted++;
        Assert.Empty(policy.ApplyMany(Batch(flipped: false)));
        Assert.Equal(1, persisted);
        Assert.Equal(100, policy.AttributeWrites);
        for (var i = 0; i < names.Length; i++) Assert.Equal(i % 2 != 0, ReadOnly(names[i]));
        // Applying the same rule again writes no manifest and changes no attribute (so no
        // change notification wakes the engine).
        Assert.Empty(policy.ApplyMany(Batch(flipped: false)));
        Assert.Empty(policy.ApplyMany([]));
        Assert.Equal(1, persisted);
        Assert.Equal(100, policy.AttributeWrites);

        // Windows refuses one file's bit: that file is returned, every other one changes.
        var refused = new FileInfo(vault.File(names[1]));
        var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.WriteAttributes, System.Security.AccessControl.AccessControlType.Deny);
        var security = refused.GetAccessControl();
        security.AddAccessRule(deny);
        refused.SetAccessControl(security);
        try
        {
            var failed = Assert.Single(policy.ApplyMany(Batch(flipped: true)));
            Assert.Equal(names[1], failed.Path.Value);
            Assert.Equal(2, persisted);
            Assert.Equal(299, policy.AttributeWrites);
            for (var i = 0; i < names.Length; i++) Assert.Equal(i == 1 || i % 2 == 0, ReadOnly(names[i]));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            refused.SetAccessControl(security);
        }

        // A path with no file keeps no intent: a file that appears there later is one the
        // server does not have, and a restart leaves it writable.
        Assert.Empty(policy.ApplyMany([(TestVault.PathValue("later.txt"), LockOwnership.Free)]));
        File.WriteAllBytes(vault.File("later.txt"), [1]);
        policy.Dispose();
        using var restarted = new ReadOnlyPolicy(vault.Paths);
        Assert.Empty(restarted.Recover(new Dictionary<VaultPath, LockOwnership>()));
        Assert.False(ReadOnly("later.txt"));
    }

    // 0.1.0 wrote {"<path>": n}: paths, not files, under the rule where Free was writable. None
    // of it is applied after the upgrade (the engine applies the v2 rule on its first pass),
    // the next write is version 2, and an unreadable manifest never stops the start.
    [WindowsFact]
    public void A_0_1_0_manifest_is_never_applied_and_never_stops_the_start()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        Directory.CreateDirectory(vault.File(".armory"));
        File.WriteAllText(vault.File(".armory/read-only.json"), "{\"part.txt\":0,\"gone.txt\":3}");
        using (var policy = new ReadOnlyPolicy(vault.Paths))
        {
            Assert.Empty(policy.Recover(new Dictionary<VaultPath, LockOwnership>()));
            Assert.False(File.GetAttributes(vault.File("part.txt")).HasFlag(FileAttributes.ReadOnly));
            policy.Apply(TestVault.PathValue(), LockOwnership.Free);
        }
        using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(vault.File(".armory/read-only.json"))))
        {
            Assert.Equal(2, manifest.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(["part.txt"], manifest.RootElement.GetProperty("intents").EnumerateObject().Select(p => p.Name));
        }
        File.WriteAllText(vault.File(".armory/read-only.json"), "{oops");
        using var broken = new ReadOnlyPolicy(vault.Paths);
        Assert.Single(broken.Recover(new Dictionary<VaultPath, LockOwnership>()));
        Assert.True(File.GetAttributes(vault.File("part.txt")).HasFlag(FileAttributes.ReadOnly), "A bit already set stays set.");
    }

    [WindowsFact]
    public void A_moved_folder_keeps_its_read_only_intents()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Gearbox"));
        File.WriteAllBytes(vault.File("Gearbox/part.txt"), [1]);
        using (var policy = new ReadOnlyPolicy(vault.Paths))
        {
            policy.Apply(TestVault.PathValue("Gearbox/part.txt"), LockOwnership.Free);
            Directory.Move(vault.File("Gearbox"), vault.File("Drivetrain"));
            policy.Rekey("Gearbox", "Drivetrain");
        }
        File.SetAttributes(vault.File("Drivetrain/part.txt"), FileAttributes.Normal);
        using var restarted = new ReadOnlyPolicy(vault.Paths);
        restarted.Recover(new Dictionary<VaultPath, LockOwnership>());
        Assert.True(File.GetAttributes(vault.File("Drivetrain/part.txt")).HasFlag(FileAttributes.ReadOnly));
    }

    [WindowsFact]
    public void Replace_with_read_only_stages_a_read_only_copy_so_the_file_is_never_writable()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using var replace = new SafeFileReplace(vault.Paths);
        bool? stagedReadOnly = null;
        replace.AfterStaging = temp => stagedReadOnly = File.GetAttributes(temp).HasFlag(FileAttributes.ReadOnly);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1]), new MemoryStream([2]), readOnly: true);
        Assert.True(result.Succeeded, result.Problem);
        Assert.True(stagedReadOnly);
        Assert.True(File.GetAttributes(vault.File("part.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(vault.File("part.txt")));
        // A refused read-only replace still removes its read-only staged copy.
        replace.AfterStaging = null;
        Assert.False(replace.Replace(TestVault.PathValue("new.txt"), TestVault.Hash([9]), new MemoryStream([3]), readOnly: true).Succeeded);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public void A_read_only_staged_orphan_is_cleaned_at_startup()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File(".armory/downloads"));
        File.WriteAllBytes(vault.File(".armory/downloads/orphan.pending"), [1]);
        File.SetAttributes(vault.File(".armory/downloads/orphan.pending"), FileAttributes.ReadOnly);
        using var replace = new SafeFileReplace(vault.Paths);
        Assert.Equal(1, replace.CleanedOrphans);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public async Task One_inspection_finds_the_open_file_in_a_folder()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Gearbox"));
        var files = Enumerable.Range(0, 30).Select(i => vault.File($"Gearbox/part-{i:D2}.txt")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, [1]);
        var detector = new OpenFileDetector();
        Assert.False(detector.InspectAll(files, out var none).IsOpen);
        Assert.Null(none);
        using var child = new ChildProcess("hold", files[17]);
        Assert.Equal("READY", await child.ReadLine());
        var status = detector.InspectAll(files, out var first);
        Assert.True(status.IsOpen);
        Assert.Equal(files[17], first);
        Assert.Contains(status.Processes, p => p.Id == child.Process.Id);
    }

    // Many files at once (a pass asks this for every file): each answered as Inspect answers
    // it, a missing file is not open, and nothing is a Restart Manager session per file.
    [WindowsFact]
    public async Task One_question_for_many_files_answers_each_as_inspect_does()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Fonts"));
        var files = Enumerable.Range(0, 40).Select(i => vault.File($"Fonts/font-{i:D2}.ttf")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, [1]);
        var detector = new OpenFileDetector();
        Assert.Empty(detector.OpenAmong([.. files, vault.File("Fonts/missing.ttf")], TimeSpan.FromSeconds(30), out _));
        using var first = new ChildProcess("hold", files[17]);
        Assert.Equal("READY", await first.ReadLine());
        using var second = new ChildProcess("hold", files[31]);
        Assert.Equal("READY", await second.ReadLine());
        var open = detector.OpenAmong(files, TimeSpan.FromSeconds(30), out _);
        Assert.Equal(new[] { files[17], files[31] }, open.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(files, f => Assert.Equal(detector.Inspect(f).IsOpen, open.Contains(f)));
    }

    // 0.3.3 (feedback N6): 1,500 read-only files with some held, asked off the caller's thread
    // with a short budget. The answer comes within about the budget (the probe answers what
    // Restart Manager had not cleared, and finds the held files), a second question while the
    // first's query still runs never starts another (Restart Manager one question at a time), and
    // a canceled question ends at once.
    [WindowsFact]
    public async Task The_open_files_question_keeps_its_budget_and_runs_one_query_at_a_time()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Robot"));
        var files = Enumerable.Range(0, 1500).Select(i => vault.File($"Robot/part-{i:D4}.SLDPRT")).ToArray();
        foreach (var file in files)
        {
            File.WriteAllBytes(file, [1, 2, 3]);
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }
        using var first = new ChildProcess("hold", files[17]);
        Assert.Equal("READY", await first.ReadLine());
        using var second = new ChildProcess("hold", files[1031]);
        Assert.Equal("READY", await second.ReadLine());
        var detector = new OpenFileDetector();
        var budget = TimeSpan.FromSeconds(1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var answer = await detector.OpenAmongAsync(files, budget, fresh: false, CancellationToken.None);
        watch.Stop();
        Assert.True(watch.Elapsed < budget + TimeSpan.FromSeconds(2), $"the question took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.Contains(files[17], answer.Open);
        Assert.Contains(files[1031], answer.Open);
        var again = await detector.OpenAmongAsync(files, budget, fresh: false, CancellationToken.None);
        Assert.Contains(files[17], again.Open);
        Assert.Equal(1, detector.MostAtOnce);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.OpenAmongAsync(files, budget, fresh: true, stop.Token));
    }

    [WindowsFact]
    public async Task Crash_mid_attribute_update_recovers_to_current_users_lock()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using (var child = new ChildProcess("readonly-crash", vault.Root))
        { Assert.Equal("PERSISTED", await child.ReadLine()); child.Kill(); }
        Assert.True((File.GetAttributes(vault.File("part.txt")) & FileAttributes.ReadOnly) != 0);
        using var recovered = new ReadOnlyPolicy(vault.Paths);
        recovered.Recover(new Dictionary<VaultPath, LockOwnership> { [TestVault.PathValue()] = LockOwnership.ThisDevice });
        Assert.False((File.GetAttributes(vault.File("part.txt")) & FileAttributes.ReadOnly) != 0);
    }
}
