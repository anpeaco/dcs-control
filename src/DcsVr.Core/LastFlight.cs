using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DcsVr.Core;

/// <summary>Where the last DCS session left its traces. Every source is optional.</summary>
/// <param name="DcsLog">Saved Games\DCS\Logs\dcs.log: the session's start and end (UTC). The prefetch fix's
/// DcsQvCullPrefetch.log is read from the engine optimizations module next to it (Saved Games\DCS\Scripts\DcsQvCull).</param>
/// <param name="PimaxLogDirectory">%LOCALAPPDATA%\Pimax\runtime with pvr_srv_log_*.txt: frames the headset received.</param>
/// <param name="ManagedProfiles">The app's managed\profiles folder: OFXR's flight logs in each profile's ofxr folder.</param>
/// <param name="BoostStatus">%LOCALAPPDATA%\DcsControl\boost\status.json written by the CPU Boost helper.</param>
/// <param name="DcsBin">DCS's bin folder: Cheeky's CheekyFoveatedDLSS logs.</param>
public sealed record FlightSources(string? DcsLog, string? PimaxLogDirectory = null, string? ManagedProfiles = null, string? BoostStatus = null, string? DcsBin = null);

/// <summary>A summary of the last DCS session, read from logs after DCS exits. Parts without a source are null.</summary>
public sealed record FlightSummary(DateTimeOffset Start, DateTimeOffset End, bool Closed, int? DcsPid, HeadsetFrames? Headset, FramegenShare? Framegen,
    FrameTimes? FrameTime, NeuralState? Dlss5, BoostResult? Boost, PrefetchResult? Prefetch,
    FrameCadence? Pacing = null, FramegenCost? FramegenCost = null, EngineComparison? EngineSwitch = null)
{
    public TimeSpan Duration => End - Start;
}
/// <param name="AverageFps">Frames per second DCS (or OFXR) delivered to the Pimax runtime, averaged over the session.</param>
/// <param name="AtRefreshShare">Share of seconds at 97% of the compositor rate or better.</param>
/// <param name="Missed">Frames the runtime had to show without a new application frame.</param>
public sealed record HeadsetFrames(double AverageFps, double CompositorFps, double AtRefreshShare, long Missed, long Discarded, int Seconds);
/// <summary>Seconds OFXR ran at each frame multiplier (from its once-a-second latency_status), and its switches.</summary>
public sealed record FramegenShare(int Seconds2x, int Seconds3x, int Switches, bool Adaptive)
{
    public double Share3x => Seconds2x + Seconds3x == 0 ? 0 : (double)Seconds3x / (Seconds2x + Seconds3x);
}
/// <summary>DCS frame time from OFXR's adaptive control: each second's p90 of max(CPU, GPU) per DCS frame; the median
/// and p90 of those over the session, in milliseconds.</summary>
public sealed record FrameTimes(double P50Ms, double P90Ms, int Seconds);
/// <param name="State">"ran", "not-started" or "failed".</param>
public sealed record NeuralState(string State, string? Version, int Toggles, bool? OnAtExit, string? Error);
public sealed record BoostResult(string State, int Moved, int Restored, IReadOnlyList<string> Errors);
/// <param name="AverageCallsPerSecond">Average over the 10 s reports with any calls.</param>
/// <param name="SkippedPercent">Share of all calls the fix skipped, weighted by calls.</param>
public sealed record PrefetchResult(double AverageCallsPerSecond, double PeakCallsPerSecond, double SkippedPercent, int Reports);
/// <summary>DCS's frames as OFXR saw them, over the flying seconds of the OFXR log (seconds with at least 10 DCS frames,
/// from the first 5 of them in a row: menus and loading screens are left out).</summary>
/// <param name="RenderedFps">DCS frames per second (its xrWaitFrame calls).</param>
/// <param name="TargetFps">The rate OFXR paced DCS to (1 / the predicted display period it gave DCS), or null.</param>
/// <param name="OnTimeShare">Share of DCS frames that came within 1.5 frame periods of the one before, or null.</param>
/// <param name="HeadsetFps">Frames OFXR passed on to the headset runtime per second (its own xrEndFrame calls), or null.</param>
/// <param name="IntervalP50Ms">Median time between two DCS frames; P95 and P99 likewise. Gaps of a second or more
/// (a pause, a loading screen) are not frames and not counted.</param>
/// <param name="Stutters">DCS frames that took more than twice the median interval: at least one frame slot missed.</param>
/// <param name="WaitP50Ms">Median time DCS waited in xrWaitFrame for its next frame slot: its spare time per frame.</param>
/// <param name="EndFrameP50Ms">Median time DCS spent in xrEndFrame (OFXR takes the frame, and waits for the GPU when it must).</param>
/// <param name="Seconds">Flying seconds measured.</param>
/// <param name="Partial">OFXR's log wrapped at its size limit: only the end of the flight is in it.</param>
public sealed record FrameCadence(double RenderedFps, double? TargetFps, double? OnTimeShare, double? HeadsetFps, double IntervalP50Ms, double IntervalP95Ms,
    double IntervalP99Ms, int Stutters, long Frames, double WaitP50Ms, double WaitP90Ms, double? EndFrameP50Ms, int Seconds, bool Partial);
