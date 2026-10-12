"""Where each late frame of a flight went: OFXR's flight log, dcs.log (with the perf-test mission's script timing),
DCS Control's flight monitor and, when given, a WPR trace (tools/FlightTrace).

usage: python -I scripts/analyze-flight-spikes.py [--ofxr LOG] [--dcs-log LOG] [--monitor CSV] [--etl TRACE]
                                                [--symbols] [--top N] [--json OUT]

Without paths it takes the newest OFXR flight log, the dcs.log of the newest DCS install's Saved Games and the flight
monitor file of the same DCS process. The three clocks are tied together by the session's IDLE->READY change, which
both OFXR's log (session_state 2) and dcs.log record, and by dcs.log's own local-time stamps on OpenXR lines.

Late frame: an interval between two of DCS's xrWaitFrame calls (its main thread) over 1.25 frame slots, in steady
flight as Last flight counts it. DCS's render thread runs a frame behind the main thread, and xrWaitFrame does not
return before the previous frame is begun (OpenXR's rule), so a main thread held in xrWaitFrame is waiting for the
render thread. Each late frame is therefore split into:
  main      the main thread's own time outside xrWaitFrame (simulation, scripts, ...);
  and, when it was held in xrWaitFrame, the render thread's cycle that released it:
  render    from xrBeginFrame to xrEndFrame (DCS's drawing, OFXR's swapchain calls);
  end       xrEndFrame (OFXR's pacing hold when the frame limit is not Match refresh);
  between   from xrEndFrame's return to the next xrBeginFrame (outside OpenXR's frame calls).
Each is compared with its median over steady flight; "where" names the one that grew most.
"""
import argparse
import bisect
import glob
import json
import os
import re
import statistics
import subprocess
import sys
from datetime import datetime, timedelta

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCAL = os.environ.get("LOCALAPPDATA", "")
HOME = os.environ.get("USERPROFILE", "")
RECORD = re.compile(r"(\w+)=(\S+)")
DCS_LINE = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (\S+)\s+(.*?): (.*)$")
PERFT = re.compile(r"^PERFT([<>])(\d+)(?: (.*))?$")


def newest(pattern):
    files = glob.glob(pattern)
    return max(files, key=os.path.getmtime) if files else None


# ---- OFXR -------------------------------------------------------------------------------------------------------

class Frame:
    __slots__ = ("wait_b", "wait_ms", "interval", "steady", "late", "parts")

    def __init__(self, wait_b):
        self.wait_b, self.wait_ms, self.interval, self.steady, self.late, self.parts = wait_b, 0.0, None, False, False, {}


class Render:
    """The render thread's xrBeginFrame entries and xrEndFrame calls (entry, return)."""

    def __init__(self):
        self.begins, self.ends = [], []

    def cycle(self, release):
        """The render thread's cycle that ended with the xrBeginFrame at or just before release: its drawing, its
        xrEndFrame and the time from there to that xrBeginFrame."""
        i = bisect.bisect_right(self.begins, release + 0.1) - 1
        if i < 1:
            return None
        start, stop = self.begins[i - 1], self.begins[i]
        j = bisect.bisect_right([e[0] for e in self.ends], start)
        if j >= len(self.ends) or self.ends[j][0] > stop:
            return None
        end_b, end_e = self.ends[j]
        return {"render": end_b - start, "end": end_e - end_b, "between": max(0.0, stop - end_e)}


