using System.Globalization;
using System.Text.Json.Nodes;

namespace Armory.Telemetry;

// Flight events as the incident file writes them: one JSON object each, with the fields that
// mean something for its kind (docs/agent/TELEMETRY.md, "Event fields"). Only ever run when
// an incident or the last flight is written, never on the recording path.
public static class FlightJson
{
    public static string KindName(FlightKind kind) => kind switch
    {
        FlightKind.PassStart => "passStart",
        FlightKind.PassPhase => "passPhase",
        FlightKind.PassEnd => "passEnd",
        FlightKind.PassYield => "passYield",
        FlightKind.Rpc => "rpc",
        FlightKind.Transfer => "transfer",
        FlightKind.WindowAction => "windowAction",
        FlightKind.Notice => "notice",
        FlightKind.FileFailed => "fileFailed",
        FlightKind.FileRecovered => "fileRecovered",
        FlightKind.Exception => "exception",
        FlightKind.ReadOnlyBroken => "readOnlyBroken",
        FlightKind.RepairedCheckout => "repairedCheckout",
        FlightKind.Note => "note",
        FlightKind.OpenFiles => "openFiles",
        FlightKind.Power => "power",
        _ => "unknown",
    };

    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static JsonArray Events(IEnumerable<FlightEvent> events, Func<long, DateTimeOffset> utcAt)
    {
        var array = new JsonArray();
        foreach (var e in events) array.Add(Event(e, utcAt(e.Timestamp)));
        return array;
    }

    public static JsonObject Event(in FlightEvent e, DateTimeOffset at)
    {
        var o = new JsonObject
        {
            ["seq"] = e.Sequence,
            ["at"] = Time(at),
            ["kind"] = KindName(e.Kind),
        };
        switch (e.Kind)
        {
            case FlightKind.PassStart:
                o["pass"] = e.Name;
                break;
            case FlightKind.PassPhase:
                o["phase"] = e.Name;
                o["ms"] = e.Ms;
                break;
            case FlightKind.PassEnd:
                o["pass"] = e.Name;
                o["ok"] = e.Ok;
                o["ms"] = e.Ms;
                o["downloaded"] = e.Count;
                o["uploaded"] = e.Count2;
                o["keptCopies"] = e.Count3;
                o["refused"] = e.Count4;
                break;
            case FlightKind.PassYield:
                o["reason"] = e.Name;
                o["unitsLeft"] = e.Count;
                o["carried"] = e.Count2;
                o["ms"] = e.Ms;
                break;
            case FlightKind.Rpc:
                o["name"] = e.Name;
                o["ms"] = e.Ms;
                o["status"] = e.Status;
                o["ok"] = e.Ok;
                if (e.Detail is not null) o["error"] = e.Detail;
                break;
            case FlightKind.Transfer:
                o["direction"] = e.Name;
                o["bytes"] = e.Bytes;
                o["ms"] = e.Ms;
                o["ok"] = e.Ok;
                if (e.Status != 0) o["status"] = e.Status;
                if (e.Detail is not null) o["error"] = e.Detail;
                break;
            case FlightKind.WindowAction:
                o["action"] = e.Name;
                o["targets"] = e.Count;
                o["ms"] = e.Ms;
                o["ok"] = e.Ok;
                break;
            case FlightKind.Notice:
                o["notice"] = e.Name;
                if (!string.IsNullOrEmpty(e.Target)) o["path"] = e.Target;
                if (e.Detail is not null) o["detail"] = e.Detail;
                break;
            case FlightKind.FileFailed:
                o["path"] = e.Target;
                o["error"] = e.Name;
                o["message"] = e.Detail;
                if (e.Stack is not null) o["stack"] = e.Stack;
                break;
            case FlightKind.FileRecovered:
            case FlightKind.ReadOnlyBroken:
            case FlightKind.RepairedCheckout:
                o["path"] = e.Target;
                break;
            case FlightKind.Exception:
                o["type"] = e.Name;
                o["where"] = e.Target;
                o["message"] = e.Detail;
                o["fatal"] = e.Fatal;
                if (e.Stack is not null) o["stack"] = e.Stack;
                break;
            case FlightKind.Note:
                o["name"] = e.Name;
                if (e.Detail is not null) o["detail"] = e.Detail;
                break;
            case FlightKind.OpenFiles:
                o["ms"] = e.Ms;
                o["files"] = e.Count;
                o["timedOut"] = !e.Ok;
                break;
            case FlightKind.Power:
                o["mode"] = e.Name;
                break;
        }
        return o;
    }
}
