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

public static class CheckoutRules
{
    // A file the server has is read-only on disk unless THIS device holds its live lock, so
    // SolidWorks opens it read-only and cannot save over it. Files the server does not have
    // (not yet added, refused, drafts) are not subject to this rule and stay writable.
    public static bool IsReadOnlyOnDisk(LockOwnership ownership) => ownership != LockOwnership.ThisDevice;
}
