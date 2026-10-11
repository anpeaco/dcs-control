"""Paired A/B analysis of a PresentMon capture taken by measure-engine-ab.ps1.

Mirrors the statistics of the in-module benchmark (native/dcsqvcull/src/bench.h):
each ON block is compared against the mean of its neighbouring OFF blocks, so
slow drift in a live scene cancels out, and the spread of those paired
differences gives a Student's t 95% interval. A change smaller than its own
interval is reported as noise rather than as a result.

Reads PresentMon 1.x and 2.x CSVs (columns are detected, not assumed) and the
schedule.json the PowerShell harness writes. Standard library only.

Usage:
    py scripts/analyze-engine-ab.py frames.csv schedule.json [--report report.txt]
"""
import argparse
import csv
import json
import math
import statistics
from collections import Counter, defaultdict
from pathlib import Path

# Student's t for a 95% two-sided interval, indexed by degrees of freedom.
# Same table as bench.h so numbers are comparable with the in-game suite.
T95 = [0.0, 12.71, 4.30, 3.18, 2.78, 2.57, 2.45, 2.36, 2.31, 2.26, 2.23, 2.20, 2.18]
T95_TAIL = 2.10

# A statistically significant change smaller than this is reported as
# negligible rather than as better or worse: below it the figure says nothing
# useful about whether to keep an optimization on.
NEGLIGIBLE_PCT = 1.0

# Candidate column names per quantity, in preference order. PresentMon renamed
# most of these between 1.x and 2.x and changed capitalisation along the way, so
# matching is case-insensitive.
COLUMNS = {
    'time': ['TimeInSeconds', 'CPUStartTime', 'CPUStartTimeInSeconds'],
    'frame_ms': ['FrameTime', 'msBetweenPresents', 'MsBetweenPresents'],
    'cpu_busy_ms': ['CPUBusy', 'MsCPUBusy'],
    'gpu_busy_ms': ['GPUBusy', 'MsGPUBusy'],
    'gpu_time_ms': ['GPUTime', 'MsGPUTime'],
    'gpu_wait_ms': ['GPUWait', 'MsGPUWait'],
    'present_ms': ['msInPresentAPI', 'MsInPresentAPI'],
}

# Reported metric: (key, label, lower_is_better). FPS is derived, the rest are
# per-frame millisecond columns averaged over the block.
METRICS = [
    ('fps', 'frames per second', False),
    ('frame_ms', 'frame time ms', True),
    ('p95_ms', 'p95 frame time ms', True),
    ('p99_ms', 'p99 frame time ms', True),
    ('cpu_busy_ms', 'CPU busy ms/frame', True),
    ('gpu_busy_ms', 'GPU busy ms/frame', True),
    ('gpu_wait_ms', 'GPU wait ms/frame', True),
    ('present_ms', 'in Present() ms/frame', True),
]


def resolve_columns(fieldnames):
    """Maps our quantity names onto the CSV's actual column names."""
    lookup = {name.strip().lower(): name for name in fieldnames if name}
    found = {}
    for key, candidates in COLUMNS.items():
        for candidate in candidates:
            actual = lookup.get(candidate.lower())
            if actual:
                found[key] = actual
                break
    return found, lookup


def percentile(sorted_values, fraction):
    """Nearest-rank percentile of an already sorted, non-empty list."""
    if not sorted_values:
        return None
    index = max(0, min(len(sorted_values) - 1, int(math.ceil(fraction * len(sorted_values))) - 1))
    return sorted_values[index]


