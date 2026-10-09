#!/usr/bin/env python3
"""Prints an IDEA Armory incident as a readable timeline (docs/agent/TELEMETRY.md).

    python3 tools/read-incident/read_incident.py <incident> [<incident> ...] [options]

An incident is a file from %LOCALAPPDATA%\\IDEA Armory\\incidents (<utc>-<kind>.json.gz, or
.sent.json.gz / .held.json.gz), the same JSON uncompressed, one .json the website exports
(the report plus the row's fields), the website's "Download all as zip", or a folder of any
of these. Python 3 standard library only; nothing is sent anywhere.

Options:
    --events N   how many of the last events to list (default 80; 0 for all)
    --log N      how many of the last agent.log lines to print (default 40; 0 for all)
    --slowest N  how many of the slowest calls and transfers to list (default 10)
    --kind K     only incidents of this kind (crash, slowAction, slowPass, repeatedFailure,
                 repairedCheckout, readOnlyBroken, userReport)
    --json       print each incident's JSON as it is, pretty, and nothing else
"""

import argparse
import gzip
import io
import json
import os
import sys
import zipfile
from datetime import datetime

ROW_FIELDS = ("id", "created_at", "email", "device_name", "app_version", "kind", "summary", "project_id", "feedback_id", "status")


def parse_time(text):
    if not text or not isinstance(text, str):
        return None
    try:
        return datetime.strptime(text.replace("Z", "+0000"), "%Y-%m-%dT%H:%M:%S.%f%z")
    except ValueError:
        try:
            return datetime.fromisoformat(text.replace("Z", "+00:00"))
        except ValueError:
            return None


def decode(data):
    if data[:2] == b"\x1f\x8b":
        data = gzip.decompress(data)
    return json.loads(data.decode("utf-8"))


def from_export(document):
    """A website export holds the report verbatim plus the row's fields; an app file is the report."""
    if isinstance(document, dict) and isinstance(document.get("report"), dict):
        row = {k: document.get(k) for k in ROW_FIELDS if k in document}
        return document["report"], row
    return document, None


def load(paths):
    """Yields (name, incident, row) for every incident under the given paths."""
    for path in paths:
        if os.path.isdir(path):
            names = sorted(n for n in os.listdir(path) if n.endswith((".json.gz", ".json", ".zip")) and not n.startswith(("last-flight", "upload-wait")))
            yield from load([os.path.join(path, n) for n in names])
        elif path.endswith(".zip"):
            with zipfile.ZipFile(path) as archive:
                for member in sorted(archive.namelist()):
                    if member.endswith((".json", ".json.gz")):
                        incident, row = from_export(decode(archive.read(member)))
                        yield f"{os.path.basename(path)}:{member}", incident, row
        else:
            with open(path, "rb") as handle:
                incident, row = from_export(decode(handle.read()))
            yield os.path.basename(path), incident, row


def ms(value):
    if value is None:
        return ""
    return f"{value / 1000:.1f} s" if value >= 1000 else f"{value} ms"


def size(value):
    if value is None:
        return ""
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.0f} {unit}" if unit == "B" else f"{value:.1f} {unit}"
        value /= 1024.0
    return str(value)


def ok(event):
    return "ok" if event.get("ok", True) else "FAILED"


def describe(event):
    """One line for one flight event (the fields FlightJson writes for its kind)."""
    kind = event.get("kind", "?")
    if kind == "passStart":
        return f"pass start ({event.get('pass')})"
    if kind == "passPhase":
        return f"  phase {event.get('phase')}: {ms(event.get('ms'))}"
    if kind == "passEnd":
        return (f"pass end ({event.get('pass')}) {ok(event)} after {ms(event.get('ms'))}: {event.get('downloaded', 0)} down, "
                f"{event.get('uploaded', 0)} up, {event.get('keptCopies', 0)} kept copies, {event.get('refused', 0)} refused")
    if kind == "passYield":
        carried = f", {event['carried']} carried on" if event.get("carried") else ""
        return f"pass gave way ({event.get('reason')}) after {ms(event.get('ms'))}, {event.get('unitsLeft')} units left{carried}"
    if kind == "rpc":
        error = f" {event['error']}" if event.get("error") else ""
        return f"rpc {event.get('name')} {ms(event.get('ms'))} {event.get('status')} {ok(event)}{error}"
    if kind == "transfer":
        error = f" {event['error']}" if event.get("error") else ""
        status = f" {event['status']}" if event.get("status") else ""
        return f"{event.get('direction')} {size(event.get('bytes'))} in {ms(event.get('ms'))}{status} {ok(event)}{error}"
    if kind == "windowAction":
        return f"window {event.get('action')} ({event.get('targets')} targets) answered in {ms(event.get('ms'))} {ok(event)}"
    if kind == "notice":
        detail = f": {event['detail']}" if event.get("detail") else ""
        return f"notice {event.get('notice')} {event.get('path', '')}{detail}"
    if kind == "fileFailed":
        return f"FILE FAILED {event.get('path')}: {event.get('error')}: {event.get('message')}"
    if kind == "fileRecovered":
        return f"file went through again {event.get('path')}"
    if kind == "exception":
        fatal = "FATAL " if event.get("fatal") else ""
        return f"{fatal}EXCEPTION in {event.get('where')}: {event.get('type')}: {event.get('message')}"
    if kind == "readOnlyBroken":
        return f"READ-ONLY BROKEN {event.get('path')} (made read-only again)"
    if kind == "repairedCheckout":
        return f"CHECK OUT REPAIRED {event.get('path')} (a lock held here had no record)"
    if kind == "previousRunEnded":
        return f"previous run ended unexpectedly; last log line: {event.get('lastLogLine')}"
    if kind == "note":
        return f"note {event.get('name')}: {event.get('detail', '')}"
    if kind == "openFiles":
        late = ", gave up at its budget" if event.get("timedOut") else ""
        return f"open-files question about {event.get('files')} files: {ms(event.get('ms'))}{late}"
    if kind == "power":
        return f"the computer {'went to sleep' if event.get('mode') == 'suspend' else 'woke up' if event.get('mode') == 'resume' else event.get('mode')}"
    return json.dumps(event, ensure_ascii=False)


