using System.Globalization;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Rules = Armory.Core.SharedComputer;

namespace Armory.Agent;

// Several students taking turns on one computer with one shared Windows login (0.3.3 part F,
// docs/agent/PROFILES.md). Off by default (settings.json "sharedComputer"): then nothing here is
// made or read, the host builds exactly the objects it always built, and every picker message is
// answered "This computer isn't set up for several students." On:
//
//   - each student has a profile with their own sign-in (ProfileClients over their own secrets);
//     the host swaps one pointer, clients, never the session inside a SessionManager (F2);
//   - the window opens on a picker ("Who's using Armory?") when it is shown from hidden, on the
//     first open of the day, after Windows is locked, and on Switch student (F6); while it shows,
//     Armory goes on working for the student in use and the page gets none of their files (F7);
//   - a 4-digit PIN guards each switch unless a mentor turned PINs off on this computer (F4, F5);
//   - one Armory folder for the computer, handed from student to student by the 0.3.2 rule; a
//     student whose work waits keeps it, and the next one may use a folder of their own (F9);
//   - a switch stops the last student's engine for good before the next one's starts (F8).
internal sealed partial class AgentHost
{
    internal const string NotShared = "This computer isn't set up for several students.";
    private const string TooNewWords = "This computer's students were set up by a newer Armory. Update Armory to use them.";
    private const string SwitchingWords = "Armory is switching students. Try again in a moment.";

    private enum SignInPurpose { None, Expired, Forgot, NoPin }

    // Where the picker is. Showing false: the window shows Home (or Connect) for the student in use.
    private sealed record Picker(bool Showing, string Kind, string? ProfileId = null, string? Message = null, int? TriesLeft = null,
        DateTimeOffset? WaitUntil = null, string? OwnFolder = null, string? OwnerName = null, string? OwnerWaiting = null, string? FromName = null,
        string? ConnectPhase = null, SignInPurpose Purpose = SignInPurpose.None);

    // A student who signed in to be added, kept only once they chose their PIN (or at once with
    // PINs off): until then Cancel leaves nothing behind.
    private sealed record PendingAdd(string Id, ProfileClients Clients, ProfileRecord? Record);

    private ProfileStore? profiles;
    private ProfileIndex index = ProfileIndex.Empty;
    private readonly object profileGate = new();
    private readonly Dictionary<string, ProfileClients> byProfile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> hasPin = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim pinGate = new(1, 1);
    // Hidden at a start: Armory runs as the student in use in the background, and the picker shows
    // when the window opens (MainWindow.Open), or at once while nobody is in use.
    private Picker picker = new(false, PickerSteps.Choose);
    private CancellationTokenSource? signingIn;
    private PendingAdd? pendingAdd;
    private string? homeNote;
    private ProfileClients? nobody;

    // On a computer shared by several students.
    internal bool SharedComputer => profiles is not null;

    // The window is on the picker: the page gets no student's files, and actions from outside the
    // window (Explorer's Armory items, the SolidWorks link) open the picker instead of acting.
    internal bool PickerShowing
    {
        get
        {
            if (profiles is null) return false;
            lock (profileGate) return picker.Showing || index.Current is null || profiles.TooNew;
        }
    }

    // The check-out question as the engine has it (the tray's balloon), the picker or not.
    internal PromptView? PromptNow
    {
        get
        {
            try { return Volatile.Read(ref runtime)?.Engine.View.Prompt; }
            catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        }
    }

    // The folder Armory runs in now: the student in use's on a shared computer, else settings'.
    internal string VaultFolder => Volatile.Read(ref runtime)?.Root ?? RuntimeFolder(Settings) ?? Settings.VaultRoot;

    private HostHttp HostClients => new(restHttp, siteHttp, storageHttp);

    // ---- Start -------------------------------------------------------------------------------

    // The constructor's clients: the one student's (secrets\, as before profiles), or the student
    // in use's on a shared computer (nobody's when nobody is in use yet).
    private ProfileClients OpenClients()
    {
        if (!settings.SharedComputer) return Watch(ProfileClients.Create(null, parts.SecretsIn(paths.SecretsFolder), HostClients, site, parts.Browser, Telemetry, log));
        profiles = new ProfileStore(paths.ProfilesFolder, parts.SecretsIn, problem => log.Error(problem));
        index = profiles.Load();
        if (index.Migrating == "off")
        {
            // A turn-off a crash left half done: finished from what is on disk.
            FinishTurningOff();
            return Watch(ProfileClients.Create(null, parts.SecretsIn(paths.SecretsFolder), HostClients, site, parts.Browser, Telemetry, log));
        }
        if (index.Migrating == "on") FinishTurningOn();
        var current = index.Find(index.Current);
        log.Info($"profiles: shared computer on, {index.Profiles.Count} {(index.Profiles.Count == 1 ? "student" : "students")}, current {current?.Email ?? "nobody"}" +
            (profiles.TooNew ? ", set up by a newer Armory" : ""));
        if (current is not null)
        {
            var owner = OwnerOf(Settings.VaultRoot);
            homeNote = FolderNote(current, current.Folder, Settings.VaultRoot, owner, movedBack: false);
        }
        return current is null || profiles.TooNew ? Nobody() : ClientsOf(current.Id);
    }

    // Shared mode: the uploader sends only the student in use's notes, through their clients.
    private void AttachedTelemetry()
    {
        if (profiles is null) return;
        Telemetry.UseAccount(Api, Feedback, () => Sessions.Current?.Email);
        _ = LearnMentorAsync();
    }

    private string? RuntimeFolder(AgentSettings target)
    {
        if (profiles is null) return target.VaultRoot;
        lock (profileGate) return profiles.TooNew ? null : index.Find(index.Current)?.Folder;
    }

    private string NoRuntimeLine() => profiles is not null && RuntimeFolder(Settings) is null ? "Pick who is using Armory." : "Armory is starting.";

    // Team status: only the student in use beats, with their own device.
    private void StartBeating()
    {
        if (profiles is not null && RuntimeFolder(Settings) is null) { beating = null; return; }
        beating = clients.StartBeating(running.Token);
    }