def load_frames(csv_path):
    """Returns (frames, columns, notes). A frame is a dict of floats plus 'time'."""
    notes = []
    with open(csv_path, 'r', newline='', encoding='utf-8-sig', errors='replace') as handle:
        reader = csv.DictReader(handle)
        if not reader.fieldnames:
            raise SystemExit(f'{csv_path} has no header row.')
        columns, lookup = resolve_columns(reader.fieldnames)
        if 'time' not in columns:
            raise SystemExit(
                'No usable time column in the CSV (looked for '
                + ', '.join(COLUMNS['time']) + '). Is this a PresentMon capture?')
        if 'frame_ms' not in columns:
            raise SystemExit(
                'No usable frame-time column in the CSV (looked for '
                + ', '.join(COLUMNS['frame_ms']) + ').')

        dropped_col = lookup.get('dropped')
        type_col = lookup.get('frametype')
        chain_col = lookup.get('swapchainaddress')

        rows = []
        skipped_dropped = 0
        skipped_type = Counter()
        malformed = 0
        for row in reader:
            if dropped_col:
                flag = (row.get(dropped_col) or '0').strip()
                if flag not in ('0', '', 'FALSE', 'False', 'false'):
                    skipped_dropped += 1
                    continue
            if type_col:
                # 2.x emits generated and repeated frames alongside real ones;
                # only the application's own frames measure DCS's render loop.
                kind = (row.get(type_col) or 'Application').strip()
                if kind and kind.lower() != 'application':
                    skipped_type[kind] += 1
                    continue
            try:
                frame = {'time': float(row[columns['time']])}
                for key, column in columns.items():
                    if key == 'time':
                        continue
                    raw = (row.get(column) or '').strip()
                    frame[key] = float(raw) if raw not in ('', 'NA', 'NaN') else None
            except (TypeError, ValueError):
                malformed += 1
                continue
            frame['chain'] = (row.get(chain_col) or '') if chain_col else ''
            rows.append(frame)

    if skipped_dropped:
        notes.append(f'{skipped_dropped} dropped presents excluded.')
    for kind, count in skipped_type.items():
        notes.append(f'{count} {kind} frames excluded (not application-rendered).')
    if malformed:
        notes.append(f'{malformed} rows skipped as malformed.')
    if not rows:
        raise SystemExit('No usable frames in the CSV after filtering.')

    # DCS can present on more than one swapchain (the desktop mirror plus
    # others). Mixing them makes per-frame intervals meaningless, so keep the
    # busiest one and say so.
    if any(frame['chain'] for frame in rows):
        counts = Counter(frame['chain'] for frame in rows)
        main, main_count = counts.most_common(1)[0]
        if len(counts) > 1:
            others = sum(count for chain, count in counts.items() if chain != main)
            share = 100.0 * others / len(rows)
            notes.append(
                f'{len(counts)} swapchains present; kept the busiest ({main}, {main_count} frames) '
                f'and dropped {others} frames ({share:.1f}%) from the others.')
            if share > 25:
                notes.append(
                    'WARNING: a large share of frames came from other swapchains. '
                    'Check that the kept one is DCS\'s main output.')
        rows = [frame for frame in rows if frame['chain'] == main]

    rows.sort(key=lambda frame: frame['time'])
    return rows, columns, notes


def block_metrics(frames, columns):
    """Per-block aggregate metrics, or None when the block has too few frames."""
    if len(frames) < 2:
        return None
    intervals = sorted(frame['frame_ms'] for frame in frames if frame['frame_ms'] is not None)
    if not intervals:
        return None
    span = frames[-1]['time'] - frames[0]['time']
    result = {
        'frames': len(frames),
        'seconds': span,
        'fps': (len(frames) - 1) / span if span > 0 else None,
        'frame_ms': statistics.fmean(intervals),
        'p95_ms': percentile(intervals, 0.95),
        'p99_ms': percentile(intervals, 0.99),
    }
    for key in ('cpu_busy_ms', 'gpu_busy_ms', 'gpu_wait_ms', 'present_ms'):
        if key not in columns:
            result[key] = None
            continue
        values = [frame[key] for frame in frames if frame.get(key) is not None]
        result[key] = statistics.fmean(values) if values else None
    return result


