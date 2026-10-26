using System.Globalization;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Rules = Armory.Core.SharedComputer;

namespace Armory.Agent;

// Several people taking turns on one computer with one shared Windows login (0.3.3 part F; 0.5.0,
// ledger 0437; docs/agent/PROFILES.md). A computer becomes shared by itself the first time a second
// person signs in on it (BecomeSharedAsync), with no setting and no PIN to choose:
//
//   - each person has a profile with their own sign-in (ProfileClients over their own secrets);
//     the host swaps one pointer, clients, never the session inside a SessionManager (F2);
//   - the window opens on the faces of the people who use the computer, most recent first, when it
//     is shown from hidden, on the first open of the day and after Windows is locked, and only when
//     two or more people use it (F6); while it shows, Armory goes on working for the person in use
//     and the page gets none of their files (F7);
//   - one click on a face switches; a mentor may turn PINs on for a computer (F4, F5);
//   - one Armory folder for everyone: whoever is in use works in it, and what the last person left
//     waiting stays theirs, held in the folder for them by the engine (SyncEngine.People.cs);
//   - a switch lets the last person's engine send what it can, then stops it for good before the
//     next person's starts (F8). It changes who Armory acts for, never what is on disk: a document
//     SolidWorks has open stays open where it is.
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
        if (profiles.Upgraded)
        {
            // From 0.3.3 to 0.4.x: saved at once as version 2 (an older Armory then runs nobody from it).
            log.Info($"profiles: carried forward from an older Armory; PINs {(index.PinsRequired ? "stay on, a mentor turned them on" : "are off")}");
            try { profiles.Save(index); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Error("could not save profiles.json", error); }
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
        // The update's one sentence about folders folded in (AgentHost.Fold.cs), for a week.
        if (foldNote is { } note) view = view with { UpdateNote = note };
        if (profiles is null) return view;
        var shown = ProfilesNow(view);
        if (!shown.Showing) return view with { Profiles = shown };
        return view with
        {
            Account = null, Notices = [], Prompt = null, MyFiles = [], Projects = [], FolderOwner = null, HeldForOthers = [], UpdateNote = null,
            Activity = Unnamed(view.Activity), Profiles = shown,
        };
    }

    // What is moving, without a file's name in it.
    private static ActivityView Unnamed(ActivityView activity) => activity with { Active = [], Log = [] };

    private ProfilesView ProfilesNow(AgentView view)
    {
        Picker p;
        ProfileIndex ix;
        lock (profileGate) { p = picker; ix = index; }
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
            Rules.CanTurnOff(ix.Profiles.Count, mentor, ix.PinsRequired), null, rows, StepView(showing ? p : new Picker(false, PickerSteps.Choose), tooNew));
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

    // ---- The picker --------------------------------------------------------------------------

    // The window asks whether to show the picker (MainWindow, the tray, Windows locked).
    internal void ShowPicker(PickerTrigger why)
    {
        if (profiles is null) return;
        lock (profileGate)
        {
            if (!Rules.ShowPicker(why, index.LastPicked, Today, index.Profiles.Count)) return;
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

    // ---- Switching (decision F8) -------------------------------------------------------------

    private async Task<ActionResult> SwitchToAsync(ProfileRecord target)
    {
        await lifecycle.WaitAsync();
        try { return await SwitchLockedAsync(target); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("profiles: the switch to " + target.Email + " failed", error);
            SetPicker(new Picker(true, PickerSteps.Choose, Message: "Armory couldn't switch. Try again in a moment."));
            return new(false, "");
        }
        finally
        {
            lifecycle.Release();
            RaiseView();
        }
    }

    // One folder for everyone (0.5.0): the next person always goes straight into the computer's
    // Armory folder. Nothing is asked and nothing waits for anyone: the next person's engine hands
    // the folder over by itself on its first pass, and what the last person left waiting is held
    // for them there, read-only for the next one (SyncEngine.People.cs).
    private async Task<ActionResult> SwitchLockedAsync(ProfileRecord target)
    {
        var shared = Settings.VaultRoot;
        var running = Volatile.Read(ref runtime);
        ProfileRecord? from;
        lock (profileGate) from = index.Find(index.Current);
        log.Info($"profiles: switching to {target.Email} in {shared}");

        // The same person, already running there: the picker goes, nothing restarts.
        if (from?.Id == target.Id && running is not null && Rules.SameFolder(running.Root, shared))
        {
            Picked(target, running.Root);
            lock (profileGate) picker = new Picker(false, PickerSteps.Choose);
            return new(true, "");
        }
        lock (profileGate) picker = new Picker(true, PickerSteps.Switching, target.Id, FromName: from is null || running is null ? null : NameOf(from));
        RaiseView();
        // The last person's own engine sends what it can first, under their own sign-in.
        if (running is not null && from is not null && from.Id != target.Id) await FlushLockedAsync(from, running);
        return await MoveLockedAsync(from, target, shared);
    }

    // One pass of the last person's engine before it stops, at most FlushPatience long: their saves
    // go to Armory now under their own sign-in when they can. Whatever is left (offline, a check out,
    // a file SolidWorks has open) is held for them by the next person's engine. A pass cut short is
    // stopped between two steps, as every stop is.
    private async Task FlushLockedAsync(ProfileRecord from, VaultRuntime running)
    {
        using var patience = new CancellationTokenSource(parts.FlushPatience);
        try { await running.Engine.SyncOnceAsync(patience.Token); }
        catch (OperationCanceledException) { log.Info("profiles: " + from.Email + "'s last pass was cut short; what is left waits for them"); }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Error("profiles: " + from.Email + "'s last pass failed; what is left waits for them", error); }
    }

    // The last person stops (their engine for good, their heartbeat with a goodbye), what they leave
    // waiting is kept for their face on the picker, and the next person starts in the folder.
    private async Task<ActionResult> MoveLockedAsync(ProfileRecord? from, ProfileRecord target, string folder)
    {
        var running = Volatile.Read(ref runtime);
        if (running is not null && !await StopCurrentLockedAsync(from, running, folder)) return Parked(from);
        Picked(target, folder);
        clients = ClientsOf(target.Id);
        var started = await StartRuntimeAsync(folder);
        StartBeating();
        Telemetry.UseAccount(Api, Feedback, () => Sessions.Current?.Email);
        lock (profileGate) picker = new Picker(false, PickerSteps.Choose);
        log.Info($"profiles: {target.Email} in use, in {folder}");
        _ = LearnMentorAsync();
        return started ? new(true, "") : new(false, runtimeProblem ?? "Armory couldn't start for you. Try again in a moment.");
    }

    // Stops the person in use's runtime for good. False when its engine did not stop in time and
    // was parked, and the next person's folder is the one it holds.
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
        // A parked engine holds its folder: a person for another folder may go on.
        return stopped || !Rules.SameFolder(running.Root, nextFolder);
    }

    private ActionResult Parked(ProfileRecord? from)
    {
        var first = from is null ? "the last person" : FirstOf(NameOf(from));
        SetPicker(new Picker(true, PickerSteps.Choose, Message: $"Armory couldn't finish {first}'s last step. Restart Armory, then pick again."));
        return new(false, "");
    }

    private void RecordWaiting(ProfileRecord who, string folder, FolderWaiting waiting)
        => SaveIndex(ix => ix.Find(who.Id) is { } r ? ix.With(r with { Waiting = waiting.Any ? WaitingRecord.Of(folder, waiting, Now) : null }) : ix);

    // The person in use is this one, in this folder, as of now; what waited for them is theirs again.
    private void Picked(ProfileRecord target, string folder)
    {
        var now = Now;
        var today = Today;
        SaveIndex(ix => (ix.Find(target.Id) is { } r ? ix.With(r with { Folder = folder, LastUsedAt = now, Waiting = null }) : ix.With(target with { Folder = folder, LastUsedAt = now, Waiting = null }))
            with { Current = target.Id, LastPicked = today });
    }

    // ---- A computer becomes shared by itself (0.5.0) ------------------------------------------

    // Someone else signed in on a computer one person used (SharedComputer.BecomesShared): that
    // person (signed in here, or the one the folder was last used by) becomes the first profile,
    // with their sign-in moved into it (never copied), and the person who just signed in the
    // second, in use at once. No PIN is chosen; PINs stay off unless a mentor turns them on.
    internal async Task<ActionResult> BecomeSharedAsync(ArmorySession arriving)
    {
        await lifecycle.WaitAsync();
        try
        {
            var store = new ProfileStore(paths.ProfilesFolder, parts.SecretsIn, problem => log.Error(problem));
            store.Load();
            if (store.TooNew) return new(false, TooNewWords);
            var signedIn = Sessions.Current;
            var firstEmail = signedIn?.Email ?? OwnerOf(Settings.VaultRoot);
            var first = firstEmail is null ? null : new ProfileRecord(ProfileStore.NewId(), firstEmail, Rules.NameFor(firstEmail, null).Name, Now, Now, Settings.VaultRoot, null);
            var ix = ProfileIndex.Empty with { Current = null, Migrating = signedIn is null ? null : "on", LastPicked = Today, Profiles = first is null ? [] : [first] };
            try { store.Save(ix); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Error("could not start profiles.json", error);
                return new(false, "Armory couldn't add you on this computer. Try again.");
            }
            log.Info($"profiles: {arriving.Email} signed in where {firstEmail} works; this computer is shared now");
            // The first person's engine sends what it can, then stops: the switch below starts the next one's.
            var old = Volatile.Read(ref runtime);
            if (old is not null && first is not null) await FlushLockedAsync(first, old);
            await StopBeatingAsync();
            old = Interlocked.Exchange(ref runtime, null);
            if (old is not null) await StopRuntimeAsync(old, parts.StopPatience);
            SaveSettingsValue(settings with { SharedComputer = true });
            clients = Nobody();
            lock (profileGate)
            {
                profiles = store;
                index = ix;
                byProfile.Clear();
                hasPin.Clear();
                picker = new Picker(true, PickerSteps.Switching);
            }
            if (signedIn is not null && first is not null)
            {
                if (!MoveSecret(paths.SecretsFolder, store.SecretsFolderOf(first.Id)) || ClientsOf(first.Id).Sessions.Current?.Email is not { } moved || !Rules.SameEmail(moved, first.Email))
                    log.Error("profiles: the first person's sign-in could not be moved; they sign in again when they pick their name");
            }
            SaveIndex(i => i with { Migrating = null });
            var record = new ProfileRecord(ProfileStore.NewId(), arriving.Email, Rules.NameFor(arriving.Email, null).Name, Now, null, Settings.VaultRoot, null);
            ClientsOf(record.Id).Sessions.SignIn(arriving);
            SaveIndex(i => i.With(record));
            return await SwitchLockedAsync(record);
        }
        finally
        {
            lifecycle.Release();
            RaiseView();
        }
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
                lock (profileGate) picker = new Picker(true, PickerSteps.Choose);
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
