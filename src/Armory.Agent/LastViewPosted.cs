using Armory.Agent.Engine.View;

namespace Armory.Agent;

// The window posts a view only when it differs from the last view its page got (0.3.3, N7).
// Picking a theme raised up to three identical views (the host's save, the bridge's own answer
// and the engine's republish), and the page drew all of Home for each. The page skips one it
// already has as well (app.js); this saves sending it at all. Every other message always goes.
internal sealed class LastViewPosted
{
    private static readonly string ViewPrefix = "{\"type\":\"" + BridgeMessages.View + "\",";
    private readonly object gate = new();
    private string? last;

    // True when json should be posted: it isn't a view, or it is one the page doesn't have yet
    // (then it is remembered as the page's).
    internal bool Take(string json)
    {
        if (!IsView(json)) return true;
        lock (gate)
        {
            if (string.Equals(last, json, StringComparison.Ordinal)) return false;
            last = json;
            return true;
        }
    }

    // The page loads again, or says it is ready: it has no view, so its next one goes whatever it holds.
    internal void Forget()
    {
        lock (gate) last = null;
    }

    internal static bool IsView(string json) => json.StartsWith(ViewPrefix, StringComparison.Ordinal);
}
