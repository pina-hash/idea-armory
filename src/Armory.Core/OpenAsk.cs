namespace Armory.Core;

// C5 (docs/agent/ENGINE.md, "SolidWorks opened a file"): which question a document SolidWorks
// opened gets, if any. Nothing is ever checked out because it was opened; this only decides
// what Armory asks.
public enum OpenAskKind
{
    None,
    // Nobody has it: check it out (and make it editable right there).
    CheckOut,
    // This computer has it checked out, but SolidWorks opened it read-only: open it again.
    Reopen,
    // Someone else has it checked out: say who, and that it can't be saved here.
    HeldByOther,
    // The same person has it checked out on another computer.
    HeldOnMyOtherComputer,
}

public static class OpenAsk
{
    // ownership: who has the file checked out. openedReadOnly: SolidWorks opened it read-only
    // (without the SolidWorks link, read from whether this computer has it checked out).
    // topLevel: the student opened it in its own window, never a reference an assembly or a
    // drawing loaded. tracked: a SolidWorks file the team has (a server id, not removed).
    // A file nobody has that SolidWorks opened writable (its read-only bit cleared by hand) is
    // still not checked out, so it gets the same question as one opened read-only.
    public static OpenAskKind Decide(LockOwnership ownership, bool openedReadOnly, bool topLevel, bool tracked)
    {
        if (!topLevel || !tracked) return OpenAskKind.None;
        return ownership switch
        {
            LockOwnership.OtherPerson => OpenAskKind.HeldByOther,
            LockOwnership.MyOtherDevice => OpenAskKind.HeldOnMyOtherComputer,
            LockOwnership.ThisDevice => openedReadOnly ? OpenAskKind.Reopen : OpenAskKind.None,
            _ => OpenAskKind.CheckOut,
        };
    }
}

// One question per document per SolidWorks session (a SolidWorks process): closing and
// opening the same file again in that session asks only in the window, never a second time
// outside it. A session that ends (SolidWorks closed) forgets its documents. Paths compare
// without case, as Windows does.
public sealed class OpenAskOnce
{
    private readonly HashSet<(string Path, int Session)> asked = [];

    // True the first time this document is asked about in this session.
    public bool TryAsk(string path, int session) => asked.Add((path.ToUpperInvariant(), session));

    public bool WasAsked(string path, int session) => asked.Contains((path.ToUpperInvariant(), session));

    public void SessionEnded(int session) => asked.RemoveWhere(k => k.Session == session);

    public int Count => asked.Count;
}