    // The student in use's heartbeat stops and says "offline-soon" with their own device.
    private async Task StopBeatingAsync()
    {
        var was = beating;
        beating = null;
        if (was is null) return;
        var theirs = clients;
        theirs.StopBeating();
        try { await was.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await theirs.Heartbeat.SayGoodbyeAsync(GoodbyeDeadline).ConfigureAwait(false);
    }

    private ProfileClients Watch(ProfileClients c)
    {
        c.Sessions.SignedOut += () => OnSignedOut(c);
        return c;
    }

    // The sign-in of the student in use ended (a refused refresh, or Sign out): on a shared
    // computer the picker shows, on that student's Sign in again.
    private void OnSignedOut(ProfileClients c)
    {
        if (!ReferenceEquals(c, clients)) return;
        log.Info("signed out");
        Wake();
        if (profiles is null || c.ProfileId is null) return;
        lock (profileGate) picker = new Picker(true, PickerSteps.SignInAgain, c.ProfileId, Purpose: SignInPurpose.Expired);
        RaiseView();
    }

    private ProfileClients ClientsOf(string id)
    {
        lock (profileGate)
        {
            if (byProfile.TryGetValue(id, out var known)) return known;
            var made = Watch(ProfileClients.Create(id, profiles!.SecretsOf(id), HostClients, site, parts.Browser, Telemetry, log));
            byProfile[id] = made;
            return made;
        }
    }

    // The clients while nobody is in use: no sign-in, nothing to send.
    private ProfileClients Nobody() => nobody ??= ProfileClients.Create(null, new InMemorySecretStore(), HostClients, site, parts.Browser, Telemetry, log);

    private bool HasPin(string id)
    {
        lock (profileGate) if (hasPin.TryGetValue(id, out var known)) return known;
        var has = profiles!.ReadPin(id) is not null;
        lock (profileGate) hasPin[id] = has;
        return has;
    }

    private DateTimeOffset Now => parts.Clock.GetUtcNow();
    private DateOnly Today => DateOnly.FromDateTime(parts.Clock.GetLocalNow().DateTime);
    private string? OwnerOf(string folder) => SyncEngine.OwnerOf(parts.StateOf(folder));
    private static string NameOf(ProfileRecord record) => Rules.NameFor(record.Email, record.Name).Name;
    private static string FirstOf(string name) => name.Split(' ')[0];

    private ProfileRecord? Find(string id)
    {
        lock (profileGate) return index.Find(id);
    }

    private void SaveIndex(Func<ProfileIndex, ProfileIndex> change)
    {
        ProfileIndex next;
        lock (profileGate) index = next = change(index);
        try { profiles?.Save(next); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Error("could not save profiles.json", error);
        }
    }

    private void SetPicker(Picker next)
    {
        lock (profileGate) picker = next;
        RaiseView();
    }

    private Picker CurrentPicker
    {
        get { lock (profileGate) return picker; }
    }

    // ---- The view ----------------------------------------------------------------------------

    // The host's overlay on every view: the students, and while the picker shows, nothing of the
    // student in use's files (decision F7).
    private AgentView WithProfiles(AgentView view)
    {
        if (profiles is null) return view;
        var shown = ProfilesNow(view);
        if (!shown.Showing) return view with { Profiles = shown };
        return view with
        {
            Account = null, Notices = [], Prompt = null, MyFiles = [], Projects = [], FolderOwner = null,
            Activity = Unnamed(view.Activity), Profiles = shown,
        };
    }

    // What is moving, without a file's name in it.
    private static ActivityView Unnamed(ActivityView activity) => activity with { Active = [], Log = [] };

    private ProfilesView ProfilesNow(AgentView view)
    {
        Picker p;
        ProfileIndex ix;
        string? note;
        lock (profileGate) { p = picker; ix = index; note = homeNote; }
        var mentor = MentorIn(view);
        var current = ix.Find(ix.Current);
        var tooNew = profiles!.TooNew;
        var showing = p.Showing || current is null || tooNew;
        var shared = Settings.VaultRoot;
        var rows = ix.Profiles.OrderByDescending(r => r.Id == ix.Current).ThenByDescending(r => r.LastUsedAt ?? r.AddedAt).Select(r =>
        {
            var (name, initials, hue) = Rules.NameFor(r.Email, r.Name);
            var waiting = r.Id != ix.Current && r.Waiting is { Total: > 0 } w ? w.Words : null;
            return new ProfileView(r.Id, name, r.Email, initials, hue, r.Id == ix.Current, Iso(r.LastUsedAt), r.Waiting is { Total: > 0 } at ? at.Folder : r.Folder,
                !Rules.SameFolder(r.Waiting is { Total: > 0 } it ? it.Folder : r.Folder, shared), waiting, ClientsOf(r.Id).Sessions.Current is null, HasPin(r.Id),
                Rules.CanRemove(r.Id == ix.Current, mentor, ix.PinsRequired));
        }).ToList();
        return new ProfilesView(showing, current?.Id, shared, ix.PinsRequired, Rules.CanChangePins(mentor), PinsNote(ix),
            Rules.CanTurnOff(ix.Profiles.Count, mentor, ix.PinsRequired), showing ? null : note, rows, StepView(showing ? p : new Picker(false, PickerSteps.Choose), tooNew));
    }

    private PickerStepView StepView(Picker p, bool tooNew)
    {
        if (tooNew) return new PickerStepView(PickerSteps.TooNew, null, TooNewWords, null, null, null, null, null, null, null);
        var now = Now;
        int? wait = p.WaitUntil is { } until && until > now ? (int)Math.Ceiling((until - now).TotalSeconds) : null;
        var message = p.Message;
        if (p.Kind == PickerSteps.SignInAgain && message is null && p.ProfileId is not null && Find(p.ProfileId) is { } who)
            message = p.Purpose switch
            {
                SignInPurpose.Forgot => $"Sign in as {who.Email} to choose a new PIN.",
                SignInPurpose.NoPin => "Sign in with Google once to choose your PIN.",
                _ => "Your sign-in on this computer ended. Sign in with your school Google account once more.",
            };
        return new PickerStepView(p.Kind, p.ProfileId, message, p.Kind == PickerSteps.Pin ? p.TriesLeft : null, p.Kind == PickerSteps.Pin ? wait : null,
            p.OwnFolder, p.OwnerName, p.OwnerWaiting, p.FromName, p.ConnectPhase);
    }

    // "Turned off by Mr. Pina on Oct 9."
    private static string? PinsNote(ProfileIndex ix)
    {
        if (ix.PinsChangedBy is not { } by || ix.PinsChangedAt is not { } at) return null;
        var name = ix.FindEmail(by) is { } r ? NameOf(r) : Rules.NameFromEmail(by);
        return $"Turned {(ix.PinsRequired ? "on" : "off")} by {name} on {at.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture)}.";
    }

    // The student in use is a mentor in one of their projects, as the server said (armory_my_projects'
    // role) for this profile: never the engine's view, whose folder may still hold the roles the
    // last student's engine read until this one reads them again.
    private readonly Dictionary<string, bool> mentors = new(StringComparer.Ordinal);

    private bool MentorIn(AgentView? _) => MentorKnown();

    private bool MentorKnown()
    {
        var id = clients.ProfileId;
        if (id is null) return false;
        lock (profileGate) return mentors.GetValueOrDefault(id);
    }

    // Asked of the server before a mentor's change; offline, what it said last.
    private async Task<bool> MentorInUseAsync()
    {
        var theirs = clients;
        if (theirs.ProfileId is not { } id || theirs.Sessions.Current is null) return false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var mentor = (await theirs.Api.MyProjectsAsync(deadline.Token)).Any(p => p.Role == MemberRole.Mentor);
            lock (profileGate) mentors[id] = mentor;
            return mentor;
        }
        catch (Exception error) when (error is ArmoryClientException or OperationCanceledException or HttpRequestException)
        {
            return MentorKnown();
        }
    }