def relative(event, anchor):
    at = parse_time(event.get("at"))
    if at is None or anchor is None:
        return "         "
    seconds = (at - anchor).total_seconds()
    return f"{seconds:+9.3f}s"


def print_incident(name, incident, row, args, out):
    events = (incident.get("flight") or {}).get("events") or []
    trigger = incident.get("trigger") or {}
    anchor = parse_time(trigger.get("at")) or (parse_time(events[-1].get("at")) if events else None) or parse_time(incident.get("createdAt"))
    rule = "=" * 100
    out.write(f"{rule}\n{name}\n{rule}\n")
    out.write(f"Incident   {incident.get('kind')}   {incident.get('createdAt')}   (schema {incident.get('schemaVersion')}, id {incident.get('id')})\n")
    out.write(f"App        {incident.get('appVersion')} on {incident.get('osVersion')}\n")
    out.write(f"Who        {incident.get('email')} on {incident.get('deviceName')}" + (f", project {incident['projectId']}" if incident.get("projectId") else "") + "\n")
    if row:
        out.write("Website    " + ", ".join(f"{k}={row[k]}" for k in row if row[k] is not None and k not in ("summary",)) + "\n")
    out.write(f"Summary    {incident.get('summary')}\n")
    feedback = incident.get("feedback")
    if feedback:
        out.write(f"Feedback   ({feedback.get('kind')}) {feedback.get('body')}\n")
        out.write(f"           sent as {incident.get('feedbackId') or '(not sent yet)'}\n")
    out.write(f"Trigger    {describe(trigger) if trigger else '(none)'}\n")
    if trigger.get("stack"):
        out.write(indent(trigger["stack"], 11))

    flight = incident.get("flight") or {}
    shown = events if args.events == 0 else events[-args.events:]
    out.write(f"\nTimeline: last {len(shown)} of {len(events)} events kept ({flight.get('recorded')} recorded since start, "
              f"ring of {flight.get('capacity')}, {flight.get('trimmed', 0)} trimmed to fit); seconds relative to the trigger\n")
    trigger_seq = trigger.get("seq")
    for event in shown:
        mark = ">>" if trigger_seq is not None and event.get("seq") == trigger_seq else "  "
        out.write(f"{mark}{relative(event, anchor)}  {describe(event)}\n")

    timed = [e for e in events if e.get("kind") in ("rpc", "transfer", "windowAction", "passEnd", "openFiles") and e.get("ms") is not None]
    if timed:
        out.write(f"\nSlowest ({min(args.slowest, len(timed))} of {len(timed)} calls, transfers, window actions and passes)\n")
        for event in sorted(timed, key=lambda e: e.get("ms", 0), reverse=True)[: args.slowest]:
            out.write(f"  {relative(event, anchor)}  {describe(event)}\n")
        rpcs = [e for e in events if e.get("kind") == "rpc"]
        if rpcs:
            failed = [e for e in rpcs if not e.get("ok", True)]
            out.write(f"  {len(rpcs)} server calls, {len(failed)} failed" +
                      (f" ({', '.join(sorted({str(e.get('error') or e.get('status')) for e in failed}))})" if failed else "") + "\n")

    errors = [e for e in events if e.get("kind") in ("exception", "fileFailed", "readOnlyBroken", "repairedCheckout")]
    if errors:
        out.write(f"\nErrors and repairs ({len(errors)})\n")
        for event in errors:
            out.write(f"  {relative(event, anchor)}  {describe(event)}\n")
            if event.get("stack"):
                out.write(indent(event["stack"], 14))

    snapshot = incident.get("snapshot")
    if snapshot is not None:
        out.write("\nSnapshot\n")
        out.write(indent(json.dumps(snapshot, indent=2, ensure_ascii=False), 2))

    log = incident.get("log") or []
    if log:
        lines = log if args.log == 0 else log[-args.log:]
        out.write(f"\nagent.log (last {len(lines)} of {len(log)} lines kept)\n")
        for line in lines:
            out.write(f"  {line}\n")
    out.write("\n")


def indent(text, spaces):
    pad = " " * spaces
    return "".join(pad + line + "\n" for line in str(text).splitlines())


def main(argv=None):
    parser = argparse.ArgumentParser(description="Print an IDEA Armory incident as a readable timeline.")
    parser.add_argument("paths", nargs="+", help=".json.gz, .json, the website's export .zip, or a folder")
    parser.add_argument("--events", type=int, default=80)
    parser.add_argument("--log", type=int, default=40)
    parser.add_argument("--slowest", type=int, default=10)
    parser.add_argument("--kind")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    out = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace") if hasattr(sys.stdout, "buffer") else sys.stdout
    found = 0
    for name, incident, row in load(args.paths):
        if args.kind and incident.get("kind") != args.kind:
            continue
        found += 1
        if args.json:
            out.write(json.dumps(incident, indent=2, ensure_ascii=False) + "\n")
        else:
            print_incident(name, incident, row, args, out)
    out.flush()
    if not found:
        sys.stderr.write("No incident found.\n")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
