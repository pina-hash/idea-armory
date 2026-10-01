using System.Collections;

namespace Armory.TestSupport;

/// <summary>A thread-safe set of emails, compared after trimming and lowercasing.</summary>
public sealed class EmailSet : ICollection<string>, IReadOnlyCollection<string>
{
    private readonly object _gate = new();
    private readonly HashSet<string> _emails = new(StringComparer.Ordinal);

    public int Count { get { lock (_gate) return _emails.Count; } }
    bool ICollection<string>.IsReadOnly => false;

    public static string Normalize(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    public bool Add(string email) { var e = Normalize(email); lock (_gate) return _emails.Add(e); }
    void ICollection<string>.Add(string item) => Add(item);
    public bool Remove(string email) { var e = Normalize(email); lock (_gate) return _emails.Remove(e); }
    public bool Contains(string email) { var e = Normalize(email); lock (_gate) return _emails.Contains(e); }
    public void Clear() { lock (_gate) _emails.Clear(); }
    public void CopyTo(string[] array, int arrayIndex) { lock (_gate) _emails.CopyTo(array, arrayIndex); }

    /// <summary>The emails joined by commas, as the test is_admin() stub reads armory.test_admins.</summary>
    public override string ToString() => string.Join(',', Snapshot());

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)Snapshot()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private string[] Snapshot() { lock (_gate) return [.. _emails.Order(StringComparer.Ordinal)]; }
}
