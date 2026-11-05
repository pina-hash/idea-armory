using Armory.Agent.Engine.View;

namespace Armory.Agent;

// 0.5.7 (idea-app ledger 0521): when a mentor, the folder's owner or the team forces in a file
// this computer had checked out, its card on Home says so; this also says it once as a Windows
// notification, so a student who closed the window still hears it. One notification per card
// key (the engine keeps the key stable while the card stands); a card that goes is withdrawn.
internal sealed class TakenBackAsks
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
        var cards = view.Connection == Connections.SignedIn && email is not null
            ? view.Notices.Where(n => n.Kind == NoticeKinds.TakenBack && n.Items.Count > 0).ToArray()
            : [];
        var tags = cards.ToDictionary(c => "t" + ToastXml.TagFor([email!, c.Key]), StringComparer.Ordinal);
        foreach (var tag in shown.Keys.Except(tags.Keys).ToArray()) { withdraw.Add(tag); shown.Remove(tag); }
        List<(string, NoticeGroupView)> show = [];
        foreach (var (tag, card) in tags)
        {
            if (shown.GetValueOrDefault(tag) == card.Key + "#" + card.Count) continue;
            shown[tag] = card.Key + "#" + card.Count;
            show.Add((tag, card));
        }
        return (show, withdraw);
    }
}
