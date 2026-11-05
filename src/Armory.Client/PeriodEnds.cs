using System.Text.Json;

namespace Armory.Client;

// When each class period ends today (idea-app ledger 0537): GET <site>/api/armory/bells, the
// website's own bell schedule and the school calendar's day letter, so the end-of-period
// reminder and the calendar never disagree. A day that is not a school day, whose times the
// website does not have, or that could not be read is no ends at all: the reminder stays quiet.
public static class PeriodEnds
{
    public const string Path = "api/armory/bells";

    // The ends in the answer, oldest first; empty for anything else.
    public static IReadOnlyList<DateTimeOffset> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (!root.TryGetProperty("schoolDay", out var school) || school.ValueKind != JsonValueKind.True) return [];
            if (!root.TryGetProperty("ends", out var ends) || ends.ValueKind != JsonValueKind.Array) return [];
            List<DateTimeOffset> list = [];
            foreach (var e in ends.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(end.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                    list.Add(at.ToUniversalTime());
            }
            list.Sort();
            return list;
        }
        catch (JsonException) { return []; }
    }

    // Today's ends from the website, or empty on any failure (never throws but for cancellation).
    public static async Task<IReadOnlyList<DateTimeOffset>> FetchAsync(HttpClient http, Uri site, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(site, Path), ct);
            if (!response.IsSuccessStatusCode) return [];
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return []; }
    }
}