    // After a switch: whether the student now in use is a mentor, for Settings.
    private async Task LearnMentorAsync()
    {
        var before = MentorKnown();
        if (await MentorInUseAsync() != before) RaiseView();
    }

    private static string? Iso(DateTimeOffset? at) => at?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // Home's one line about the folder the student in use is in.
    private static string? FolderNote(ProfileRecord who, string folder, string shared, string? owner, bool movedBack)
    {
        if (movedBack) return $"You're back in {shared}. Armory doesn't use {who.Folder} any more; you can delete it in File Explorer.";
        if (Rules.SameFolder(folder, shared)) return null;
        if (owner is not null && !Rules.SameEmail(owner, who.Email))
            return $"You're in your own folder, {folder}, while {FirstOf(Rules.NameFromEmail(owner))}'s work waits in {shared}.";
        return $"You're in your own folder, {folder}, until your work there is done. Then Armory moves you back to {shared}.";
    }

    // ---- The picker --------------------------------------------------------------------------

    // The window asks whether to show the picker (MainWindow, the tray, Windows locked).
    internal void ShowPicker(PickerTrigger why)
    {
        if (profiles is null) return;
        lock (profileGate)
        {
            if (!Rules.ShowPicker(why, index.LastPicked, Today)) return;
            // A browser sign-in or a switch under way keeps its step; anything else starts over at the tiles.
            if (picker.Showing && (picker.Kind == PickerSteps.Switching || (signingIn is not null && picker.Kind is PickerSteps.Adding or PickerSteps.SignInAgain))) return;
            picker = new Picker(true, PickerSteps.Choose);
        }
        log.Info("picker: shown (" + why + ")");
        RaiseView();
    }

    internal async Task<ActionResult> PickProfileAsync(string profileId)
    {
        if (profiles is null) return new(false, NotShared);
        if (profiles.TooNew) return new(false, TooNewWords);
        if (CurrentPicker.Kind == PickerSteps.Switching) return new(false, SwitchingWords);
        if (Find(profileId) is not { } target) return new(false, "That student isn't on this computer any more.");
        if (ClientsOf(profileId).Sessions.Current is null)
        {
            SetPicker(new Picker(true, PickerSteps.SignInAgain, profileId, Purpose: SignInPurpose.Expired));
            return new(true, "");
        }
        bool pins;
        lock (profileGate) pins = index.PinsRequired;
        if (pins)
        {
            // With PINs on, a student with no PIN yet signs in once first: otherwise the first
            // classmate to click their name would choose their PIN.
            if (profiles.ReadPin(profileId) is not { } pin)
            {
                SetPicker(new Picker(true, PickerSteps.SignInAgain, profileId, Purpose: SignInPurpose.NoPin));
                return new(true, "");
            }
            SetPicker(new Picker(true, PickerSteps.Pin, profileId, TriesLeft: Rules.PinTriesLeft(pin.WrongTries), WaitUntil: pin.WaitUntil > Now ? pin.WaitUntil : null,
                Message: pin.WaitUntil > Now ? WaitWords(pin.WaitUntil.Value - Now) : null));
            return new(true, "");
        }
        return await SwitchToAsync(target);
    }

    internal async Task<ActionResult> EnterPinAsync(string profileId, string pin)
    {
        if (profiles is null) return new(false, NotShared);
        var p = CurrentPicker;
        if (p.Kind != PickerSteps.Pin || p.ProfileId != profileId || Find(profileId) is not { } target) return new(false, "Pick your name first.");
        await pinGate.WaitAsync();
        PinCheck check;
        PinRecord next;
        try
        {
            if (profiles.ReadPin(profileId) is not { } record)
            {
                SetPicker(new Picker(true, PickerSteps.SignInAgain, profileId, Purpose: SignInPurpose.NoPin));
                return new(true, "");
            }
            var now = Now;
            (check, next) = await Task.Run(() => (PinHash.Check(record, pin, now, out var after), after));
            if (!ReferenceEquals(next, record)) profiles.WritePin(profileId, next);
        }
        finally { pinGate.Release(); }
        if (check == PinCheck.Right)
        {
            log.Info("picker: the right PIN for " + target.Email);
            return await SwitchToAsync(target);
        }
        var left = Rules.PinTriesLeft(next.WrongTries);
        var message = next.WaitUntil is { } until && until > Now ? WaitWords(until - Now)
            : left == 1 ? "That PIN isn't right. 1 more try, then a short wait."
            : $"That PIN isn't right. {left} more tries, then a short wait.";
        if (check == PinCheck.Wrong) log.Info($"picker: a wrong PIN for {target.Email} ({next.WrongTries} in a row)");
        SetPicker(new Picker(true, PickerSteps.Pin, profileId, message, left, next.WaitUntil > Now ? next.WaitUntil : null));
        return new(false, "");
    }

