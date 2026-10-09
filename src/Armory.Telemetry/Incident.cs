using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Telemetry;

// Who and what wrote an incident. MachineId tells apart computers that share a DeviceName (a
// short hash of Windows' own install id; null where there is none).
public sealed record IncidentHeader(string AppVersion, string OsVersion, string? DeviceName, string? Email, string? MachineId = null);

// The words a person typed in "Report a problem", kept with their incident until the site has them.
public sealed record IncidentFeedback(string Kind, string Body);

// Takes every secret out of the text an incident keeps: the exact strings this computer knows
// are secret (its tokens and keys), then the patterns (the agent's Redactor: JWTs, Bearer
// values, token parameters, long random runs). Applied to every string in the document. With
// ownEmail, every email address but the signed-in person's own is masked too: a site admin reads
// these reports, and other people's addresses are not the app's to send (ARMORY.md v0.3, item 4).
public sealed partial class Scrubber(Func<string, string>? patterns = null, Func<IEnumerable<string?>>? secrets = null, Func<string?>? ownEmail = null)
{
    public const string Mask = "[redacted]", AddressMask = "[address]";
    public static Scrubber None { get; } = new();

    public string Scrub(string text)
    {
        if (text.Length == 0) return text;
        if (secrets is not null)
            foreach (var secret in secrets())
                if (secret is { Length: >= 8 } && text.Contains(secret, StringComparison.Ordinal)) text = text.Replace(secret, Mask, StringComparison.Ordinal);
        if (ownEmail is not null && text.Contains('@'))
        {
            var own = ownEmail();
            text = Address().Replace(text, m => own is not null && string.Equals(m.Value, own, StringComparison.OrdinalIgnoreCase) ? m.Value : AddressMask);
        }
        return patterns is null ? text : patterns(text);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial System.Text.RegularExpressions.Regex Address();

    // Every string value in the tree, scrubbed in place.
    public void ScrubTree(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToArray())
                {
                    var child = o[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s)) o[key] = Scrub(s);
                    else ScrubTree(child);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var child = a[i];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s)) a[i] = Scrub(s);
                    else ScrubTree(child);
                }
                break;
        }
    }
}

// One incident document (docs/agent/TELEMETRY.md, "The incident file"), and its file form:
// gzip-compressed JSON under MaximumFileBytes, trimmed oldest first when it would be larger.
public static class IncidentDocument
{
    public const int SchemaVersion = 1;
    public const int MaximumFileBytes = 200 * 1024;
    public const int LogLines = 300;
    // A note sent on its own from "Send feedback" is kept in the same folder as an incident, of
    // this kind, with noteOnly true: the uploader sends its words and nothing after them.
    public const string NoteKind = "note", NoteOnlyField = "noteOnly";

    public static JsonObject Build(Glitch glitch, IncidentHeader header, DateTimeOffset createdAt, JsonObject? trigger, JsonArray events,
        long recorded, int capacity, JsonNode? snapshot, IReadOnlyList<string> log, IncidentFeedback? feedback = null, Guid? projectId = null)
    {
        var lines = new JsonArray();
        foreach (var line in log.Skip(Math.Max(0, log.Count - LogLines))) lines.Add(line);
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["id"] = Guid.NewGuid().ToString(),
            ["createdAt"] = FlightJson.Time(createdAt),
            ["kind"] = glitch.Kind,
            ["summary"] = GlitchRules.Clip(glitch.Summary),
            ["appVersion"] = header.AppVersion,
            ["osVersion"] = header.OsVersion,
            ["deviceName"] = header.DeviceName,
            ["machineId"] = header.MachineId,
            ["email"] = header.Email,
            ["projectId"] = projectId?.ToString(),
            ["feedback"] = feedback is null ? null : new JsonObject { ["kind"] = feedback.Kind, ["body"] = feedback.Body },
            ["feedbackId"] = null,
            ["trigger"] = trigger,
            ["flight"] = new JsonObject
            {
                ["capacity"] = capacity,
                ["recorded"] = recorded,
                ["trimmed"] = 0L,
                ["events"] = events,
            },
            ["snapshot"] = snapshot,
            ["log"] = lines,
        };
    }

    // The scrubbed, compressed file: every string scrubbed first, then trimmed (oldest events,
    // then oldest log lines, then stacks) until it fits.
    public static byte[] Render(JsonObject incident, Scrubber scrubber, int maximumBytes = MaximumFileBytes)
    {
        scrubber.ScrubTree(incident);
        for (var round = 0; ; round++)
        {
            var bytes = Gzip(JsonSerializer.SerializeToUtf8Bytes(incident));
            if (bytes.Length <= maximumBytes || round >= 40) return bytes;
            Trim(incident);
        }
    }

    // Halves what is kept, the oldest part first. False when nothing more can go.
    internal static bool Trim(JsonObject incident)
    {
        var flight = incident["flight"] as JsonObject;
        var events = flight?["events"] as JsonArray;
        if (events is { Count: > 200 })
        {
            var drop = events.Count / 2;
            for (var i = 0; i < drop; i++) events.RemoveAt(0);
            flight!["trimmed"] = (flight["trimmed"]?.GetValue<long>() ?? 0) + drop;
            return true;
        }
        if (incident["log"] is JsonArray { Count: > 50 } log)
        {
            var drop = log.Count / 2;
            for (var i = 0; i < drop; i++) log.RemoveAt(0);
            return true;
        }
        if (events is not null && events.OfType<JsonObject>().Any(e => e.ContainsKey("stack")))
        {
            // The trigger keeps its stack; the events around it lose theirs.
            foreach (var e in events.OfType<JsonObject>()) e.Remove("stack");
            return true;
        }
        if (events is { Count: > 0 })
        {
            var drop = Math.Max(1, events.Count / 2);
            for (var i = 0; i < drop; i++) events.RemoveAt(0);
            flight!["trimmed"] = (flight["trimmed"]?.GetValue<long>() ?? 0) + drop;
            return true;
        }
        if (incident["snapshot"] is not null)
        {
            incident["snapshot"] = new JsonObject { ["trimmed"] = true };
            return true;
        }
        return false;
    }

    // The same document within a byte limit as JSON (the site refuses reports over 1 MB).
    public static JsonObject FitJson(JsonObject incident, int maximumBytes)
    {
        var copy = (JsonObject)incident.DeepClone();
        for (var round = 0; round < 60 && JsonSerializer.SerializeToUtf8Bytes(copy).Length > maximumBytes; round++)
            if (!Trim(copy)) break;
        return copy;
    }

    public static byte[] Gzip(byte[] json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(json);
        return output.ToArray();
    }

    public static JsonObject Read(byte[] file)
    {
        byte[] json;
        if (file.Length > 2 && file[0] == 0x1f && file[1] == 0x8b)
        {
            using var input = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            json = output.ToArray();
        }
        else json = file;
        return JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("An incident file holds no JSON object.");
    }
}
