using System.Text;

namespace Armory.Core;

// Several students taking turns on one computer with one shared Windows login (0.3.3, part F;
// docs/agent/PROFILES.md). Each student keeps a profile of their own (their own sign-in), the
// window opens on a picker, and one Armory folder is handed from student to student by the
// 0.3.2 rule. These are the rules the host follows; none of them touches a disk or a network.

// Why the window is asking whether to show the picker (PROFILES.md, "When the picker shows").
public enum PickerTrigger
{
    // Armory starts in the background (--background): it runs as the student in use, and the
    // picker shows when the window first opens.
    Start,
    // The window was hidden (closed with X, or never shown) and is shown now: the taskbar, the
    // Start menu, the desktop, a second launch, the tray, a balloon.
    ShownFromHidden,
    // Brought to the front or restored from minimized.
    Activated,
    // Windows was locked: the picker is up when someone unlocks it.
    WindowsLocked,
    // Switch student (Home, the tray).
    SwitchStudent,
    Minimized,
    // The student in use's sign-in ended (a refused refresh).
    SignInEnded,
}

// How a student gets a folder (PROFILES.md, "The shared folder").
public enum FolderStep
{
    // The shared folder is free or already theirs: stop whoever runs, start them there.
    UseShared,
    // The shared folder belongs to the student running on it now: ask that student's engine what
    // waits; nothing waits, so it is handed over; something waits, so it stays theirs.
    AskRunningOwner,
    // The shared folder belongs to someone not running on it (a student not in use, or an address
    // with no profile here): a stopped engine for the new student looks first, and takes it over
    // only when nothing of the owner's waits. Whoever runs keeps running until then.
    ProbeShared,
    // The student's own folder: their work waits there, or the shared folder can't be had.
    UseOwn,
}

// What the host knows before a switch. SharedOwner is the address the shared folder is bound
// to (null: nobody's yet). RunningEmail and RunningFolder are the student Armory runs for now
// and their folder (null: nobody). TargetFolder is the folder the student's profile names, the
// shared one or their own; TargetWaiting is what they had waiting there when they last stopped.
public sealed record FolderFacts(
    string TargetEmail, string SharedRoot, string? SharedOwner,
    string? RunningEmail, string? RunningFolder,
    string TargetFolder, int TargetWaiting, bool TargetFolderOpenInSolidWorks);

// Folder: where the student goes. OwnFolderIfBusy: for a student coming back from a folder of
// their own, where they stay when the shared folder can't be had (no question asked); null for
// a student whose folder is the shared one (the picker offers Wait or a folder of their own).
public sealed record FolderPlan(FolderStep Step, string Folder, string? OwnFolderIfBusy = null)
{
    // A student in their own folder goes back to the shared one, if it can be had.
    public bool MovesBack => Step != FolderStep.UseOwn && OwnFolderIfBusy is not null;
}

public static class SharedComputer
{
    public const int PinDigits = 4, FreePinTries = 5, PictureHues = 8;
    public static readonly TimeSpan FirstPinWait = TimeSpan.FromSeconds(30), LongestPinWait = TimeSpan.FromMinutes(15);
    // The longest folder path Armory takes (AgentSettings.TryNormalizeVaultRoot), so CAD file paths still fit.
    public const int MaximumFolderPath = 120;
    public const string TooEasyPin = "Pick a PIN that's harder to guess than 1234.";

    // Exactly four ASCII digits: no spaces, no other digits (a full-width 1 is not 1).
    public static bool IsPin(string? text)
    {
        if (text is null || text.Length != PinDigits) return false;
        foreach (var c in text) if (c is < '0' or > '9') return false;
        return true;
    }

    // A PIN anyone would try first: four of one digit, or a straight run up or down (1234, 9876).
    public static string? TooEasy(string pin)
    {
        if (!IsPin(pin)) return "Type 4 digits.";
        bool same = true, up = true, down = true;
        for (var i = 1; i < pin.Length; i++)
        {
            same &= pin[i] == pin[0];
            up &= pin[i] == pin[i - 1] + 1;
            down &= pin[i] == pin[i - 1] - 1;
        }
        return same || up || down ? TooEasyPin : null;
    }

    // After FreePinTries wrong PINs in a row: 30 seconds, then twice as long after each further
    // wrong PIN, never more than 15 minutes, and never a lockout for good (a classmate typing
    // wrong PINs must not shut a student out; the browser sign-in always gets them in).
    public static TimeSpan? PinWait(int wrongTries)
    {
        if (wrongTries < FreePinTries) return null;
        var doublings = Math.Min(wrongTries - FreePinTries, 10);
        var wait = TimeSpan.FromSeconds(FirstPinWait.TotalSeconds * (1 << doublings));
        return wait < LongestPinWait ? wait : LongestPinWait;
    }

    // Tries left before the first wait (0 once waits have begun).
    public static int PinTriesLeft(int wrongTries) => Math.Max(0, FreePinTries - wrongTries);