def read_ofxr(path):
    m = re.search(r"ofxr-bridge-flight-(\d{8}-\d{6})-pid(\d+)\.log$", path)
    if not m:
        sys.exit("not an OFXR flight log: " + path)
    pid = int(m.group(2))
    frames, render, opened = [], Render(), None
    tids = {}
    ready_ms, states = None, []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if " op=app_" not in line and " op=frame_load" not in line and " op=session_state" not in line and " op=internal_wait_frame" not in line:
                continue
            d = dict(RECORD.findall(line))
            op, phase, ms = d["op"], d["phase"], float(d["ms"])
            if op == "app_wait_frame":
                tids.setdefault("main", int(d["tid"]))
                if phase == "B":
                    frames.append(Frame(ms))
                elif frames:
                    frames[-1].wait_ms = int(d["dur_us"]) / 1000
            elif op == "app_begin_frame" and phase == "B":
                tids.setdefault("render", int(d["tid"]))
                render.begins.append(ms)
            elif op == "app_end_frame":
                if phase == "B":
                    opened = ms
                elif opened is not None:
                    render.ends.append((opened, ms))
                    opened = None
            elif op == "internal_wait_frame":
                tids.setdefault("presenter", int(d["tid"]))
            elif op == "session_state":
                state = int(d["result"])
                states.append((ms, state))
                if state == 2 and ready_ms is None:
                    ready_ms = ms
    return pid, tids, frames, render, ready_ms, states


