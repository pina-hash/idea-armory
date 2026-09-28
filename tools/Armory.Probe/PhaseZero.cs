using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.Probe;

internal static class PhaseZero
{
    private sealed record Installation(string Name, string Version, string Location, string RegistryView);
    internal static int Run(string repository)
    {
        var reports = Path.Combine(Path.GetFullPath(repository), "docs", "spike");
        Directory.CreateDirectory(reports);
        var context = $"Measured: {DateTimeOffset.Now:O}\n\nWindows: {Environment.OSVersion.VersionString}; build {Environment.OSVersion.Version.Build}.\n\n";
        var installations = Installed();
        File.WriteAllText(Path.Combine(reports, "installed-solidworks.md"), "# Installed SolidWorks releases\n\n" + context +
            "Read-only inspection of both HKLM registry views, using the Windows uninstall display name/version and install location. No registry keys were written.\n\n" +
            (installations.Count == 0 ? "No installed SolidWorks releases found.\n" : string.Join("\n", installations.Select(i => $"- {i.Name}; product version `{i.Version}`; registry view {i.RegistryView}; executable present: {File.Exists(Path.Combine(i.Location, "SLDWORKS.exe"))}."))) + "\n");
        var found = FindDocuments(out var searchNotes);
        var scratch = Path.Combine(Path.GetFullPath(repository), "artifacts", "solidworks-probe");
        Directory.CreateDirectory(scratch);
        var measured = ProbeSolidWorks(scratch, found.FirstOrDefault(), installations.Count > 0);
        File.WriteAllText(Path.Combine(reports, "solidworks-lock-file.md"), "# SolidWorks lock-file behavior\n\n" + context + measured.Report);
        if (measured.GeneratedFile is not null) found.Add(measured.GeneratedFile);
        var release = new StringBuilder("# Saved release without SolidWorks\n\n" + context);
        release.AppendLine($"Search scope: current user's Documents and `C:\\IDEA\\Armory` if present, capped at 50 existing CAD files. {searchNotes}");
        release.AppendLine($"\nExisting files found: {found.Count - (measured.GeneratedFile is null ? 0 : 1)}. Generated fixture inspected: {(measured.GeneratedFile is null ? 0 : 1)}.\n");
        release.AppendLine("Method attempted: open the compound document read-only with Windows structured storage; enumerate streams and inspect bounded Header, VersionHistory, and OLE summary streams for explicit release information. This reader neither starts nor calls SolidWorks. Generic dates and undocumented internal version integers are not accepted as a saved release.\n");
        foreach (var file in found)
        {
            release.AppendLine($"## {Path.GetFileName(file)}\n");
            using (var input = File.OpenRead(file)) release.AppendLine($"SHA-256: `{Convert.ToHexStringLower(SHA256.HashData(input))}`.\n");
            try { release.AppendLine(CompoundInspection.Inspect(file)); }
            catch (Exception error) { release.AppendLine($"Could not inspect: {error.GetType().Name}: {error.Message}\n"); }
        }
        release.AppendLine("\nSolidWorks confirmation for the generated/copied probe document: " + measured.VersionEvidence + "\n");
        release.AppendLine("Validated standalone saved-release method: none. Validated reads: 0. No production `ISavedReleaseReader` is registered on the strength of unverified metadata. The core will refuse an unknown CAD release. Next measurement: multiple known releases and a documented vendor-supported format/API, or the scope's add-in stamping fallback bound to the content hash. No customer/team files were committed; generated probe files remain under ignored artifacts.\n");
        release.AppendLine("References: [Windows structured storage](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nn-objidl-istorage), [SOLIDWORKS version-history API](https://help.solidworks.com/2022/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~IVersionHistory.html). The latter requires the SolidWorks application and does not establish a standalone byte parser.");
        release.AppendLine("\n[Document Manager GetVersion](https://help.solidworks.com/2024/English/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMDocument~GetVersion.html) is a documented candidate, but a Document Manager key was not supplied. [Vendor guidance](https://help.solidworks.com/2026/english/api/swdocmgrapi/GettingStarted-swdocmgrapi.html?id=8.2) says standard compound-file techniques cannot externally read third-party data in files from SolidWorks 2015 onward. This explains why classic OLE inspection is insufficient; it is not evidence for an undocumented release-number mapping.");
        File.WriteAllText(Path.Combine(reports, "saved-release.md"), release.ToString());
        Benchmark(reports, context);
        Console.WriteLine($"Probe reports written to {reports}");
        return 0;
    }
    private static List<Installation> Installed()
    {
        List<Installation> results = [];
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var item = uninstall.OpenSubKey(name, writable: false);
                var display = item?.GetValue("DisplayName") as string;
                if (display is null || !System.Text.RegularExpressions.Regex.IsMatch(display, @"^SOLIDWORKS 20\d\d SP")) continue;
                results.Add(new(display, item?.GetValue("DisplayVersion") as string ?? "unknown", item?.GetValue("InstallLocation") as string ?? "", view.ToString()));
            }
        }
        return results;
    }
    private static List<string> FindDocuments(out string notes)
    {
        List<string> found = [];
        var failures = 0;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"C:\IDEA\Armory" })
        {
            if (!Directory.Exists(root)) continue;
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.TryPop(out var directory) && found.Count < 50)
            {
                try
                {
                    foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(item);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (Path.GetFileName(item) is not (".git" or "node_modules" or "bin" or "obj" or "artifacts")) directories.Push(item);
                        }
                        else if (new[] { ".sldprt", ".sldasm", ".slddrw" }.Contains(Path.GetExtension(item), StringComparer.OrdinalIgnoreCase))
                        { found.Add(item); if (found.Count == 50) break; }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures++; }
            }
        }
        notes = $"Skipped reparse/cloud placeholders and build/vendor/artifact folders; inaccessible directories: {failures}.";
        return found;
    }
    private sealed record SolidWorksProbe(string Report, string VersionEvidence, string? GeneratedFile);
    private static SolidWorksProbe ProbeSolidWorks(string scratch, string? sample, bool installed)
    {
        if (!installed) return new("SolidWorks is not installed; document-open and lock-file measurements skipped.\n", "Unavailable: not installed.", null);
        var report = new StringBuilder();
        object? appObject = null;
        dynamic? app = null;
        dynamic? model = null;
        var started = false;
        string? originalTitle = null;
        string? ownedTitle = null;
        string? generated = null;
        var versionEvidence = "No successful confirmation.";
        var pendingClose = false;
        var stage = "COM registration";
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application") ?? throw new InvalidOperationException("SolidWorks COM registration unavailable.");
            var clsid = type.GUID;
            stage = "Running Object Table attachment";
            var result = GetActiveObject(ref clsid, IntPtr.Zero, out appObject);
            if (result < 0)
            {
                if (Process.GetProcessesByName("SLDWORKS").Length > 0) throw new InvalidOperationException("An existing SolidWorks process is not accessible through the Running Object Table; left untouched.");
                appObject = Activator.CreateInstance(type);
                started = true;
            }
            app = new ComDispatch(appObject ?? throw new InvalidOperationException("No SolidWorks COM object."));
            stage = "reading the existing active document";
            var active = app.ActiveDoc;
            if (active is not null) originalTitle = (string)active.GetTitle();
            stage = "reading the application revision";
            report.AppendLine($"Application revision: `{app.RevisionNumber()}`. Connected to {(started ? "a probe-owned" : "an existing")} session; only the probe document will be closed.\n");
            string probeFile;
            if (sample is not null)
            {
                probeFile = Path.Combine(scratch, "probe-copy" + Path.GetExtension(sample));
                File.Copy(sample, probeFile, overwrite: true);
                report.AppendLine("Opened a copy of an existing document; the original was not modified.\n");
            }
            else
            {
                stage = "reading the default part template";
                var template = (string)app.GetUserPreferenceStringValue(8);
                if (string.IsNullOrWhiteSpace(template) || !File.Exists(template)) throw new InvalidOperationException("No usable default part template; a blank fixture cannot be generated noninteractively.");
                model = app.NewDocument(template, 0, 0d, 0d);
                if (model is null) throw new InvalidOperationException("SolidWorks could not create the blank part.");
                ownedTitle = (string)model.GetTitle();
                pendingClose = true;
                probeFile = Path.Combine(scratch, "generated-empty-2026.SLDPRT");
                int saveErrors = 0, saveWarnings = 0;
                if (!(bool)model.Extension.SaveAs(probeFile, 0, 1, null, ref saveErrors, ref saveWarnings))
                    throw new InvalidOperationException($"Blank-part save failed: errors={saveErrors}, warnings={saveWarnings}.");
                generated = probeFile;
                ownedTitle = (string)model.GetTitle();
                versionEvidence = $"Application revision {app.RevisionNumber()}; generated document version history: {FormatVariant(model.VersionHistory())}.";
                app.CloseDoc(ownedTitle);
                pendingClose = false;
                model = null;
                report.AppendLine("No existing CAD files were found within the permitted search roots. SolidWorks generated and saved a blank part from its default template, then closed it before the open/close measurement.\n");
            }
            var before = Directory.GetFiles(scratch).Select(Path.GetFileName).Order().ToArray();
            int errors = 0, warnings = 0;
            var kind = Path.GetExtension(probeFile).Equals(".sldasm", StringComparison.OrdinalIgnoreCase) ? 2 : Path.GetExtension(probeFile).Equals(".slddrw", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
            model = app.OpenDoc6(probeFile, kind, 1, "", ref errors, ref warnings);
            if (model is null) throw new InvalidOperationException($"OpenDoc6 failed: errors={errors}, warnings={warnings}.");
            ownedTitle = (string)model.GetTitle();
            pendingClose = true;
            var openAt = DateTimeOffset.Now;
            Thread.Sleep(500);
            var during = Directory.GetFiles(scratch).Select(Path.GetFileName).Order().ToArray();
            versionEvidence = $"Application revision {app.RevisionNumber()}; document version history: {FormatVariant(model.VersionHistory())}.";
            app.CloseDoc(ownedTitle);
            pendingClose = false;
            model = null;
            var closedAt = DateTimeOffset.Now;
            Thread.Sleep(500);
            var after = Directory.GetFiles(scratch).Select(Path.GetFileName).Order().ToArray();
            report.AppendLine($"Opened: {openAt:O}; closed: {closedAt:O}; folder sampled 500 ms after each transition.\n");
            report.AppendLine($"Before open: `{string.Join(", ", before)}`.\n\nWhile open: `{string.Join(", ", during)}`.\n\nAfter close: `{string.Join(", ", after)}`.\n");
            var created = during.Except(before).Where(n => n?.StartsWith("~$", StringComparison.Ordinal) == true).ToArray();
            report.AppendLine($"Observed new `~$` files: {created.Length}; removed after close: {created.Count(n => !after.Contains(n))}. This is an observation of this release and document, not the open-file detector's authority. `~$*` is always excluded from sync.\n");
        }
        catch (Exception error) { report.AppendLine($"Probe could not complete during {stage}: {error.GetType().Name}: {error.Message}\n"); Console.WriteLine(error.ToString()); }
        finally
        {
            try
            {
                if (pendingClose && app is not null && ownedTitle is not null) app.CloseDoc(ownedTitle);
                if (app is not null && originalTitle is not null)
                { int errors = 0; app.ActivateDoc3(originalTitle, false, 0, ref errors); }
                if (started && app is not null) app.ExitApp();
            }
            catch (Exception error) { report.AppendLine($"Probe cleanup note: {error.Message}\n"); }
            if (appObject is not null && Marshal.IsComObject(appObject)) Marshal.ReleaseComObject(appObject);
        }
        return new(report.ToString(), versionEvidence, generated);
    }
    private static string FormatVariant(object? value) => value is Array array ? string.Join(" | ", array.Cast<object>()) : value?.ToString() ?? "unavailable";
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? result);

    private static void Benchmark(string reports, string context)
    {
        var defender = "Unknown: status query unavailable.";
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("Get-MpComputerStatus | Select-Object AntivirusEnabled,RealTimeProtectionEnabled,AMProductVersion | ConvertTo-Json -Compress");
            using var process = Process.Start(start)!;
            defender = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0) defender = "Status query failed: " + process.StandardError.ReadToEnd().Trim();
        }
        catch (Exception error) { defender = error.Message; }
        var parent = Path.Combine(Path.GetTempPath(), "Armory-C2-Probes");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<double> timings = [];
        List<string> failures = [];
        var violations = 0;
        var retries = 0;
        var bytes = new byte[5 * 1024 * 1024];
        new Random(5669).NextBytes(bytes);
        var old = new byte[] { 1, 2, 3 };
        var oldHash = Convert.ToHexStringLower(SHA256.HashData(old));
        var replacementHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var total = Stopwatch.StartNew();
        try
        {
            using var replacer = new SafeFileReplace(new WindowsPaths(root));
            for (var i = 0; i < 100; i++)
            {
                var name = $"benchmark-{i:D3}.bin";
                File.WriteAllBytes(Path.Combine(root, name), old);
                VaultPath.TryCreate(name, out var path, out _);
                var watch = Stopwatch.StartNew();
                var result = replacer.Replace(path, oldHash, new MemoryStream(bytes, writable: false));
                watch.Stop();
                timings.Add(watch.Elapsed.TotalMilliseconds);
                violations += result.SharingViolations;
                retries += result.Retries;
                if (!result.Succeeded) failures.Add($"{name}: {result.Problem}");
                else
                {
                    using var check = File.OpenRead(Path.Combine(root, name));
                    if (Convert.ToHexStringLower(SHA256.HashData(check)) != replacementHash) failures.Add($"{name}: hash mismatch");
                }
            }
        }
        finally
        {
            total.Stop();
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe probe cleanup root.");
            Directory.Delete(root, recursive: true);
        }
        timings.Sort();
        File.WriteAllText(Path.Combine(reports, "antivirus-replace.md"), "# Antivirus and safe replacement\n\n" + context +
            $"Defender before measurement: `{defender}`. No settings changed.\n\n" +
            $"Measured 100 destination files, each replaced with 5 MiB (5,242,880 bytes). Same-volume hidden staging, write-through temp file, full flush, destination re-hash, open-file checks, and atomic MoveFileEx replacement were included. Final hashes were verified outside each timed interval.\n\n" +
            $"Succeeded: {100 - failures.Count}/100. Sharing violations: {violations}. Retries: {retries}.\n\n" +
            $"Mean: {timings.Average():F3} ms; median: {timings[49]:F3} ms; p95: {timings[94]:F3} ms; maximum: {timings[^1]:F3} ms. Total including setup and validation: {total.Elapsed.TotalSeconds:F3} seconds.\n\n" +
            (failures.Count == 0 ? "No failed replacements. This single run does not prove Defender caused or cannot cause interference.\n" : string.Join("\n", failures.Select(f => "- " + f))) + "\n");
    }
}
