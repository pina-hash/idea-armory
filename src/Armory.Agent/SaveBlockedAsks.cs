using Armory.Agent.Engine.View;

namespace Armory.Agent;

// Unlike an open-file question, a blocked-save explanation is shown even with Home visible.
// The engine's burst key is stable across watcher churn and view refreshes. One Windows tag
// per file replaces the preceding burst instead of filling the notification center.
internal sealed class SaveBlockedAsks
{
    private readonly Dictionary<string, string> shown = new(StringComparer.Ordinal);
    private string? email;
    internal (IReadOnlyList<(string Tag, NoticeGroupView Card)> Show, IReadOnlyList<string> Withdraw) Update(AgentView view)
    {
        List<string> withdraw = [];
        if (!string.Equals(email, view.Account?.Email, StringComparison.OrdinalIgnoreCase))
        {
            withdraw.AddRange(shown.Keys);
            shown.Clear();
            email = view.Account?.Email;
        }
        var cards = view.Connection == Connections.SignedIn && email is not null ? view.Notices.Where(n => n.Kind == NoticeKinds.SaveBlocked).ToArray() : [];
        var tags = cards.ToDictionary(c => "b" + ToastXml.TagFor([email!, c.Items[0].Path]), StringComparer.Ordinal);
        foreach (var tag in shown.Keys.Except(tags.Keys).ToArray()) { withdraw.Add(tag); shown.Remove(tag); }
        List<(string, NoticeGroupView)> show = [];
        foreach (var (tag, card) in tags)
        {
            // The detector owns burst identity. A refreshed reason or newly available
            // checkout action updates Home, but must not notify again for the same burst.
            // The notification carries this key; its click rechecks the current action.
            if (shown.GetValueOrDefault(tag) == card.Key) continue;
            shown[tag] = card.Key;
            show.Add((tag, card));
        }
        return (show, withdraw);
    }
}