def mark_steady(frames, states):
    """A frame's interval runs from its own xrWaitFrame to the next one, so its wait, render and xrEndFrame are what
    filled it. Steady seconds as Last flight counts them: at 90% or more of the paced rate, FOCUSED, and so were the
    2 s either side."""
    for a, b in zip(frames, frames[1:]):
        a.interval = b.wait_b - a.wait_b
    timed = [f for f in frames if f.interval is not None and f.interval < 1000]
    if not timed:
        return None
    slot = statistics.median(f.interval for f in timed)
    changes = [ms for ms, _ in states]

    def focused(ms):
        i = bisect.bisect_right(changes, ms) - 1
        return i >= 0 and states[i][1] == 5

    counts = {}
    for f in frames:
        counts[int(f.wait_b // 1000)] = counts.get(int(f.wait_b // 1000), 0) + 1
    good = {s for s, n in counts.items() if n >= 0.9 * 1000 / slot and focused(s * 1000) and focused(s * 1000 + 999)}
    steady = {s for s in good if all(x in good for x in range(s - 2, s + 3))}
    for f in timed:
        f.steady = int(f.wait_b // 1000) in steady and int((f.wait_b + f.interval) // 1000) in steady
        f.late = f.steady and f.interval > slot * 1.25
    return slot


PARTS = ("main", "render", "end", "between")


def split_frames(steady, late, render):
    """Each frame's parts (see the module notes) and, for late ones, how much each grew beyond its steady median and
    which grew most. Returns the medians."""
    for f in steady:
        raw = {"main": f.interval - f.wait_ms}
        held = render.cycle(f.wait_b + f.wait_ms)
        if held:
            raw.update(held)
        f.parts = {"raw": raw}
    usual = {k: statistics.median([f.parts["raw"][k] for f in steady if k in f.parts["raw"]] or [0.0]) for k in PARTS}
    usual_wait = statistics.median([f.wait_ms for f in steady] or [0.0])
    for f in late:
        raw = f.parts["raw"]
        grown = {"main": max(0.0, raw["main"] - usual["main"])}
        if f.wait_ms - usual_wait > 1.0:
            for k in ("render", "end", "between"):
                if k in raw:
                    grown[k] = max(0.0, raw[k] - usual[k])
        f.parts.update(grown)
        f.parts["where"] = max(grown, key=grown.get)
    return usual


# ---- dcs.log ----------------------------------------------------------------------------------------------------

def read_dcs_log(path):
    lines, lag = [], None
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            m = DCS_LINE.match(raw.rstrip("\n"))
            if not m:
                continue
            at = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S.%f")
            text = m.group(4)
            if lag is None:
                inner = re.match(r"\[(\d{2}:\d{2}:\d{2}\.\d{3})\]", text)
                if inner:
                    t = datetime.strptime(inner.group(1), "%H:%M:%S.%f").time()
                    local = datetime.combine(at.date(), t)
                    while local - at > timedelta(hours=14):
                        local -= timedelta(days=1)
                    while at - local > timedelta(hours=14):
                        local += timedelta(days=1)
                    lag = local - at
            lines.append((at, m.group(3), text))
    ready = next((at for at, _, text in lines if "XR_SESSION_STATE_IDLE->XR_SESSION_STATE_READY" in text), None)
    return lines, ready, lag or timedelta(0)


def script_calls(lines, to_ofxr):
    opened, calls = {}, []
    for at, _, text in lines:
        m = PERFT.match(text)
        if not m:
            continue
        kind, n = m.group(1), int(m.group(2))
        if kind == ">":
            opened[n] = (at, m.group(3) or "?")
        elif n in opened:
            start, name = opened.pop(n)
            calls.append((to_ofxr(start), (at - start).total_seconds() * 1000, name))
    return calls


# ---- Flight monitor -----------------------------------------------------------------------------------------------

def read_monitor(path):
    start, header, rows = None, None, []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("# start="):
                start = datetime.strptime(line.split()[1][len("start="):], "%Y-%m-%dT%H:%M:%S.%f")
            elif line.startswith("#"):
                continue
            elif header is None and not line[:1].isdigit():
                header = line.split(",")
            elif line[:1].isdigit():
                rows.append(line.split(","))
    return start, header, rows


def monitor_row_text(header, row):
    if not header:
        return ",".join(row)
    cells = dict(zip(header, row))
    parts = []
    for i in (1, 2, 3):
        name, pct = cells.get(f"t{i}", ""), cells.get(f"t{i}_pct", "")
        if name:
            parts.append(f"{name} {pct}%")
    if cells.get("other_cpu"):
        parts.append(f"other: {cells['other_cpu']} {cells.get('other_cpu_pct', '')}%")
    if cells.get("cpu_pct"):
        parts.append(f"CPU {cells['cpu_pct']}%")
    if cells.get("vram_mib"):
        parts.append(f"VRAM {cells['vram_mib']} MiB")
    return ", ".join(parts) if parts else ",".join(row)


# ---- Trace ------------------------------------------------------------------------------------------------------

def run_trace(etl, ofxr, pid, tids, windows, symbols, scratch):
    exe = os.path.join(REPO, "tools", "FlightTrace", "bin", "Release", "net10.0-windows", "FlightTrace.exe")
    if not os.path.exists(exe):
        dotnet = os.path.join(REPO, ".tools", "dotnet", "dotnet.exe")
        subprocess.run([dotnet, "build", os.path.join(REPO, "tools", "FlightTrace"), "-c", "Release"], check=True)
    request = os.path.join(scratch, "flight-trace-windows.json")
    out = os.path.join(scratch, "flight-trace.json")
    with open(request, "w", encoding="utf-8") as f:
        json.dump({"pid": pid, "threads": tids, "windows": windows}, f)
    cmd = [exe, "--etl", etl, "--ofxr", ofxr, "--windows", request, "--out", out] + (["--symbols"] if symbols else [])
    subprocess.run(cmd, check=True)
    with open(out, encoding="utf-8") as f:
        return json.load(f)


def shares(items, n=4):
    return ", ".join(f"{s['name']} {s['ms']:.1f}" for s in items[:n]) or "-"


def trace_text(w):
    out = []
    for t in w["threads"]:
        out.append(f"     {t['role']:<9} running {t['running_ms']:.1f} ms, not {t['not_running_ms']:.1f}; in: {shares(t['in'])}; "
                   f"called from: {shares(t['called_from'], 3)}")
        for wt in t["waits"][:3]:
            out.append(f"        waited {wt['ms']:.1f} ms (+{wt['ready_ms']:.1f} ready), woken by {wt['readied_by']}: "
                       f"{' < '.join(wt['ready_stack'][:5]) or '-'}")
    if w["other_dcs_threads"]:
        out.append("     other DCS threads: " + "; ".join(
            f"#{o['tid']}{(' ' + o['name']) if o['name'] else ''} {o['running_ms']:.1f} ms ({shares(o['in'], 2)})" for o in w["other_dcs_threads"][:4]))
    out.append("     processes: " + ", ".join(f"{p['name']} {p['ms']:.1f}" for p in w["processes"][:6]))
    irq = w["interrupts"]
    if irq["dpc_ms"] or irq["isr_ms"]:
        out.append(f"     DPC {irq['dpc_ms']:.1f} ms, ISR {irq['isr_ms']:.1f} ms ({shares(irq['top'], 3)})")
    return out


# ---- Report -----------------------------------------------------------------------------------------------------

def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--ofxr")
    p.add_argument("--dcs-log")
    p.add_argument("--monitor")
    p.add_argument("--etl")
    p.add_argument("--symbols", action="store_true")
    p.add_argument("--top", type=int, default=25)
    p.add_argument("--json")
    a = p.parse_args()

    ofxr = a.ofxr or newest(os.path.join(LOCAL, "DcsControl", "managed", "profiles", "*", "ofxr", "ofxr-bridge-flight-*.log"))
    if not ofxr:
        sys.exit("no OFXR flight log found (--ofxr)")
    pid, tids, frames, render, ready_ms, states = read_ofxr(ofxr)
    slot = mark_steady(frames, states)
    if slot is None:
        sys.exit("no frames in " + ofxr)
    steady = [f for f in frames if f.steady]
    late = [f for f in frames if f.late]

    dcs_log = a.dcs_log or newest(os.path.join(HOME, "Saved Games", "DCS*", "Logs", "dcs.log"))
    lines, ready_dcs, lag, calls, to_ofxr = [], None, timedelta(0), [], None
    if dcs_log:
        lines, ready_dcs, lag = read_dcs_log(dcs_log)
        if ready_dcs is None or ready_ms is None:
            print(f"warning: {dcs_log} and the OFXR log share no session start; dcs.log left out")
            lines = []
        else:
            def to_ofxr(at):
                return (at - ready_dcs).total_seconds() + ready_ms / 1000
            first_wall = lines[0][0] + lag
            ofxr_name = datetime.strptime(re.search(r"(\d{8}-\d{6})", os.path.basename(ofxr)).group(1), "%Y%m%d-%H%M%S")
            if abs((first_wall - ofxr_name).total_seconds()) > 3600:
                print(f"warning: {dcs_log} starts {first_wall}, the OFXR log {ofxr_name}: not the same run?")
            calls = script_calls(lines, to_ofxr)

    monitor = a.monitor or newest(os.path.join(LOCAL, "DcsControl", "boost", f"flight-*-pid{pid}.csv"))
    mon_start, mon_header, mon_rows = read_monitor(monitor) if monitor else (None, None, [])

    def monitor_at(t):
        if not mon_rows or mon_start is None or to_ofxr is None:
            return None
        ofxr_zero_wall = ready_dcs + lag - timedelta(milliseconds=ready_ms)
        target = (ofxr_zero_wall + timedelta(seconds=t) - mon_start).total_seconds() * 1000
        # Each row covers the second before its time.
        for row in mon_rows:
            if float(row[0]) >= target:
                return monitor_row_text(mon_header, row)
        return None

    worst = sorted(late, key=lambda f: -f.interval)[: a.top]
    trace = None
    if a.etl:
        windows = [{"id": f"{f.wait_b:.3f}", "start_ms": f.wait_b, "end_ms": f.wait_b + f.interval} for f in worst]
        if steady:
            windows.append({"id": "steady", "start_ms": steady[0].wait_b, "end_ms": steady[-1].wait_b + steady[-1].interval})
        scratch = os.path.join(LOCAL, "DcsControl", "traces")
        os.makedirs(scratch, exist_ok=True)
        trace = run_trace(a.etl, ofxr, pid, tids, windows, a.symbols, scratch)
        by_id = {w["id"]: w for w in trace["windows"]}

    print(f"OFXR log:  {ofxr}")
    print(f"dcs.log:   {dcs_log if lines else '(not used)'}")
    print(f"monitor:   {monitor or '(none)'}")
    if trace:
        print(f"trace:     {trace['etl']} (OFXR + {trace['offset_ms']:.3f} ms; alignment {trace['alignment_score']:.0%}, "
              f"next best {trace['alignment_runner_up']:.0%})")
    secs = sum(f.interval for f in steady) / 1000
    print(f"\nSteady flight: {secs:.0f} s, frame slot {slot:.1f} ms; {len(late)} late frames over {slot * 1.25:.1f} ms "
          f"({len(late) / secs * 60 if secs else 0:.1f}/min)")
    usual = split_frames(steady, late, render)
    if late:
        excess = sum(f.interval - slot for f in late)
        grown = {k: sum(f.parts.get(k, 0.0) for f in late) for k in PARTS}
        print(f"  their time over the slot: {excess:.0f} ms; grown beyond the usual (median) "
              + ", ".join(f"{k} {grown[k]:.0f} ms (usual {usual[k]:.1f})" for k in PARTS))
        where = {}
        for f in late:
            where[f.parts["where"]] = where.get(f.parts["where"], 0) + 1
        print("  where, per late frame: " + ", ".join(f"{k} {n}" for k, n in sorted(where.items(), key=lambda kv: -kv[1])))

    if lines:
        timed = [c for c in calls if c[1] > 0]
        print(f"\nScript calls (PERFT): {len(calls)}" + ("" if calls else " (none: a mission without script timing)"))
        if calls:
            by_name = {}
            for t, ms, name in calls:
                s = by_name.setdefault(name, [0, 0.0, 0.0])
                s[0] += 1
                s[1] += ms
                s[2] = max(s[2], ms)
            print("  longest single calls:")
            for t, ms, name in sorted(timed, key=lambda c: -c[1])[:10]:
                print(f"    {ms:6.0f} ms  t={t:7.2f}  {name}")
            print("  by function (calls, total ms, longest ms):")
            for name, (n, total, mx) in sorted(by_name.items(), key=lambda kv: -kv[1][1])[:10]:
                print(f"    {n:6d} {total:8.0f} {mx:6.0f}  {name}")

    print(f"\nLate frames, longest first (up to {a.top}):")
    for f in worst:
        start = f.wait_b / 1000
        end = (f.wait_b + f.interval) / 1000
        raw = f.parts.get("raw", {})
        parts = [f"xrWaitFrame {f.wait_ms:.1f}"] + [f"{k} {raw[k]:.1f}" for k in PARTS if k in raw]
        print(f"\n  t={end:7.2f} s  interval {f.interval:.1f} ms, where: {f.parts.get('where', '?')}  ({', '.join(parts)})")
        overlapping = [c for c in calls if c[0] < end and c[0] + c[1] / 1000 > start - 0.05 and c[1] > 0]
        if overlapping:
            print("     script: " + "; ".join(f"{ms:.0f} ms {name} at {t:.2f}" for t, ms, name in overlapping[:4]))
        if lines and to_ofxr:
            near = [(to_ofxr(at), src, text) for at, src, text in lines
                    if not text.startswith("PERFT") and start - 0.3 <= to_ofxr(at) <= end + 0.3]
            for t, src, text in near[:4]:
                print(f"     dcs.log {t:7.2f}: {src}: {text[:110]}")
        row = monitor_at(end)
        if row:
            print(f"     monitor: {row}")
        if trace and f"{f.wait_b:.3f}" in by_id:
            print("\n".join(trace_text(by_id[f"{f.wait_b:.3f}"])))

    if trace and "steady" in by_id:
        print("\nWhole steady flight, for comparison:")
        print("\n".join(trace_text(by_id["steady"])))

    if a.json:
        with open(a.json, "w", encoding="utf-8") as fp:
            json.dump({"ofxr": ofxr, "dcs_log": dcs_log, "monitor": monitor, "slot_ms": slot, "steady_s": secs,
                       "late": [{"t": (f.wait_b + f.interval) / 1000, "interval_ms": f.interval, "wait_ms": f.wait_ms,
                                 "parts": f.parts} for f in late],
                       "script_calls": [{"t": t, "ms": ms, "name": n} for t, ms, n in calls],
                       "trace": trace}, fp, indent=1)


if __name__ == "__main__":
    main()