    private static string WaitWords(TimeSpan wait)
    {
        var words = wait.TotalSeconds <= 90 ? $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} seconds" : $"{(int)Math.Ceiling(wait.TotalMinutes)} minutes";
        return $"Too many wrong tries. Try again in {words}, or sign in with Google instead.";
    }

    internal async Task<ActionResult> SetPinAsync(string profileId, string pin)
    {
        if (profiles is null) return new(false, NotShared);
        var p = CurrentPicker;
        if (p.Kind != PickerSteps.NewPin || p.ProfileId != profileId) return new(false, "Sign in first, then choose your PIN.");
        if (Rules.TooEasy(pin) is { } why)
        {
            SetPicker(p with { Message = why });
            return new(false, "");
        }
        var record = await Task.Run(() => PinHash.Create(pin, parts.PinIterations));
        PendingAdd? adding;
        lock (profileGate) adding = pendingAdd?.Id == profileId ? pendingAdd : null;
        ProfileRecord target;
        if (adding is { Record: { } added })
        {
            // A new student: kept now, with their PIN.
            profiles.WritePin(profileId, record);
            Keep(added, adding.Clients);
            target = added;
        }
        else if (Find(profileId) is { } known)
        {
            profiles.WritePin(profileId, record);
            target = known;
        }
        else return new(false, "That student isn't on this computer any more.");
        lock (profileGate) hasPin[profileId] = true;
        log.Info("picker: " + target.Email + " chose a PIN");
        return await SwitchToAsync(target);
    }

    // Add a student: the normal browser sign-in, once, into a profile of their own. The step says
    // to click "Not you? Use another account" when the browser is still signed in as someone else.
    internal Task<ActionResult> AddProfileAsync()
    {
        if (profiles is null) return Task.FromResult(new ActionResult(false, NotShared));
        if (profiles.TooNew) return Task.FromResult(new ActionResult(false, TooNewWords));
        var id = ProfileStore.NewId();
        var made = ProfileClients.Create(id, profiles.SecretsOf(id), HostClients, site, parts.Browser, Telemetry, log);
        var cancel = new CancellationTokenSource();
        CancellationTokenSource? previous = null;
        PendingAdd? dropped = null;
        var busy = false;
        lock (profileGate)
        {
            // "Open the browser again" while a student is being added: that sign-in stops and a
            // new one starts. Any other sign-in under way, or a switch, is left to finish.
            if (picker.Kind == PickerSteps.Switching || (signingIn is not null && picker.Kind != PickerSteps.Adding)) busy = true;
            else
            {
                previous = signingIn;
                dropped = previous is null ? null : pendingAdd;
                signingIn = cancel;
                pendingAdd = new PendingAdd(id, made, null);
                picker = new Picker(true, PickerSteps.Adding, ConnectPhase: "waitingForBrowser");
            }
        }
        if (busy)
        {
            cancel.Dispose();
            profiles.Forget(id);
            return Task.FromResult(new ActionResult(true, ""));
        }
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (dropped is not null) _ = ForgetAddedAsync(dropped);
        RaiseView();
        log.Info(previous is null ? "picker: adding a student, waiting for the browser" : "picker: adding a student, the browser opened again");
        _ = Task.Run(() => AddInBrowserAsync(id, made, cancel));
        return Task.FromResult(new ActionResult(true, ""));
    }

    private async Task AddInBrowserAsync(string id, ProfileClients made, CancellationTokenSource cancel)
    {
        try
        {
            var session = await made.Connector.ConnectAsync(parts.MachineName, cancel.Token);
            // Stopped (Cancel, or Open the browser again) just as it finished: not kept.
            bool ours;
            lock (profileGate) ours = ReferenceEquals(signingIn, cancel) && pendingAdd?.Id == id;
            if (!ours)
            {
                await made.Sessions.SignOutSessionAsync();
                profiles!.Forget(id);
                log.Info("picker: a sign-in that was stopped finished anyway; not kept");
                return;
            }
            log.Info("picker: signed in as " + session.Email + " to add a student");
            ProfileRecord? existing;
            bool pins;
            lock (profileGate) { existing = index.FindEmail(session.Email); pins = index.PinsRequired; }
            if (existing is not null)
            {
                // Already on this computer: their profile takes the new sign-in, and the browser
                // sign-in is the proof to choose a new PIN (a forgotten one included).
                lock (profileGate) { if (pendingAdd?.Id == id) pendingAdd = null; signingIn = null; }
                Renew(existing, session);
                made.Sessions.SignOut();
                profiles!.Forget(id);
                if (pins) SetPicker(new Picker(true, PickerSteps.NewPin, existing.Id));
                else await SwitchToAsync(existing);
                return;
            }
            var record = new ProfileRecord(id, session.Email, Rules.NameFor(session.Email, null).Name, Now, null, Settings.VaultRoot, null);
            lock (profileGate) { pendingAdd = new PendingAdd(id, made, record); signingIn = null; }
            if (pins) SetPicker(new Picker(true, PickerSteps.NewPin, id));
            else
            {
                Keep(record, made);
                await SwitchToAsync(record);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { log.Info("picker: adding a student was canceled"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("picker: adding a student failed" + (error is ConnectException { Status: { } status } ? " (" + status + ")" : ""), error is ConnectException ? null : error);
            lock (profileGate) if (pendingAdd?.Id == id) pendingAdd = null;
            profiles!.Forget(id);
            SetPicker(new Picker(true, PickerSteps.Adding, Message: error is ConnectException ? error.Message : "That sign-in didn't finish. Check that you're online, then try again.",
                ConnectPhase: "failed"));
        }
        finally
        {
            lock (profileGate) if (ReferenceEquals(signingIn, cancel)) signingIn = null;
            cancel.Dispose();
        }
    }

    // A new student is kept: in the index, with their clients.
    private void Keep(ProfileRecord record, ProfileClients made)
    {
        lock (profileGate)
        {
            if (pendingAdd?.Id == record.Id) pendingAdd = null;
            byProfile[record.Id] = Watch(made);
        }
        SaveIndex(ix => ix.With(record));
        log.Info("profiles: added " + record.Email);
    }

    // A student signed in again in the browser: their profile takes the new sign-in, and the old
    // one ends on the server (best effort; it would simply go unused).
    private void Renew(ProfileRecord record, ArmorySession session)
    {
        var theirs = ClientsOf(record.Id);
        var old = theirs.Sessions.Current;
        theirs.Sessions.SignIn(session);
        if (old is not null && old.RefreshToken != session.RefreshToken) _ = EndSessionAsync(old);
    }

    private async Task EndSessionAsync(ArmorySession old)
    {
        try
        {
            var once = new SessionManager(restHttp, new InMemorySecretStore());
            once.SignIn(old);
            await once.SignOutSessionAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Error("could not end an old sign-in", error); }
    }

    // "Forgot your PIN?", "Sign in again", and "no PIN yet": a browser sign-in as that same
    // student, into a temporary store; only their own address is taken.
    internal Task<ActionResult> ForgotPinAsync(string profileId)
    {
        if (profiles is null) return Task.FromResult(new ActionResult(false, NotShared));
        if (Find(profileId) is not { } record) return Task.FromResult(new ActionResult(false, "That student isn't on this computer any more."));
        var temp = ProfileClients.Create(null, new InMemorySecretStore(), HostClients, site, parts.Browser, Telemetry, log);
        var cancel = new CancellationTokenSource();
        SignInPurpose purpose;
        CancellationTokenSource? previous;
        lock (profileGate)
        {
            // "Open the browser again" for the same student: that sign-in stops and a new one
            // starts. Any other sign-in under way, or a switch, is left to finish.
            var again = picker.Kind == PickerSteps.SignInAgain && picker.ProfileId == profileId;
            if (picker.Kind == PickerSteps.Switching || (signingIn is not null && !again)) { cancel.Dispose(); return Task.FromResult(new ActionResult(true, "")); }
            purpose = again ? picker.Purpose : SignInPurpose.Forgot;
            previous = signingIn;
            signingIn = cancel;
            picker = new Picker(true, PickerSteps.SignInAgain, profileId, ConnectPhase: "waitingForBrowser", Purpose: purpose);
        }
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        RaiseView();
        log.Info($"picker: {record.Email} signs in again ({purpose})");
        _ = Task.Run(() => SignInAgainAsync(record, temp, purpose, cancel));
        return Task.FromResult(new ActionResult(true, ""));
    }

    private async Task SignInAgainAsync(ProfileRecord record, ProfileClients temp, SignInPurpose purpose, CancellationTokenSource cancel)
    {
        try
        {
            var session = await temp.Connector.ConnectAsync(parts.MachineName, cancel.Token);
            bool ours;
            lock (profileGate)
            {
                ours = ReferenceEquals(signingIn, cancel);
                if (ours) signingIn = null;
            }
            if (!ours)
            {
                // Stopped (Cancel, or Open the browser again) just as it finished: not used.
                _ = temp.Sessions.SignOutSessionAsync();
                return;
            }
            if (!Rules.SameEmail(session.Email, record.Email))
            {
                // Someone else's account: not kept, and it changes nothing of this student's.
                _ = temp.Sessions.SignOutSessionAsync();
                var first = FirstOf(NameOf(record));
                SetPicker(new Picker(true, PickerSteps.SignInAgain, record.Id, ConnectPhase: "failed", Purpose: purpose,
                    Message: $"That was {session.Email}. To {(purpose == SignInPurpose.Forgot ? "change " + first + "'s PIN" : "continue as " + first)}, sign in as {record.Email}."));
                return;
            }
            Renew(record, session);
            // The browser sign-in is the proof: the wrong tries are forgotten.
            if (profiles!.ReadPin(record.Id) is { } pin && (pin.WrongTries > 0 || pin.WaitUntil is not null))
                profiles.WritePin(record.Id, pin with { WrongTries = 0, WaitUntil = null });
            bool pins;
            lock (profileGate) pins = index.PinsRequired;
            if (pins && (purpose is SignInPurpose.Forgot or SignInPurpose.NoPin || !HasPin(record.Id))) SetPicker(new Picker(true, PickerSteps.NewPin, record.Id));
            else await SwitchToAsync(record);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { log.Info("picker: the sign-in was canceled"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("picker: the sign-in failed", error is ConnectException ? null : error);
            SetPicker(new Picker(true, PickerSteps.SignInAgain, record.Id, ConnectPhase: "failed", Purpose: purpose,
                Message: error is ConnectException ? error.Message : "That sign-in didn't finish. Check that you're online, then try again."));
        }
        finally
        {
            lock (profileGate) if (ReferenceEquals(signingIn, cancel)) signingIn = null;
            cancel.Dispose();
        }
    }

    // Back to the tiles: a sign-in in the browser stops, and a student being added is not kept.
    internal void CancelPicker()
    {
        if (profiles is null) return;
        CancellationTokenSource? cancel;
        PendingAdd? dropped;
        lock (profileGate)
        {
            if (picker.Kind == PickerSteps.Switching) return;
            cancel = signingIn;
            signingIn = null;
            dropped = pendingAdd;
            pendingAdd = null;
            picker = new Picker(true, PickerSteps.Choose);
        }
        try { cancel?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (dropped is not null) _ = ForgetAddedAsync(dropped);
        RaiseView();
    }

    private async Task ForgetAddedAsync(PendingAdd dropped)
    {
        await dropped.Clients.Sessions.SignOutSessionAsync();
        profiles?.Forget(dropped.Id);
        log.Info("picker: a student being added was not kept");
    }

    // The shared folder holds another student's work: wait for them, or a folder of one's own.
    internal async Task<ActionResult> ChooseFolderAsync(string profileId, string choice)
    {
        if (profiles is null) return new(false, NotShared);
        var p = CurrentPicker;
        if (p.Kind != PickerSteps.FolderBusy || p.ProfileId != profileId || Find(profileId) is not { } target) return new(false, "Pick your name first.");
        if (choice == "wait")
        {
            SetPicker(new Picker(true, PickerSteps.Choose));
            return new(true, "");
        }
        if (p.OwnFolder is not { } own || !AgentSettings.TryNormalizeVaultRoot(own, out var folder, out var problem))
            return new(false, "Armory can't make a folder of your own next to this one. Ask your teacher.");
        _ = problem;
        return await SwitchToAsync(target with { Folder = folder! }, ownChosen: true);
    }

    // ---- Switching (decision F8) -------------------------------------------------------------

    private async Task<ActionResult> SwitchToAsync(ProfileRecord target, bool ownChosen = false)
    {
        await lifecycle.WaitAsync();
        try { return await SwitchLockedAsync(target, ownChosen); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("profiles: the switch to " + target.Email + " failed", error);
            SetPicker(new Picker(true, PickerSteps.Choose, Message: "Armory couldn't switch students. Try again in a moment."));
            return new(false, "");
        }
        finally
        {
            lifecycle.Release();
            RaiseView();
        }
    }

    private async Task<ActionResult> SwitchLockedAsync(ProfileRecord target, bool ownChosen)
    {
        var shared = Settings.VaultRoot;
        var running = Volatile.Read(ref runtime);
        ProfileRecord? from;
        lock (profileGate) from = index.Find(index.Current);
        var waitingThere = target.Waiting is { } w && Rules.SameFolder(w.Folder, target.Folder) ? w.Total : 0;
        var own = !Rules.SameFolder(target.Folder, shared);
        var plan = Rules.PlanFolder(new FolderFacts(target.Email, shared, OwnerOf(shared), running is null ? null : from?.Email, running?.Root,
            target.Folder, waitingThere, own && parts.OpenInSolidWorks(target.Folder)));
        // A folder of their own just chosen because the shared one is busy: straight there.
        if (ownChosen) plan = new FolderPlan(FolderStep.UseOwn, target.Folder);
        log.Info($"profiles: switching to {target.Email}: {plan.Step} {plan.Folder}");

        // The same student, already running there: the picker goes, nothing restarts.
        if (from?.Id == target.Id && running is not null && Rules.SameFolder(running.Root, plan.Folder))
        {
            Picked(target, running.Root);
            lock (profileGate) picker = new Picker(false, PickerSteps.Choose);
            return new(true, "");
        }
        lock (profileGate) picker = new Picker(true, PickerSteps.Switching, target.Id, FromName: from is null || running is null ? null : NameOf(from));
        RaiseView();

        if (plan.Step == FolderStep.UseOwn) return await MoveLockedAsync(from, target, plan.Folder, null, sealShared: ownChosen);

        // The shared folder. Whoever runs on it stops first; the student it belongs to is asked
        // first what waits (their own engine knows best), and nothing is handed over while it does.
        var theirs = ClientsOf(target.Id);
        var stoppedOwner = false;
        if (running is not null && Rules.SameFolder(running.Root, shared))
        {
            if (plan.Step == FolderStep.AskRunningOwner)
            {
                FolderWaiting asked;
                try { asked = await running.Engine.WaitingAsync(); }
                catch (Exception error) when (error is IOException or EngineStoppedException)
                {
                    log.Error("profiles: could not ask what waits in " + shared, error);
                    return await BusyLockedAsync(from, target, OwnerOf(shared) ?? "", "work Armory couldn't look through just now", plan);
                }
                if (from is not null) RecordWaiting(from, running.Root, asked);
                if (asked.Any) return await BusyLockedAsync(from, target, asked.Owner ?? OwnerOf(shared) ?? "", asked.Words, plan);
            }
            if (!await StopCurrentLockedAsync(from, running, shared)) return Parked(from);
            stoppedOwner = true;
        }

        // Whose the folder is now that nothing runs on it (a first pass may have bound it a moment
        // ago): nobody's or the student's own, they go straight in; someone else's, an engine for
        // the student looks first and takes it over only when nothing of the owner's waits
        // (SyncEngine.TakeOverFolderAsync, the 0.3.2 rule). Whoever runs elsewhere keeps running
        // until then.
        var owner = OwnerOf(shared);
        if (owner is null || Rules.SameEmail(owner, target.Email))
            return await MoveLockedAsync(from, target, shared, null, sealShared: false, movedBack: plan.MovesBack);
        VaultRuntime probe;
        try { probe = await CreateRuntimeAsync(shared, theirs); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Error("profiles: could not open " + shared + " to look at it", error);
            if (stoppedOwner) await RestartFormerLockedAsync(from, shared);
            return await BusyLockedAsync(from, target, owner, "work Armory couldn't look through just now", plan);
        }
        string? refused = null;
        try
        {
            var look = await probe.Engine.WaitingAsync();
            if (look.Any) refused = look.Words;
            else
            {
                var taken = await probe.Engine.TakeOverFolderAsync();
                if (!taken.Ok) refused = "work still waiting";
            }
        }
        catch (Exception error) when (error is IOException or EngineStoppedException)
        {
            log.Error("profiles: could not look at " + shared, error);
            refused = "work Armory couldn't look through just now";
        }
        if (refused is not null)
        {
            await probe.DisposeAsync(log, StopTimeout);
            // Their work is there: whoever was running on the folder goes on there.
            if (stoppedOwner) await RestartFormerLockedAsync(from, shared);
            return await BusyLockedAsync(from, target, owner, refused, plan);
        }
        log.Info("profiles: " + shared + " handed over to " + target.Email);
        return await MoveLockedAsync(from, target, shared, probe, sealShared: false, movedBack: plan.MovesBack);
    }

    // The shared folder can't be had: a student coming back from a folder of their own stays
    // there; anyone else is asked (wait, or a folder of their own).
    private async Task<ActionResult> BusyLockedAsync(ProfileRecord? from, ProfileRecord target, string owner, string words, FolderPlan plan)
    {
        if (plan.OwnFolderIfBusy is { } stay) return await MoveLockedAsync(from, target, stay, null, sealShared: true);
        var ownerName = Find(OwnerProfileId(owner) ?? "") is { } known ? NameOf(known) : Rules.NameFromEmail(owner);
        SetPicker(new Picker(true, PickerSteps.FolderBusy, target.Id, OwnFolder: OwnFolderFor(target), OwnerName: ownerName, OwnerWaiting: words));
        log.Info($"profiles: {Settings.VaultRoot} stays {owner}'s ({words}); {target.Email} is asked to wait or use a folder of their own");
        return new(true, "");
    }

    private string? OwnerProfileId(string owner)
    {
        lock (profileGate) return index.FindEmail(owner)?.Id;
    }

    // C:\IDEA\Armory-jordan: not another student's folder, nor a folder bound to someone else.
    private string OwnFolderFor(ProfileRecord target)
    {
        var shared = Settings.VaultRoot;
        List<string> taken;
        lock (profileGate) taken = index.Profiles.Where(p => p.Id != target.Id).Select(p => p.Folder).ToList();
        for (var i = 0; i < 20; i++)
        {
            var candidate = Rules.OwnFolder(shared, target.Email, taken);
            var owner = OwnerOf(candidate);
            if (owner is null || Rules.SameEmail(owner, target.Email)) return candidate;
            taken.Add(candidate);
        }
        return Rules.OwnFolder(shared, target.Email, taken);
    }

    // The last student stops (their engine for good, their heartbeat with a goodbye), what they
    // leave waiting is kept for their tile, and the next student starts in the folder. made: the
    // runtime that looked at the folder for them, started now.
    private async Task<ActionResult> MoveLockedAsync(ProfileRecord? from, ProfileRecord target, string folder, VaultRuntime? made, bool sealShared, bool movedBack = false)
    {
        var shared = Settings.VaultRoot;
        var running = Volatile.Read(ref runtime);
        if (running is not null && !await StopCurrentLockedAsync(from, running, folder))
        {
            if (made is not null) await made.DisposeAsync(log, StopTimeout);
            return Parked(from);
        }
        // The next student works in a folder of their own because the last one's work waits in the
        // shared one: the last one's check outs there are made read-only until they are back. Only
        // once their engine has stopped for good, or its next pass would make them writable again.
        if (sealShared) await SealSharedLockedAsync(shared);
        var owner = OwnerOf(shared);
        Picked(target with { Folder = folder }, folder);
        clients = ClientsOf(target.Id);
        lock (profileGate) homeNote = FolderNote(target, folder, shared, owner, movedBack && Rules.SameFolder(folder, shared));
        var started = await StartRuntimeAsync(folder, made);
        StartBeating();
        Telemetry.UseAccount(Api, Feedback, () => Sessions.Current?.Email);
        lock (profileGate) picker = new Picker(false, PickerSteps.Choose);
        log.Info($"profiles: {target.Email} in use, in {folder}");
        _ = LearnMentorAsync();
        return started ? new(true, "") : new(false, runtimeProblem ?? "Armory couldn't start for you. Try again in a moment.");
    }

    // Stops the student in use's runtime for good. False when its engine did not stop in time and
    // was parked, and the next student's folder is the one it holds.
    private async Task<bool> StopCurrentLockedAsync(ProfileRecord? from, VaultRuntime running, string nextFolder)
    {
        if (from is not null)
        {
            try { RecordWaiting(from, running.Root, await running.Engine.WaitingAsync()); }
            catch (Exception error) when (error is IOException or EngineStoppedException) { log.Error("profiles: could not say what waits for " + from.Email, error); }
        }
        await StopBeatingAsync();
        Interlocked.CompareExchange(ref runtime, null, running);
        var stopping = StopRuntimeAsync(running, parts.StopPatience);
        if (await Task.WhenAny(stopping, Task.Delay(parts.StillFinishingAfter)) != stopping && from is not null)
            SetPicker(CurrentPicker with { Message = $"Still finishing {FirstOf(NameOf(from))}'s last file..." });
        var stopped = await stopping;
        if (!stopped) log.Error("profiles: the engine for " + (from?.Email ?? "nobody") + " did not stop; " + running.Root + " stays closed until it does");
        // A parked engine holds its folder: a student for another folder may go on.
        return stopped || !Rules.SameFolder(running.Root, nextFolder);
    }

    private ActionResult Parked(ProfileRecord? from)
    {
        var first = from is null ? "the last student" : FirstOf(NameOf(from));
        SetPicker(new Picker(true, PickerSteps.Choose, Message: $"Armory couldn't finish {first}'s last step. Restart Armory, then pick again."));
        return new(false, "");
    }

    // The student who was running on the shared folder goes on there (a look after them found
    // new work of theirs on the disk).
    private async Task RestartFormerLockedAsync(ProfileRecord? from, string shared)
    {
        if (from is null) return;
        await StartRuntimeAsync(shared);
        StartBeating();
    }

    // Nobody runs on the shared folder now: an engine that is never started makes the check outs
    // there read-only (the read-only rule as the next student sees it).
    private async Task SealSharedLockedAsync(string shared)
    {
        try
        {
            var probe = await CreateRuntimeAsync(shared, clients);
            int made;
            try { made = await probe.Engine.SealCheckOutsAsync(); }
            finally { await probe.DisposeAsync(log, StopTimeout); }
            log.Info($"profiles: {made} files in {shared} made read-only while their student is away");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EngineStoppedException)
        {
            log.Error("profiles: could not make the last student's check outs read-only", error);
        }
    }

    private void RecordWaiting(ProfileRecord who, string folder, FolderWaiting waiting)
        => SaveIndex(ix => ix.Find(who.Id) is { } r ? ix.With(r with { Waiting = waiting.Any ? WaitingRecord.Of(folder, waiting, Now) : null }) : ix);

    // The student in use is this one, in this folder, as of now.
    private void Picked(ProfileRecord target, string folder)
    {
        var now = Now;
        var today = Today;
        SaveIndex(ix => (ix.Find(target.Id) is { } r ? ix.With(r with { Folder = folder, LastUsedAt = now }) : ix.With(target with { Folder = folder, LastUsedAt = now }))
            with { Current = target.Id, LastPicked = today });
    }

    // ---- Remove, PINs, shared mode on and off ------------------------------------------------

    // Forgets a student's sign-in and PIN on this computer (best-effort server sign-out); never a
    // file. Their work in a folder stays theirs until they add themselves here again.
    internal async Task<ActionResult> RemoveProfileAsync(string profileId)
    {
        if (profiles is null) return new(false, NotShared);
        if (Find(profileId) is not { } record) return new(false, "That student isn't on this computer any more.");
        bool self, pins;
        lock (profileGate) { self = index.Current == profileId; pins = index.PinsRequired; }
        var mentor = !self && pins && await MentorInUseAsync();
        if (!Rules.CanRemove(self, mentor, pins)) return new(false, "Only a mentor can remove another student from this computer.");
        if (self)
        {
            await lifecycle.WaitAsync();
            try
            {
                var running = Volatile.Read(ref runtime);
                if (running is not null) await StopCurrentLockedAsync(record, running, "");
                clients = Nobody();
                SaveIndex(ix => ix with { Current = null });
                Telemetry.UseAccount(Api, Feedback, () => Sessions.Current?.Email);
                lock (profileGate) { picker = new Picker(true, PickerSteps.Choose); homeNote = null; }
            }
            finally { lifecycle.Release(); }
        }
        var theirs = ClientsOf(profileId);
        await theirs.Sessions.SignOutSessionAsync();
        lock (profileGate) { byProfile.Remove(profileId); hasPin.Remove(profileId); }
        SaveIndex(ix => ix.Without(profileId));
        profiles.Forget(profileId);
        log.Info("profiles: removed " + record.Email + "; no file was deleted");
        RaiseView();
        return new(true, $"{NameOf(record)} was removed from this computer. No files were deleted.");
    }

    // Settings > Shared computer > "Ask for a PIN when switching students": per computer, only by
    // a mentor's own profile in use, kept with who and when (decision F5).
    internal async Task<ActionResult> SetPinsRequiredAsync(bool on)
    {
        if (profiles is null) return new(false, NotShared);
        if (!Rules.CanChangePins(await MentorInUseAsync())) return new(false, "Only a mentor can change this.");
        var who = Sessions.Current?.Email;
        var now = Now;
        SaveIndex(ix => ix with { PinsRequired = on, PinsChangedBy = who, PinsChangedAt = now });
        log.Info($"profiles: PINs turned {(on ? "on" : "off")} by {who}");
        RaiseView();
        return new(true, on ? "Each student types their PIN when they switch on this computer." : "PINs are off on this computer. Picking a name switches at once.");
    }

    // "This computer is shared by several students" (decision F12).
    internal async Task<ActionResult> SetSharedComputerAsync(bool on, string? pin)
    {
        if (on == SharedComputer) return new(true, on ? "This computer is already shared by several students." : "This computer is used by one student.");
        if (on)
        {
            var session = Sessions.Current;
            if (session is not null && !Rules.IsPin(pin)) return new(false, "Choose a 4-digit PIN for yourself first.");
            if (session is not null && Rules.TooEasy(pin!) is { } why) return new(false, why);
        }
        else
        {
            int count;
            bool pins;
            lock (profileGate) { count = index.Profiles.Count; pins = index.PinsRequired; }
            if (!Rules.CanTurnOff(count, count <= 1 || !pins || await MentorInUseAsync(), pins)) return new(false, "Only a mentor can turn this off while other students use this computer.");
        }
        await lifecycle.WaitAsync();
        try { return on ? await TurnOnLockedAsync(pin) : await TurnOffLockedAsync(); }
        finally
        {
            lifecycle.Release();
            RaiseView();
        }
    }

    // On: the student signed in now becomes the first profile. Their sign-in MOVES into it, never
    // copied: a refresh token used by two SessionManagers would be rotated twice, and Supabase ends
    // the whole session when a used one comes back.
    private async Task<ActionResult> TurnOnLockedAsync(string? pin)
    {
        var store = new ProfileStore(paths.ProfilesFolder, parts.SecretsIn, problem => log.Error(problem));
        store.Load();
        if (store.TooNew) return new(false, TooNewWords);
        var session = Sessions.Current;
        var record = session is null ? null : new ProfileRecord(ProfileStore.NewId(), session.Email, Rules.NameFor(session.Email, null).Name, Now, Now, Settings.VaultRoot, null);
        var ix = ProfileIndex.Empty with { Current = record?.Id, Migrating = record is null ? null : "on", LastPicked = Today, Profiles = record is null ? [] : [record] };
        try { store.Save(ix); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Error("could not start profiles.json", error);
            return new(false, "Armory couldn't set this computer up for several students. Try again.");
        }
        SaveSettingsValue(settings with { SharedComputer = true });
        // The one student's runtime and heartbeat stop; nothing of theirs uses the old sign-in again.
        await StopBeatingAsync();
        var old = Interlocked.Exchange(ref runtime, null);
        if (old is not null) await StopRuntimeAsync(old, parts.StopPatience);
        var single = clients;
        clients = Nobody();
        Telemetry.UseAccount(Api, Feedback, () => Sessions.Current?.Email);
        lock (profileGate)
        {
            profiles = store;
            index = ix;
            byProfile.Clear();
            hasPin.Clear();
            picker = new Picker(record is null, PickerSteps.Choose);
            homeNote = null;
        }
        if (record is not null)
        {
            if (!MoveSecret(paths.SecretsFolder, store.SecretsFolderOf(record.Id)) || ClientsOf(record.Id).Sessions.Current?.Email is not { } moved || !Rules.SameEmail(moved, record.Email))
            {
                // Put back as it was: one student, their sign-in where it was.
                log.Error("profiles: the sign-in could not be moved into the first profile; shared mode stays off");
                MoveSecret(store.SecretsFolderOf(record.Id), paths.SecretsFolder);
                store.ForgetAll();
                lock (profileGate) { profiles = null; index = ProfileIndex.Empty; byProfile.Clear(); }
                SaveSettingsValue(settings with { SharedComputer = false });
                clients = single;
                await StartRuntimeAsync(Settings.VaultRoot);
                StartBeating();
                Telemetry.UseAccount(Api, Feedback, null);
                return new(false, "Armory couldn't move your sign-in. Nothing changed. Try again.");
            }
            store.WritePin(record.Id, await Task.Run(() => PinHash.Create(pin!, parts.PinIterations)));
            lock (profileGate) hasPin[record.Id] = true;
            SaveIndex(i => i with { Migrating = null });
            clients = ClientsOf(record.Id);
            await StartRuntimeAsync(record.Folder);
            StartBeating();
            Telemetry.UseAccount(Api, Feedback, () => Sessions.Current?.Email);
            _ = LearnMentorAsync();
        }
        log.Info("profiles: shared computer turned on" + (record is null ? ", nobody signed in" : ", " + record.Email + " is the first student"));
        return new(true, record is null ? "This computer is shared now. Each student adds themselves with Add a student."
            : "This computer is shared now. Other students add themselves with Add a student.");
    }

    // Off: only the student in use stays, signed in as before, in their folder; everyone else's
    // sign-in and PIN are forgotten here (best-effort server sign-out). No file is touched.
    private async Task<ActionResult> TurnOffLockedAsync()
    {
        SaveIndex(ix => ix with { Migrating = "off" });
        await StopBeatingAsync();
        var old = Interlocked.Exchange(ref runtime, null);
        if (old is not null) await StopRuntimeAsync(old, parts.StopPatience);
        ProfileRecord? keep;
        List<ProfileRecord> others;
        lock (profileGate)
        {
            keep = index.Find(index.Current);
            others = index.Profiles.Where(p => p.Id != keep?.Id).ToList();
        }
        clients = Nobody();
        Telemetry.UseAccount(Api, Feedback, null);
        foreach (var other in others) await ClientsOf(other.Id).Sessions.SignOutSessionAsync();
        FinishTurningOff();
        clients = Watch(ProfileClients.Create(null, parts.SecretsIn(paths.SecretsFolder), HostClients, site, parts.Browser, Telemetry, log));
        await StartRuntimeAsync(Settings.VaultRoot);
        StartBeating();
        Telemetry.UseAccount(Api, Feedback, null);
        log.Info("profiles: shared computer turned off" + (keep is null ? "" : ", " + keep.Email + " stays"));
        return new(true, keep is null ? "This computer is used by one student now. Connect it to start."
            : $"This computer is used by one student now: {NameOf(keep)}. Nothing in any Armory folder changed.");
    }

    // The rest of a turn-off, also after a crash in the middle of one: the student in use's
    // sign-in moves back to secrets\, settings point at their folder, profiles\ goes.
    private void FinishTurningOff()
    {
        var store = profiles!;
        ProfileRecord? keep;
        lock (profileGate) keep = index.Find(index.Current);
        if (keep is not null) MoveSecret(store.SecretsFolderOf(keep.Id), paths.SecretsFolder);
        store.ForgetAll();
        SaveSettingsValue(settings with { SharedComputer = false, VaultRoot = keep?.Folder ?? settings.VaultRoot });
        lock (profileGate)
        {
            profiles = null;
            index = ProfileIndex.Empty;
            byProfile.Clear();
            hasPin.Clear();
            pendingAdd = null;
            homeNote = null;
        }
    }

    // A turn-on a crash left half done: the sign-in is in secrets\ or in the profile, never both.
    private void FinishTurningOn()
    {
        if (index.Find(index.Current) is { } first && !File.Exists(ProfileStore.SecretFile(profiles!.SecretsFolderOf(first.Id), ProfileStore.SessionSecret)))
            MoveSecret(paths.SecretsFolder, profiles.SecretsFolderOf(first.Id));
        SaveIndex(ix => ix with { Migrating = null });
        log.Info("profiles: a turn-on left half done was finished");
    }

    // One rename on the same volume: the secret is in one place or the other, never both.
    private bool MoveSecret(string fromFolder, string toFolder)
    {
        var from = ProfileStore.SecretFile(fromFolder, ProfileStore.SessionSecret);
        try
        {
            if (!File.Exists(from)) return false;
            Directory.CreateDirectory(toFolder);
            File.Move(from, ProfileStore.SecretFile(toFolder, ProfileStore.SessionSecret), overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Error("could not move a sign-in", error);
            return false;
        }
    }

    private void SaveSettingsValue(AgentSettings next)
    {
        lock (gate) settings = next;
        try { settingsStore.Save(next); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Error("could not save settings", error); }
    }
}
