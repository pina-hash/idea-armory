namespace Armory.Core;

// Automatic is v1: a local change takes the lock and shares itself. Explicit is v2 (PDM style):
// only a check out takes the lock on a shared file, saves made while checked out are kept as
// side versions, and the shared version advances only at check in (or when a file is added).
public enum CheckoutMode { Automatic, Explicit }
// What the student asked for on a file this device has checked out. Persisted by the adapter
// so a crash finishes it; ignored in Automatic mode.
public enum CheckoutRequest { None, CheckIn, Undo }
// Why a side version was kept. Set on every SaveSideVersion in Explicit mode, never in Automatic.
public enum SideVersionReason { Conflict, LockBroken, SavedWhileCheckedOut, ChangedWithoutCheckOut, UndoCheckOut }
// What a check out of one file needs before this device may take its lock.
public enum CheckOutStep
{
    // The copy on disk is the live shared version, unchanged: take the lock now.
    TakeLock,
    // The copy is missing or behind, and closed: bring it up to date first (D18), then decide again.
    DownloadFirst,
    // The copy has bytes saved without a check out: keep them as a kept copy and put the shared
    // version back first (one pass with the lock free does both), then decide again.
    KeepChangesFirst,
    // The copy is missing or behind, and open: it cannot be brought up to date until it is closed.
    CloseFirst,
    // The copy is missing because it was removed on this computer; the removal is still pending.
    RemovedHere,
    // The server has no live version (never added, or removed): there is nothing to check out.
    NotShared,
}
// What a check in (or an undo, or an add's automatic check in) of one file needs before this
// device may let its lock go.
public enum CheckInStep
{
    // The bytes read just now are the shared version: let the lock go.
    LetGo,
    // The bytes read just now are not the shared version: a pass shares them first (a check in
    // commits them; an undo keeps them as a kept copy and puts the shared version back), then
    // decide again.
    CommitFirst,
    // The file is open (SolidWorks has it): it can still be saved, so the lock stays and the
    // file stays writable until it is closed. The first pass after it closes decides again.
    WaitForClose,
    // The file could not be read just now: what is on disk is unknown, so the lock stays until
    // a later pass can read it.
    ReadAgain,
}

public static class CheckoutRules
{
    // A file the server has is read-only on disk unless THIS device holds its live lock, so
    // SolidWorks opens it read-only and cannot save over it. Files the server does not have
    // (not yet added, refused, drafts) are not subject to this rule and stay writable.
    public static bool IsReadOnlyOnDisk(LockOwnership ownership) => ownership != LockOwnership.ThisDevice;

    // The check out rule. The lock is taken only over a copy that IS the live shared version:
    // a check in shares whatever bytes are on disk, so taking the lock over bytes saved without
    // a check out (the read-only attribute was cleared) would let them become the shared
    // version. The adapter hashes the file at check-out time, never trusting an older scan, and
    // passes the copy's base, that hash, the live remote revision and whether the file is open.
    public static CheckOutStep NextCheckOutStep(Revision? baseRevision, string? localHash, Revision? remote, bool isOpen)
    {
        if (remote is not { IsTombstone: false }) return CheckOutStep.NotShared;
        if (localHash is not null && localHash != baseRevision?.Hash) return CheckOutStep.KeepChangesFirst; // MUTATION: check out over unshared bytes
        if (Reconciler.SameRevision(baseRevision, remote)) return localHash is null ? CheckOutStep.RemovedHere : CheckOutStep.TakeLock;
        return isOpen ? CheckOutStep.CloseFirst : CheckOutStep.DownloadFirst;
    }

    // The check in rule (feedback N4: a part SolidWorks held open for writing was "checked in"
    // with nothing uploaded, and its saves were later put aside as a kept copy). The lock is let
    // go only over bytes read at that moment that are the shared version, and never while the
    // file is open: SolidWorks keeps saving through the handle it opened while the file was
    // writable, whatever the read-only bit says afterwards. The adapter makes the file read-only
    // first, then reads it, and passes the shared version's hash (baseHash), the hash it has for
    // the file (localHash, null when the file is not on disk), whether that hash was read from
    // the disk just now (read), and whether the file is open.
    public static CheckInStep NextCheckInStep(string? baseHash, string? localHash, bool read, bool isOpen)
    {
        if (isOpen) return CheckInStep.WaitForClose; // MUTATION: let go while open
        if (!read) return CheckInStep.ReadAgain; // MUTATION: let go over bytes not read
        return localHash == baseHash ? CheckInStep.LetGo : CheckInStep.CommitFirst;
    }
}