def paired_change(blocks, key):
    """bench.h's comparison: each ON block against the mean of its OFF neighbours."""
    diffs = []
    for i, block in enumerate(blocks):
        if block['state'] != 'on' or not block['metrics']:
            continue
        value = block['metrics'].get(key)
        if value is None:
            continue
        baselines = []
        for neighbour in (i - 1, i + 1):
            if 0 <= neighbour < len(blocks):
                other = blocks[neighbour]
                if other['state'] == 'off' and other['metrics']:
                    base_value = other['metrics'].get(key)
                    if base_value is not None:
                        baselines.append(base_value)
        if not baselines:
            continue
        base = statistics.fmean(baselines)
        if base == 0:
            continue
        diffs.append((value - base) / base * 100.0)

    if not diffs:
        return None
    mean = statistics.fmean(diffs)
    if len(diffs) > 1:
        sd = statistics.stdev(diffs)
        dof = len(diffs) - 1
        t95 = T95[dof] if dof < len(T95) else T95_TAIL
        ci = t95 * sd / math.sqrt(len(diffs))
    else:
        sd, ci = 0.0, 0.0
    return {
        'n': len(diffs),
        'mean': mean,
        'sd': sd,
        'ci': ci,
        'significant': len(diffs) > 1 and abs(mean) > ci,
    }