    // Whether the window opens on the picker. lastPicked: the local day someone last picked a
    // student here (null: never). A student continuing after their own X goes through the picker
    // too: nothing tells them apart from the next student.
    public static bool ShowPicker(PickerTrigger trigger, DateOnly? lastPicked, DateOnly today) => trigger switch
    {
        PickerTrigger.ShownFromHidden or PickerTrigger.WindowsLocked or PickerTrigger.SwitchStudent or PickerTrigger.SignInEnded => true,
        // The first open of the day, a window left open overnight included.
        PickerTrigger.Activated => lastPicked is not { } day || day < today,
        _ => false,
    };

    // The hand-over table (PROFILES.md, "The shared folder"). The engine's own answer (what waits,
    // the take-over) is always the authority; this only says which question to ask.
    public static FolderPlan PlanFolder(FolderFacts facts)
    {
        var own = !SameFolder(facts.TargetFolder, facts.SharedRoot);
        if (own && (facts.TargetWaiting > 0 || facts.TargetFolderOpenInSolidWorks)) return new(FolderStep.UseOwn, facts.TargetFolder);
        var stay = own ? facts.TargetFolder : null;
        if (facts.SharedOwner is not { } owner || SameEmail(owner, facts.TargetEmail)) return new(FolderStep.UseShared, facts.SharedRoot, stay);
        if (facts.RunningEmail is { } running && SameEmail(owner, running) && facts.RunningFolder is { } folder && SameFolder(folder, facts.SharedRoot))
            return new(FolderStep.AskRunningOwner, facts.SharedRoot, stay);
        return new(FolderStep.ProbeShared, facts.SharedRoot, stay);
    }

    // A folder of the student's own beside the shared one: C:\IDEA\Armory-alex for
    // alex.kim@boscotech.edu (the first part of the address, letters and digits only), then -2,
    // -3 while that name is taken (another profile's folder, or a folder bound to someone else).
    // Never longer than MaximumFolderPath.
    public static string OwnFolder(string sharedRoot, string email, IReadOnlyCollection<string> taken)
    {
        var root = sharedRoot.TrimEnd('\\', '/');
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var local = at >= 0 ? email[..at] : email;
        var first = local.Split(['.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var name = new StringBuilder();
        // Accents go (José is jose); anything else that is not a letter or digit of A to Z goes too.
        foreach (var c in first.Normalize(NormalizationForm.FormD).ToLowerInvariant()) if (c is >= 'a' and <= 'z' or >= '0' and <= '9') name.Append(c);
        if (name.Length == 0) name.Append("student");
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? "" : "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var room = Math.Max(1, MaximumFolderPath - root.Length - 1 - suffix.Length);
            var candidate = root + "-" + name.ToString(0, Math.Min(name.Length, room)) + suffix;
            if (!taken.Any(t => SameFolder(t, candidate))) return candidate;
        }
    }

    // The name a tile shows (the name the website shows when Armory knows it, else the address's
    // words: "alex.kim" is "Alex Kim"), its initials (first and last word, a title like "Mr."
    // skipped) and one of PictureHues circle colors, the same every time for an address.
    public static (string Name, string Initials, int Hue) NameFor(string email, string? shownName)
    {
        var name = string.IsNullOrWhiteSpace(shownName) ? NameFromEmail(email) : shownName.Trim();
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].TrimEnd('.') is "Mr" or "Mrs" or "Ms" or "Dr" or "Mx") words.RemoveAt(0);
        var initials = words.Count == 0 ? "?" : (words[0][..1] + (words.Count > 1 ? words[^1][..1] : "")).ToUpperInvariant();
        // FNV-1a over the address: stable across runs and computers (string.GetHashCode is not).
        var hash = 2166136261u;
        foreach (var c in email.Trim().ToLowerInvariant()) hash = (hash ^ c) * 16777619u;
        return (name, initials, (int)(hash % PictureHues));
    }

    // "Alex Kim" for alex.kim@boscotech.edu (the engine's DisplayName does the same).
    public static string NameFromEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var local = at > 0 ? email[..at] : email;
        var words = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return name.Length == 0 ? email : name;
    }

    // Who may remove a student from this computer: themselves; a mentor in use; anyone while PINs
    // are off (with PINs off anyone can enter anyone anyway).
    public static bool CanRemove(bool self, bool mentorInUse, bool pinsRequired) => self || mentorInUse || !pinsRequired;

    // Turning "shared by several students" off keeps only the student in use: allowed with at
    // most one student, a mentor in use, or PINs off.
    public static bool CanTurnOff(int profiles, bool mentorInUse, bool pinsRequired) => profiles <= 1 || mentorInUse || !pinsRequired;

    // Only a mentor's own profile in use changes whether PINs are asked on this computer.
    public static bool CanChangePins(bool mentorInUse) => mentorInUse;

    // Folders compare as Windows does: case-insensitive, either slash, no trailing slash.
    public static bool SameFolder(string a, string b)
        => string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool SameEmail(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