/// <summary>What frame generation itself cost: the median GPU time of one generated pair (NVIDIA optical flow only;
/// null with FidelityFX), and the most video memory DCS used, with the budget Windows gave it then and the GPU's size
/// (OFXR's once-a-second vram_status, written while "VRAM in the headset panel" is on).</summary>
public sealed record FramegenCost(double? GpuPerPairMs, int? VramPeakMiB, int? VramBudgetMiB, int? GpuMemoryMiB);
/// <summary>The flight's own A/B when the pilot used the engine optimizations' in-flight switch: DCS's frames in the
/// stretches with the optimizations on and off (the first 2 s after each switch left out). The frame figures are null
/// unless both sides have at least 20 s. The scene can differ between the stretches.</summary>
public sealed record EngineComparison(int Switches, double OnSeconds, double OffSeconds, double? OnFrameMs, double? OffFrameMs, double? OnWaitMs, double? OffWaitMs);
/// <summary>The engine optimizations' state over the flight, on OFXR's log timeline (milliseconds since it started).</summary>
public sealed record EngineSwitches(int Count, IReadOnlyList<EngineStretch> Stretches);
public sealed record EngineStretch(double FromMs, double ToMs, bool Off);

/// <summary>Reads <see cref="FlightSummary"/> defensively and cheaply: only the start and tail of dcs.log, only the
/// lines that matter in the other logs, and each parsed file is cached by size and timestamp.</summary>
public static partial class LastFlight
{
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, object? Value)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const long MaxTail = 64L * 1024 * 1024;

    /// <summary>The last session, or null when dcs.log is missing or unreadable (no flight recorded yet).</summary>
    public static FlightSummary? Read(FlightSources sources)
    {
        if (sources.DcsLog is not { } log || Session(log) is not { } session) return null;
        var (start, end, closed) = session;
        var (ofxrPath, ofxrPid, ofxrStart) = FindOfxrLog(sources.ManagedProfiles, start, end);
        var ofxr = ofxrPath is null ? null : ReadOfxr(ofxrPath, ofxrStart is { } at ? ReadEngineSwitches(EngineLog(log), start, end, at) : null);
        var boost = sources.BoostStatus is { } status && File.Exists(status) ? Cached(status, () => ParseBoostStatus(File.ReadAllText(status))) : null;
        if (boost is not null && !(boost.UpdatedAt >= start.AddMinutes(-1) && boost.UpdatedAt <= end.AddMinutes(10))) boost = null;
        var pid = ofxrPid ?? boost?.DcsPid;
        return new(start, end, closed, pid,
            ReadPimax(sources.PimaxLogDirectory, start, end, pid),
            ofxr?.Share, ofxr?.Times,
            ReadCheeky(sources.DcsBin, start, end),
            boost?.Result,
            ReadPrefetch(PrefetchLog(log), start, end),
            ofxr?.Pacing, ofxr?.Cost, ofxr?.Engine);
    }

    // ---- dcs.log ---------------------------------------------------------------------------------------------------

    /// <summary>The session in dcs.log: "=== Log opened UTC …", the last timestamped line, and whether it closed.</summary>
    private static (DateTimeOffset, DateTimeOffset, bool)? Session(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            return Cached(path, () =>
            {
                var head = ReadHead(path, 4096); var tail = ReadTail(path, 64 * 1024);
                return ParseDcsSession(head, tail, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Start from the "Log opened UTC" header, end from the last timestamped line (or the file time).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End, bool Closed)? ParseDcsSession(string head, string tail, DateTimeOffset written)
    {
        var opened = OpenedRegex().Match(head);
        if (!opened.Success || !DateTime.TryParseExact(opened.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startUtc)) return null;
        var start = new DateTimeOffset(startUtc, TimeSpan.Zero);
        var end = written;
        foreach (Match m in StampRegex().Matches(tail))
            if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) end = new DateTimeOffset(t, TimeSpan.Zero);
        if (end < start) end = start;
        return (start, end, tail.Contains("=== Log closed.", StringComparison.Ordinal));
    }
    [GeneratedRegex(@"=== Log opened UTC (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)")] private static partial Regex OpenedRegex();
    [GeneratedRegex(@"(?m)^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) ")] private static partial Regex StampRegex();

    // ---- Pimax runtime -------------------------------------------------------------------------------------------

    private static HeadsetFrames? ReadPimax(string? directory, DateTimeOffset start, DateTimeOffset end, int? pid)
    {
        try
        {
            if (directory is null || !Directory.Exists(directory)) return null;
            // pvr_srv_log_YY-MM-DD-HH-MM-SS.txt starts at that local time; the one(s) open during the session.
            var files = Directory.EnumerateFiles(directory, "pvr_srv_log_*.txt").Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTimeUtc >= start.UtcDateTime && PimaxFileStart(f.Name) is { } s && s <= end).OrderBy(f => f.Name).ToArray();
            var lines = new List<string>();
            foreach (var file in files.TakeLast(2))
                lines.AddRange(Cached(file.FullName, () => ReadLines(file.FullName, MaxTail, l => l.Contains("rendering fps:", StringComparison.Ordinal))) ?? []);
            return ParsePimax(lines, start, end, pid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    private static DateTimeOffset? PimaxFileStart(string name)
    {
        var m = PimaxFileRegex().Match(name);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? new DateTimeOffset(t) : null;
    }
    [GeneratedRegex(@"^pvr_srv_log_(\d\d-\d\d-\d\d-\d\d-\d\d-\d\d)\.txt$", RegexOptions.IgnoreCase)] private static partial Regex PimaxFileRegex();

    /// <summary>Pimax's once-a-second "rendering fps:(a:…,c:…) Missed:(a:…) … Discard:(a:…)" lines of the DCS process
    /// (the number after [PSRV]; any non-zero process when the PID is unknown) within the session, skipping seconds
    /// below 1 FPS (loading). Times are local.</summary>
    public static HeadsetFrames? ParsePimax(IEnumerable<string> lines, DateTimeOffset start, DateTimeOffset end, int? pid)
    {
        double app = 0, compositor = 0; long missed = 0, discarded = 0; int seconds = 0, atRefresh = 0;
        foreach (var line in lines)
        {
            var m = PimaxLineRegex().Match(line);
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)) continue;
            var at = new DateTimeOffset(local);
            if (at < start || at > end) continue;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var process)) continue;
            if (pid is { } p ? process != p : process == 0) continue;
            if (Number(m.Groups[3].Value) is not { } a || Number(m.Groups[4].Value) is not { } c
                || !long.TryParse(m.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var missedNow)
                || !long.TryParse(m.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var discardedNow)) continue;
            // Loading screens submit next to nothing; those seconds are not flight.
            if (a < 1) continue;
            seconds++; app += a; compositor += c;
            if (c > 0 && a >= c * .97) atRefresh++;
            missed += missedNow; discarded += discardedNow;
        }
        return seconds == 0 ? null : new(app / seconds, compositor / seconds, (double)atRefresh / seconds, missed, discarded, seconds);
    }
    [GeneratedRegex(@"^\[(\d\d-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\].*?\[PSRV\]\s+(\d+)\s+\w+\s+rendering fps:\(a:([\d.]+),c:([\d.]+)\)\s+Missed:\(a:(\d+)[^)]*\).*?Discard:\(a:(\d+)\)")]
    private static partial Regex PimaxLineRegex();

    // ---- OFXR flight log -------------------------------------------------------------------------------------------

    /// <summary>ofxr-bridge-flight-YYYYMMDD-HHMMSS-pidN.log (local start time) started during the session, its DCS PID
    /// and that start time: every line's ms= counts from it.</summary>
    private static (string?, int?, DateTime?) FindOfxrLog(string? profiles, DateTimeOffset start, DateTimeOffset end)
    {
        try
        {
            if (profiles is null || !Directory.Exists(profiles)) return (null, null, null);
            var best = Directory.EnumerateDirectories(profiles).Select(d => Path.Combine(d, "ofxr")).Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "ofxr-bridge-flight-*.log"))
                .Select(f => (Path: f, Match: OfxrFileRegex().Match(Path.GetFileName(f))))
                .Select(x => (x.Path, x.Match, At: x.Match.Success && DateTime.TryParseExact(x.Match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : (DateTime?)null))
                .Where(x => x.At is { } t && new DateTimeOffset(t) >= start.AddMinutes(-2) && new DateTimeOffset(t) <= end)
                .OrderBy(x => x.Match.Groups[1].Value, StringComparer.Ordinal).LastOrDefault();
            return best.Path is null ? (null, null, null) : (best.Path, int.TryParse(best.Match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null, best.At);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null, null); }
    }
    [GeneratedRegex(@"^ofxr-bridge-flight-(\d{8}-\d{6})-pid(\d+)\.log$", RegexOptions.IgnoreCase)] private static partial Regex OfxrFileRegex();

    /// <summary>The app writes max_file_mb=256: OFXR starts its log again from empty when it reaches that.</summary>
    private const long MaxOfxr = 300L * 1024 * 1024;

    public sealed record OfxrResult(FramegenShare? Share, FrameTimes? Times, FrameCadence? Pacing = null, FramegenCost? Cost = null, EngineComparison? Engine = null);

    /// <summary>One streaming pass over OFXR's flight log, cached by size and timestamp; the engine switches come from
    /// another file and are applied to the cached frames each time.</summary>
    private static OfxrResult? ReadOfxr(string path, EngineSwitches? engine)
    {
        try { return Cached(path, () => OfxrScan.Of(StreamLines(path, MaxOfxr)))?.Summarize(engine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>OFXR's flight log ("seq=… ms=… tid=… phase=B|E|I op=… result=… dur_us=… a=… b=… c=…"):
    /// <list type="bullet">
    /// <item>latency_status (once a second: result = multiplier, +10 under adaptive control; a = that second's p90 of
    /// max(CPU, GPU) per DCS frame in microseconds) and adaptive_switch (a multiplier change, or adaptive turned on/off);</item>
    /// <item>app_wait_frame E: one per DCS frame, dur_us = how long DCS waited for its frame slot, b = the predicted
    /// display period (ns) OFXR gave it; app_end_frame E: dur_us = DCS's time in xrEndFrame;</item>
    /// <item>internal_end_frame E: a frame OFXR passed on to the headset runtime; nvidia_gpu_total: one generated pair's
    /// GPU time (dur_us); vram_status result=1: a = this process's resident MiB (bits 0–31) and its budget (32–63),
    /// b = every process's use (0–31) and the GPU's size (32–63).</item>
    /// </list>
    /// <paramref name="engine"/>: the in-flight engine switch on the same timeline (<see cref="ParseEngineSwitches"/>).</summary>
    public static OfxrResult? ParseOfxr(IEnumerable<string>? lines, EngineSwitches? engine = null) => lines is null ? null : OfxrScan.Of(lines).Summarize(engine);
    private static double Percentile(List<double> sorted, double q) => Math.Round(Quantile(sorted, q), 1);
    private static double Quantile(List<double> sorted, double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>What one pass over an OFXR flight log keeps: the once-a-second figures, and per DCS frame its time and
    /// wait, frames per second in and out, xrEndFrame times, generated pairs' GPU time and the video memory peak.</summary>
    private sealed class OfxrScan
    {
        private int _two, _three, _switches; private bool _adaptive, _first = true, _wrapped;
        private readonly List<double> _latency = [], _frameMs = [], _waitUs = [], _periodNs = [], _endMs = [], _endUs = [], _gpuUs = [];
        private readonly List<int> _appPerSecond = [], _outPerSecond = [];
        private long _vramPeak; private int? _vramBudget, _gpuMemory;
        /// <summary>DCS frames in a second for it to count as flying (loading screens and menus run far slower).</summary>
        private const int FlyingFps = 10;

        public static OfxrScan Of(IEnumerable<string> lines)
        {
            var scan = new OfxrScan();
            foreach (var line in lines) scan.Add(line);
            return scan;
        }

        private void Add(string text)
        {
            var line = text.AsSpan();
            if (_first && line.StartsWith("seq=", StringComparison.Ordinal))
            {
                // A log that starts again at its size limit no longer begins with the logger's own first line (seq=1).
                _first = false;
                var space = line.IndexOf(' ');
                _wrapped = !(space > 4 && line[4..space] is "1");
            }
            if (!Field(line, " op=", out var op)) return;
            switch (op)
            {
                case "latency_status" or "adaptive_switch": Once(text); return;
                case "app_wait_frame":
                    if (Ended(line) && Real(line, " ms=") is { } ms && Whole(line, " dur_us=") is { } wait)
                    {
                        _frameMs.Add(ms); _waitUs.Add(wait); _periodNs.Add(Whole(line, " b=") ?? 0);
                        Count(_appPerSecond, ms);
                    }
                    return;
                case "app_end_frame":
                    if (Ended(line) && Real(line, " ms=") is { } endMs && Whole(line, " dur_us=") is { } end) { _endMs.Add(endMs); _endUs.Add(end); }
                    return;
                case "internal_end_frame":
                    if (Ended(line) && Real(line, " ms=") is { } outMs) Count(_outPerSecond, outMs);
                    return;
                case "nvidia_gpu_total":
                    if (Field(line, " result=", out var ok) && ok is "0" && Whole(line, " dur_us=") is { } gpu and > 0 and < 1_000_000) _gpuUs.Add(gpu);
                    return;
                case "vram_status":
                    if (Field(line, " result=", out var kind) && kind is "1" && Whole(line, " a=") is { } a && Whole(line, " b=") is { } b
                        && (long)(a & 0xFFFFFFFF) is var resident and > 0 && resident > _vramPeak)
                    {
                        _vramPeak = resident; _vramBudget = (int)Math.Min(a >> 32, int.MaxValue); _gpuMemory = (int)Math.Min(b >> 32, int.MaxValue);
                    }
                    return;
            }
        }

        private void Once(string line)
        {
            var m = OfxrLineRegex().Match(line);
            if (!m.Success) return;
            if (!long.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
                || !ulong.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                || !ulong.TryParse(m.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return;
            if (m.Groups[1].Value == "latency_status")
            {
                _adaptive |= result >= 10;
                if (result % 10 == 2) _two++; else if (result % 10 == 3) _three++;
                if (a > 0 && a < 1_000_000) _latency.Add(a / 1000.0);
            }
            // Reason 5 is adaptive control turned on or off; the others change the multiplier between 2 and 3.
            else if (result != 5 && a != b && a is 2 or 3 && b is 2 or 3) _switches++;
        }

        public OfxrResult? Summarize(EngineSwitches? engine)
        {
            var share = _two + _three == 0 ? null : new FramegenShare(_two, _three, _switches, _adaptive);
            var latency = _latency.Order().ToList();
            var times = latency.Count == 0 ? null : new FrameTimes(Percentile(latency, .5), Percentile(latency, .9), latency.Count);
            var (pacing, comparison) = Pacing(engine);
            var gpu = _gpuUs.Order().ToList();
            var cost = gpu.Count == 0 && _vramPeak == 0 ? null
                : new FramegenCost(gpu.Count == 0 ? null : Math.Round(Quantile(gpu, .5) / 1000, 2), _vramPeak == 0 ? null : (int)Math.Min(_vramPeak, int.MaxValue), _vramBudget, _gpuMemory);
            return share is null && times is null && pacing is null && cost is null ? null : new(share, times, pacing, cost, comparison);
        }

        private (FrameCadence?, EngineComparison?) Pacing(EngineSwitches? engine)
        {
            // Flying: from the first 5 seconds in a row with at least FlyingFps DCS frames, every such second.
            int first = -1;
            for (int s = 0, run = 0; s < _appPerSecond.Count && first < 0; s++)
                if ((run = _appPerSecond[s] >= FlyingFps ? run + 1 : 0) == 5) first = s - 4;
            if (first < 0) return (null, null);
            bool Flying(double ms) => (int)(ms / 1000) is var s && s >= first && s < _appPerSecond.Count && _appPerSecond[s] >= FlyingFps;
            var seconds = 0; long output = 0;
            for (var s = first; s < _appPerSecond.Count; s++)
                if (_appPerSecond[s] >= FlyingFps) { seconds++; if (s < _outPerSecond.Count) output += _outPerSecond[s]; }
            long frames = 0; var intervals = new List<double>(); var waits = new List<double>(); var periods = new List<double>();
            var on = new Side(); var off = new Side(); var stretch = 0;
            for (var i = 0; i < _frameMs.Count; i++)
            {
                var ms = _frameMs[i];
                if (!Flying(ms)) continue;
                frames++; waits.Add(_waitUs[i] / 1000);
                if (_periodNs[i] is > 0 and < 1e9) periods.Add(_periodNs[i] / 1e6);
                // A gap of a second or more is a pause or a loading screen, not a frame.
                if (i == 0 || !Flying(_frameMs[i - 1])) continue;
                var interval = ms - _frameMs[i - 1];
                if (!(interval > 0 && interval < 1000)) continue;
                intervals.Add(interval);
                if (engine is null) continue;
                while (stretch < engine.Stretches.Count && engine.Stretches[stretch].ToMs <= ms) stretch++;
                if (stretch < engine.Stretches.Count && engine.Stretches[stretch] is { } e && ms >= e.FromMs + 2000)
                    (e.Off ? off : on).Add(interval, _waitUs[i] / 1000);
            }
            if (intervals.Count == 0) return (null, null);
            intervals.Sort(); waits.Sort(); periods.Sort();
            var median = Quantile(intervals, .5);
            double? period = periods.Count == 0 ? null : Quantile(periods, .5);
            var ends = _endMs.Select((ms, i) => (ms, i)).Where(x => Flying(x.ms)).Select(x => _endUs[x.i] / 1000).Order().ToList();
            var pacing = new FrameCadence(Math.Round((double)frames / seconds, 1), period is { } p ? Math.Round(1000 / p, 1) : null,
                period is { } q ? Math.Round((double)intervals.Count(d => d <= q * 1.5) / intervals.Count, 3) : null,
                output == 0 ? null : Math.Round((double)output / seconds, 1),
                Percentile(intervals, .5), Percentile(intervals, .95), Percentile(intervals, .99), intervals.Count(d => d > 2 * median), frames,
                Percentile(waits, .5), Percentile(waits, .9), ends.Count == 0 ? null : Percentile(ends, .5), seconds, _wrapped);
            EngineComparison? comparison = null;
            if (engine is not null)
            {
                var enough = on.Seconds >= 20 && off.Seconds >= 20;
                comparison = new(engine.Count, Math.Round(on.Seconds), Math.Round(off.Seconds), enough ? on.Frame : null, enough ? off.Frame : null, enough ? on.Wait : null, enough ? off.Wait : null);
            }
            return (pacing, comparison);
        }

        /// <summary>DCS's frames in the stretches with the engine optimizations on, or off.</summary>
        private sealed class Side
        {
            private readonly List<double> _intervals = [], _waits = [];
            public double Seconds { get; private set; }
            public void Add(double interval, double wait) { _intervals.Add(interval); _waits.Add(wait); Seconds += interval / 1000; }
            public double? Frame => _intervals.Count == 0 ? null : Percentile(_intervals.Order().ToList(), .5);
            public double? Wait => _waits.Count == 0 ? null : Percentile(_waits.Order().ToList(), .5);
        }

        private static void Count(List<int> perSecond, double ms)
        {
            // A week of seconds at most: a line claiming more is not trusted.
            if (ms is < 0 or > 7 * 24 * 3600e3) return;
            var s = (int)(ms / 1000);
            while (perSecond.Count <= s) perSecond.Add(0);
            perSecond[s]++;
        }
        private static bool Ended(ReadOnlySpan<char> line) => Field(line, " phase=", out var phase) && phase is "E" && Field(line, " result=", out var result) && result is "0";
        /// <summary>The value after <paramref name="key"/> (" name=") up to the next space.</summary>
        private static bool Field(ReadOnlySpan<char> line, string key, out ReadOnlySpan<char> value)
        {
            var at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) { value = default; return false; }
            var rest = line[(at + key.Length)..];
            var space = rest.IndexOf(' ');
            value = space < 0 ? rest : rest[..space];
            return true;
        }
        private static double? Real(ReadOnlySpan<char> line, string key) =>
            Field(line, key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
        private static ulong? Whole(ReadOnlySpan<char> line, string key) =>
            Field(line, key, out var v) && ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
    [GeneratedRegex(@"\bop=(latency_status|adaptive_switch) result=(-?\d+) dur_us=\d+ a=(\d+) b=(\d+)")] private static partial Regex OfxrLineRegex();

    // ---- Engine optimizations' in-flight switch -------------------------------------------------------------------

    /// <summary>The engine optimizations module's DcsQvCull.log next to the prefetch fix's log (rewritten at every DCS start).</summary>
    public static string? EngineLog(string dcsLog) => PrefetchLog(dcsLog) is { } prefetch ? Path.Combine(Path.GetDirectoryName(prefetch)!, "DcsQvCull.log") : null;

    private static EngineSwitches? ReadEngineSwitches(string? path, DateTimeOffset start, DateTimeOffset end, DateTime ofxrStart)
    {
        try
        {
            if (path is null || !File.Exists(path)) return null;
            var written = File.GetLastWriteTimeUtc(path);
            if (written < start.UtcDateTime.AddMinutes(-1) || written > end.UtcDateTime.AddMinutes(10)) return null;
            var lines = Cached(path, () => ReadLines(path, 64L * 1024 * 1024, IsEngineLine));
            return lines is null ? null : ParseEngineSwitches(lines, start.ToLocalTime().DateTime, ofxrStart);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    private static bool IsEngineLine(string line) => line.Contains("hotkey: engine optimizations", StringComparison.Ordinal) || line.Contains("loader started", StringComparison.Ordinal)
        || line.Contains("bench: ", StringComparison.Ordinal) || line.Contains("==== ", StringComparison.Ordinal) || line.Contains("report written to report_", StringComparison.Ordinal);

    /// <summary>"HH:mm:ss.fff hotkey: engine optimizations OFF|ON" (and "… back ON (ini changed)") in the module's log,
    /// which holds one DCS session with the time of day only: the day is the session's (<paramref name="sessionStartLocal"/>),
    /// the next one after midnight. The optimizations are on from the log's first line. The module's test suite and A/B
    /// benchmark switch them too, so their windows are left out. Times become milliseconds since
    /// <paramref name="ofxrStartLocal"/>, OFXR's log timeline. Null when the pilot never used the switch.</summary>
    public static EngineSwitches? ParseEngineSwitches(IEnumerable<string> lines, DateTime sessionStartLocal, DateTime ofxrStartLocal)
    {
        var stretches = new List<EngineStretch>(); int count = 0; bool off = false, suite = false, bench = false; double? from = null;
        DateTime? previous = null;
        foreach (var line in lines)
        {
            if (line.Length < 13 || line[12] != ' ' || !TimeSpan.TryParseExact(line.AsSpan(0, 12), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var time)) continue;
            var at = (previous?.Date ?? sessionStartLocal.Date) + time;
            if (previous is null && at - sessionStartLocal > TimeSpan.FromHours(12)) at = at.AddDays(-1);
            else if (previous is null && sessionStartLocal - at > TimeSpan.FromHours(12)) at = at.AddDays(1);
            else if (previous is { } p && at < p - TimeSpan.FromHours(1)) at = at.AddDays(1);
            previous = at;
            var ms = (at - ofxrStartLocal).TotalMilliseconds;
            from ??= ms;
            var text = line.AsSpan(13);
            bool? nextOff = null, nextSuite = null, nextBench = null;
            if (text.StartsWith("hotkey: engine optimizations OFF", StringComparison.Ordinal)) { nextOff = true; count++; }
            else if (text.StartsWith("hotkey: engine optimizations back ON", StringComparison.Ordinal)) nextOff = false;
            else if (text.StartsWith("hotkey: engine optimizations ON", StringComparison.Ordinal)) { nextOff = false; count++; }
            else if (text.StartsWith("==== DcsQvCull test suite", StringComparison.Ordinal)) nextSuite = true;
            else if (text.StartsWith("==== suite aborted", StringComparison.Ordinal) || text.Contains("report written to report_", StringComparison.Ordinal)) nextSuite = false;
            else if (text.StartsWith("bench: start", StringComparison.Ordinal)) nextBench = true;
            else if (text.StartsWith("bench: TSC", StringComparison.Ordinal) || text.StartsWith("bench: aborted", StringComparison.Ordinal)) nextBench = false;
            else continue;
            if (!suite && !bench && ms > from) stretches.Add(new(from.Value, ms, off));
            from = ms; off = nextOff ?? off; suite = nextSuite ?? suite; bench = nextBench ?? bench;
        }
        if (count == 0 || from is null) return null;
        if (!suite && !bench) stretches.Add(new(from.Value, double.PositiveInfinity, off));
        return new(count, stretches);
    }

    // ---- Cheeky (DLSS 5) -------------------------------------------------------------------------------------------

    private static NeuralState? ReadCheeky(string? bin, DateTimeOffset start, DateTimeOffset end)
    {
        try
        {
            if (bin is null) return null;
            var folder = Path.Combine(bin, "CheekyFoveatedDLSS");
            var standalone = Path.Combine(folder, "CheekyFoveatedDLSS-Standalone.log"); var host = Path.Combine(folder, "CheekyFoveatedDLSS-Host.log");
            bool During(string f) => File.Exists(f) && File.GetLastWriteTimeUtc(f) >= start.UtcDateTime.AddMinutes(-1) && File.GetLastWriteTimeUtc(f) <= end.UtcDateTime.AddMinutes(10);
            if (!During(standalone)) return null;
            // Every session appends to these logs, with the time of day only: the startup markers are kept so only the
            // last session's lines count.
            var runtime = Cached(standalone, () => ReadLines(standalone, 16L * 1024 * 1024, l => IsRuntimeStart(l) || (l.Contains("DLSS-NR", StringComparison.Ordinal) && (l.Contains("runtime initialized", StringComparison.Ordinal) || NeuralFailedRegex().IsMatch(l) || NeuralErrorRegex().IsMatch(l)))));
            var toggles = During(host) ? Cached(host, () => ReadLines(host, 4L * 1024 * 1024, l => IsHostStart(l) || l.Contains("hotkey toggled", StringComparison.Ordinal))) : null;
            return ParseCheeky(runtime ?? [], toggles ?? []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"DLSS-NR 310.8 feature-18 runtime initialized", DLSS-NR failures, and "DLSS-NR hotkey toggled enabled=yes|no",
    /// counted only after the last startup marker of each log ("Cheeky 0.5.3 Standalone initializing; …" in the runtime
    /// log, "Initializing standalone host" in the host log): every DCS session appends to both files.</summary>
    public static NeuralState ParseCheeky(IEnumerable<string> runtime, IEnumerable<string> host)
    {
        string? version = null, error = null;
        runtime = LastSession(runtime, IsRuntimeStart); host = LastSession(host, IsHostStart);
        foreach (var line in runtime)
        {
            if (NeuralInitRegex().Match(line) is { Success: true } init) version ??= init.Groups[1].Value;
            else if (error is null && line.Contains("DLSS-NR", StringComparison.Ordinal) && (NeuralFailedRegex().IsMatch(line) || NeuralErrorRegex().IsMatch(line)))
                error = line.Length > 40 && line.IndexOf("DLSS-NR", StringComparison.Ordinal) is var at and >= 0 ? line[at..].Trim() : line.Trim();
        }
        int toggles = 0; bool? on = null;
        foreach (var line in host)
            if (line.Contains("hotkey toggled enabled=", StringComparison.Ordinal)) { toggles++; on = line.TrimEnd().EndsWith("enabled=yes", StringComparison.Ordinal); }
        if (error is { Length: > 160 }) error = error[..157] + "…";
        return new(version is not null && error is null ? "ran" : error is not null ? "failed" : "not-started", version, toggles, on, error);
    }
    /// <summary>Cheeky's line when the DLSS 5 runtime starts; group 1 is the runtime version. Also read by <see cref="Diagnostics"/>.</summary>
    [GeneratedRegex(@"DLSS-NR (\S+) feature-\d+ runtime initialized")] internal static partial Regex NeuralInitRegex();
    /// <summary>The lines after the last line that matches <paramref name="start"/>; all lines when none does (a log
    /// from before the markers, or one whose start is beyond the read limit).</summary>
    private static List<string> LastSession(IEnumerable<string> lines, Func<string, bool> start)
    {
        var list = lines as List<string> ?? lines.ToList();
        var last = list.FindLastIndex(l => start(l));
        return last < 0 ? list : list.GetRange(last + 1, list.Count - last - 1);
    }
    private static bool IsRuntimeStart(string line) => line.Contains("Cheeky ", StringComparison.Ordinal) && line.Contains(" initializing; runtime remains resident", StringComparison.Ordinal);
    private static bool IsHostStart(string line) => line.Contains("Initializing standalone host", StringComparison.Ordinal) || line.Contains("Initializing OptiScaler host", StringComparison.Ordinal);
    [GeneratedRegex(@"\berror=(?!0\b)-?\d+")] private static partial Regex NeuralErrorRegex();
    [GeneratedRegex(@"\b(failed|failure)\b", RegexOptions.IgnoreCase)] private static partial Regex NeuralFailedRegex();

    // ---- CPU Boost ---------------------------------------------------------------------------------------------------

    public sealed record BoostStatus(DateTimeOffset UpdatedAt, int? DcsPid, BoostResult Result);

    /// <summary>status.json of the CPU Boost helper: state, what it changed and restored, its errors.</summary>
    public static BoostStatus? ParseBoostStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("updatedAt", out var updated) || !updated.TryGetDateTimeOffset(out var at)) return null;
            static string[] List(JsonElement root, string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Select(x => x.GetString() ?? "")] : [];
            var changed = List(r, "changed");
            var restored = r.TryGetProperty("restored", out var re) && re.ValueKind == JsonValueKind.Number && re.TryGetInt32(out var count) ? count : 0;
            var state = r.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            var pid = r.TryGetProperty("dcsPid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var number) ? number : (int?)null;
            // DCS's own priority and affinity are listed first as "DCS (PID n): …"; the rest are other processes moved.
            var moved = changed.Count(c => !c.StartsWith("DCS (PID", StringComparison.Ordinal));
            return new(at, pid, new(state, moved, restored, List(r, "errors")));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // ---- Prefetch fix ------------------------------------------------------------------------------------------------

    /// <summary>The engine optimizations module's DcsQvCullPrefetch.log for the Saved Games folder of
    /// <paramref name="dcsLog"/> (Saved Games\DCS*\Logs\dcs.log → Saved Games\DCS*\Scripts\DcsQvCull).</summary>
    public static string? PrefetchLog(string dcsLog) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dcsLog))) is { } savedGames ? Path.Combine(savedGames, "Scripts", "DcsQvCull", "DcsQvCullPrefetch.log") : null;

    private static PrefetchResult? ReadPrefetch(string? path, DateTimeOffset start, DateTimeOffset end)
    {
        try { return path is not null && File.Exists(path) ? ParsePrefetch(Cached(path, () => ReadLines(path, 8L * 1024 * 1024, l => l.Contains("calls/s=", StringComparison.Ordinal))) ?? [], start, end) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"2026-10-05 19:41:45 calls/s=22239 skipped=84.2% …" every 10 s, local time, within the session.</summary>
    public static PrefetchResult? ParsePrefetch(IEnumerable<string> lines, DateTimeOffset start, DateTimeOffset end)
    {
        double total = 0, skipped = 0, peak = 0; int reports = 0, active = 0;
        foreach (var line in lines)
        {
            var m = PrefetchRegex().Match(line);
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)) continue;
            var at = new DateTimeOffset(local);
            if (at < start.AddMinutes(-1) || at > end.AddMinutes(1)) continue;
            if (Number(m.Groups[2].Value) is not { } calls || Number(m.Groups[3].Value) is not { } share) continue;
            reports++;
            if (calls <= 0) continue;
            active++; total += calls; skipped += calls * share / 100; peak = Math.Max(peak, calls);
        }
        return reports == 0 ? null : new(active == 0 ? 0 : total / active, peak, total == 0 ? 0 : Math.Round(skipped / total * 100, 1), reports);
    }
    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) calls/s=(\d+(?:\.\d+)?) skipped=(\d+(?:\.\d+)?)%")] private static partial Regex PrefetchRegex();

    // ---- Reading ---------------------------------------------------------------------------------------------------

    /// <summary>A finite number, or null: log lines are never trusted to be well formed.</summary>
    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>The parsed value for this file's size and timestamp; parsed again only when the file changes.</summary>
    private static T? Cached<T>(string path, Func<T?> parse)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return default;
        if (Cache.TryGetValue(path + "|" + typeof(T).Name, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc) return (T?)hit.Value;
        var value = parse();
        Cache[path + "|" + typeof(T).Name] = (info.Length, info.LastWriteTimeUtc, value);
        return value;
    }

    /// <summary>Lines of the last <paramref name="maxBytes"/> of a file that pass <paramref name="keep"/>; the file may
    /// still be written by another process.</summary>
    private static List<string> ReadLines(string path, long maxBytes, Func<string, bool> keep)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        var skipFirst = stream.Length > maxBytes;
        if (skipFirst) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16);
        if (skipFirst) reader.ReadLine();
        while (reader.ReadLine() is { } line) if (keep(line)) lines.Add(line);
        return lines;
    }
    /// <summary>The lines of the last <paramref name="maxBytes"/> of a file one at a time, never all held at once (OFXR's
    /// log reaches hundreds of MB); the file may still be written by another process.</summary>
    private static IEnumerable<string> StreamLines(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        var skipFirst = stream.Length > maxBytes;
        if (skipFirst) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16);
        if (skipFirst) reader.ReadLine();
        while (reader.ReadLine() is { } line) yield return line;
    }
    private static string ReadHead(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(bytes, stream.Length)]; stream.ReadExactly(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
    private static string ReadTail(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(bytes, stream.Length); stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length]; stream.ReadExactly(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
}