def mean_by_state(blocks, key, state):
    values = [b['metrics'][key] for b in blocks
              if b['state'] == state and b['metrics'] and b['metrics'].get(key) is not None]
    return statistics.fmean(values) if values else None


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('csv', type=Path, help='PresentMon CSV')
    parser.add_argument('schedule', type=Path, help='schedule.json from measure-engine-ab.ps1')
    parser.add_argument('--report', type=Path, help='also write the report here')
    parser.add_argument('--min-frames', type=int, default=20,
                        help='blocks with fewer usable frames are discarded (default 20)')
    args = parser.parse_args()

    schedule = json.loads(args.schedule.read_text(encoding='utf-8-sig'))
    settle = float(schedule.get('settleSeconds', 0.0))
    raw_blocks = schedule.get('blocks') or []
    if isinstance(raw_blocks, dict):
        raw_blocks = [raw_blocks]
    if not raw_blocks:
        raise SystemExit('The schedule has no blocks.')

    frames, columns, notes = load_frames(args.csv)
    lines = []

    def emit(text=''):
        lines.append(text)
        print(text)

    emit('==== DCS engine optimizations: external A/B ====')
    emit(f'  frames {len(frames)} over {frames[-1]["time"] - frames[0]["time"]:.1f} s '
         f'of capture; {len(raw_blocks)} blocks, {settle:.1f} s discarded at each block start')
    emit(f'  metrics available: {", ".join(sorted(columns))}')
    for note in notes:
        emit(f'  note: {note}')

    # Bin frames into their blocks, discarding the settle window at the start of
    # each one. The toggle is requested at the end of the previous block and the
    # module polls for it once a second, so the window covers both the poll and
    # the frame-time settling that follows.
    blocks = []
    for entry in raw_blocks:
        start = float(entry['start']) + settle
        end = float(entry['end'])
        inside = [f for f in frames if start <= f['time'] <= end]
        discarded = None
        if len(inside) < args.min_frames:
            discarded = f'only {len(inside)} frames'
        blocks.append({
            'index': int(entry['index']),
            'state': str(entry['state']).lower(),
            'start': start,
            'end': end,
            'metrics': None if discarded else block_metrics(inside, columns),
            'discarded': discarded,
        })
        if blocks[-1]['metrics'] is None and not discarded:
            blocks[-1]['discarded'] = 'no usable frame intervals'

    emit()
    emit('  block  state   frames   fps    frame ms   p95 ms')
    for block in blocks:
        if not block['metrics']:
            emit(f'  {block["index"]:>5}  {block["state"]:<6}  {"-":>6}   discarded ({block["discarded"]})')
            continue
        m = block['metrics']
        emit(f'  {block["index"]:>5}  {block["state"]:<6}  {m["frames"]:>6}  '
             f'{m["fps"]:>6.2f}  {m["frame_ms"]:>8.3f}  {m["p95_ms"]:>7.3f}')

    usable_on = sum(1 for b in blocks if b['state'] == 'on' and b['metrics'])
    usable_off = sum(1 for b in blocks if b['state'] == 'off' and b['metrics'])
    if not usable_on or not usable_off:
        emit()
        emit('  RESULT: not enough usable blocks on both sides to compare.')
        if args.report:
            args.report.write_text('\n'.join(lines) + '\n', encoding='utf-8')
        return

    emit()
    emit('  Each ON block is compared with the mean of its neighbouring OFF blocks;')
    emit('  the interval is Student\'s t 95% over those paired differences.')
    emit()

    results = {}
    for key, label, lower_better in METRICS:
        if mean_by_state(blocks, key, 'on') is None:
            continue
        change = paired_change(blocks, key)
        if not change:
            continue
        results[key] = change
        off = mean_by_state(blocks, key, 'off')
        on = mean_by_state(blocks, key, 'on')
        verdict = '(significant)' if change['significant'] else '(within noise)'
        direction = ''
        if change['significant']:
            # A 95% test on eight blocks throws the occasional sub-1% "result".
            # Statistically real, practically nothing: say so rather than
            # labelling it a regression.
            if abs(change['mean']) < NEGLIGIBLE_PCT:
                direction = ' but negligible'
            else:
                improved = (change['mean'] < 0) if lower_better else (change['mean'] > 0)
                direction = ' better' if improved else ' WORSE'
        emit(f'  {label:<24} OFF {off:>9.3f}  ON {on:>9.3f}  '
             f'change {change["mean"]:+6.2f}% +/- {change["ci"]:.2f}%  n={change["n"]} {verdict}{direction}')

    emit()
    fps = results.get('fps')
    if fps and fps['significant']:
        emit(f'  RESULT: optimizations ON changed FPS by {fps["mean"]:+.1f}% '
             f'(95% CI {fps["mean"] - fps["ci"]:+.1f}% to {fps["mean"] + fps["ci"]:+.1f}%).')
    elif fps:
        emit(f'  RESULT: no significant FPS change ({fps["mean"]:+.1f}% +/- {fps["ci"]:.2f}%). '
             'Either the scene is GPU-bound or the run was too noisy.')

    # GPU busy is the control: these optimizations change CPU work, not what is
    # drawn, so it should stay put. If it moved, the scene did.
    gpu = results.get('gpu_busy_ms')
    if gpu:
        if gpu['significant']:
            emit(f'  CONTROL: GPU busy moved {gpu["mean"]:+.1f}% (expected ~0%). The scene probably '
                 'changed during the run, so treat the FPS figure with suspicion and re-run holding still.')
        else:
            emit(f'  CONTROL: GPU busy flat ({gpu["mean"]:+.1f}% +/- {gpu["ci"]:.2f}%), as it should be: '
                 'the same work was drawn either way, so the FPS change is CPU-side.')
    cpu = results.get('cpu_busy_ms')
    if cpu and cpu['significant'] and cpu['mean'] < 0 and fps and not fps['significant']:
        emit(f'  NOTE: CPU busy fell {abs(cpu["mean"]):.1f}% without an FPS gain, so you are GPU-bound here. '
             'The headroom is real; raise settings or resolution to spend it.')

    discarded = [b['index'] for b in blocks if not b['metrics']]
    if discarded:
        emit(f'  {len(discarded)} block(s) discarded: {", ".join(str(i) for i in discarded)}')
    logged = schedule.get('loggedToggles') or []
    if logged and len(logged) < len(raw_blocks):
        emit(f'  WARNING: the module logged {len(logged)} toggles for {len(raw_blocks)} blocks; '
             'some blocks may be mislabelled.')

    if args.report:
        args.report.write_text('\n'.join(lines) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
