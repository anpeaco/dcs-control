// FlightTrace: what DCS's threads were doing in given windows of a flight, from a WPR trace (CPU profile) and OFXR's
// flight log.
//
//   FlightTrace --etl <trace.etl> --ofxr <ofxr-bridge-flight-*.log> --windows <windows.json> --out <etw.json> [--symbols]
//
// windows.json comes from scripts/analyze-flight-spikes.py: { "pid": n, "threads": { "main": tid, ... },
// "windows": [ { "id": "...", "start_ms": x, "end_ms": y } ] }, times on OFXR's log clock (ms from the log's start).
// OFXR's clock is the performance counter the trace runs on, so one offset maps one onto the other. It is found from
// where OFXR's threads were running: a thread writes a record only while it runs, so at the right offset every record
// of a mostly idle thread (the presenter, the render thread) lands inside one of that thread's time slices, and at a
// wrong one only as often as the thread happens to be busy. The search is centred on the start time in the log's name.
//
// --symbols loads symbols (_NT_SYMBOL_PATH, or Microsoft's and NVIDIA's servers plus this repo's native build output):
// slow the first time; without it every figure is per module.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("usage: FlightTrace --etl <trace.etl> --ofxr <ofxr log> --windows <windows.json> --out <etw.json> [--symbols]");
    return 2;
}

// A trace or log that cannot answer the question is a message, not a stack trace.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is not InvalidDataException x) return;
    Console.Error.WriteLine("FlightTrace: " + x.Message);
    Environment.Exit(1);
};

var request = JsonSerializer.Deserialize<WindowRequest>(File.ReadAllText(options.Windows), Json.Options)
    ?? throw new InvalidDataException("windows.json is empty");
var roles = request.Threads.ToDictionary(kv => kv.Value, kv => kv.Key);
var ofxrStart = OfxrStart(options.Ofxr);
var records = ReadOfxrRecords(options.Ofxr, roles.Keys.ToHashSet());
Console.WriteLine($"OFXR: {records.Count} records on {string.Join(", ", request.Threads.Select(kv => $"{kv.Key} #{kv.Value}"))}");

if (options.Symbols && Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH") is null)
{
    var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DcsControl", "symbols");
    var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var native = Path.Combine(repo, "artifacts", "native");
    var local = Directory.Exists(native) ? Directory.EnumerateFiles(native, "*.pdb", SearchOption.AllDirectories).Select(Path.GetDirectoryName).Distinct() : [];
    Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", string.Join(";", local.Append(
        $"srv*{cache}*https://msdl.microsoft.com/download/symbols").Append($"srv*{cache}*https://driver-symbols.nvidia.com/")));
}

using var trace = TraceProcessor.Create(options.Etl, new TraceProcessorSettings { AllowLostEvents = true, AllowTimeInversion = true });
var pendingSamples = trace.UseCpuSamplingData();
var pendingScheduling = trace.UseCpuSchedulingData();
var pendingInterrupts = trace.UseInterruptHandlingData();
var pendingSymbols = options.Symbols ? trace.UseSymbols() : null;
trace.Process();
if (pendingSymbols is not null) await pendingSymbols.Result.LoadSymbolsForConsoleAsync(SymCachePath.Automatic);

var scheduling = pendingScheduling.Result;
var slices = scheduling.CpuTimeSlices;
if (slices.Count == 0) throw new InvalidDataException("the trace has no context switches: record it with the CPU profile (scripts/record-flight-trace.ps1)");

// The trace's zero on the wall clock, for the search's centre.
var first = slices[0];
var traceZero = first.StartTime.DateTimeOffset - TimeSpan.FromTicks(Ns(first.StartTime) / 100);

// Time slices: every process, for CPU per process; DCS's own per thread, for running time and the alignment.
var all = new SliceIndex(slices.Select(s => (Ns(s.StartTime), Ns(s.StopTime), s.Process?.Id ?? -1, s.Thread?.Id ?? -1)));
var dcsThreads = slices.Where(s => s.Process?.Id == request.Pid && s.Thread is not null)
    .GroupBy(s => s.Thread!.Id)
    .ToDictionary(g => g.Key, g => new Runs(g.Select(s => (Ns(s.StartTime), Ns(s.StopTime)))));
if (dcsThreads.Count == 0) throw new InvalidDataException($"DCS (pid {request.Pid}) does not appear in the trace: was it recorded during this flight?");
var names = slices.Where(s => s.Process?.Id is not null && s.Thread is not null)
    .GroupBy(s => s.Thread!.Id).ToDictionary(g => g.Key, g => g.First().Thread!.Name ?? "");
var processNames = slices.Where(s => s.Process is not null).GroupBy(s => s.Process!.Id).ToDictionary(g => g.Key, g => g.First().Process!.ImageName ?? "?");

var alignment = Align(records, dcsThreads, (ofxrStart - traceZero).Ticks * 100);
Console.WriteLine($"alignment: OFXR + {alignment.OffsetNs / 1e6:F3} ms = trace time; {alignment.Score:P1} of {alignment.Records} records inside their thread's slices (next best offset {alignment.RunnerUp:P1})");
if (alignment.Score < 0.8) Console.WriteLine("warning: alignment is weak; figures may belong to the wrong frames");

// CPU samples. For DCS, each with the module it was in and the first caller outside Windows' own libraries; for
// everything else only the process.
var samples = pendingSamples.Result.Samples;
var strings = new StringTable();
var leafFirst = samples.Take(1000).Where(s => s.Stack?.Frames.Count > 0).Select(s => s.Stack!.Frames[0].Address == s.InstructionPointer).DefaultIfEmpty(true)
    .GroupBy(x => x).MaxBy(g => g.Count())!.Key;
var dcsSamples = new List<Sample>();
var otherSamples = new List<(long Ns, int Pid, double Ms)>();
foreach (var s in samples)
{
    var pid = s.Process?.Id ?? -1;
    var ms = (double)s.Weight.TotalMilliseconds;
    if (pid != request.Pid) { otherSamples.Add((Ns(s.Timestamp), pid, ms)); continue; }
    var inModule = Frame(s.Image?.FileName, options.Symbols ? s.TopStackFrame.Symbol?.FunctionName : null);
    var caller = "?";
    var frames = s.Stack?.Frames;
    if (frames is not null)
    {
        for (var k = 0; k < frames.Count; k++)
        {
            var f = frames[leafFirst ? k : frames.Count - 1 - k];
            var image = f.Image?.FileName;
            if (image is null || WindowsLibraries.Contains(image)) continue;
            caller = Frame(image, options.Symbols ? f.Symbol?.FunctionName : null);
            break;
        }
    }
    dcsSamples.Add(new Sample(Ns(s.Timestamp), s.Thread?.Id ?? -1, ms, strings.Id(inModule), strings.Id(caller)));
}
dcsSamples.Sort((a, b) => a.Ns.CompareTo(b.Ns));
otherSamples.Sort((a, b) => a.Ns.CompareTo(b.Ns));
Console.WriteLine($"samples: {dcsSamples.Count} in DCS, {otherSamples.Count} elsewhere");

// The waits of the role threads: from each switch-in, how long it waited, who readied it and from where.
var waits = new List<Wait>();
foreach (var a in scheduling.ThreadActivity)
{
    if (a.Process?.Id != request.Pid || a.Thread is null || !roles.ContainsKey(a.Thread.Id) || a.WaitingDuration is not { } waiting) continue;
    var switchIn = Ns(a.StartTime);
    var ready = a.ReadyDuration is { } r ? r.Nanoseconds : 0;
    var waitEnd = switchIn - ready;
    var waitStart = waitEnd - waiting.Nanoseconds;
    if (waiting.Nanoseconds < 1_000_000) continue;
    var by = a.ReadyingThread is { } t
        ? $"{a.ReadyingProcess?.ImageName ?? "?"} #{t.Id}{(roles.TryGetValue(t.Id, out var role) && a.ReadyingProcess?.Id == request.Pid ? $" ({role})" : string.IsNullOrEmpty(t.Name) ? "" : $" ({t.Name})")}"
        : "?";
    waits.Add(new Wait(a.Thread.Id, waitStart, waitEnd, ready, by, StackSummary(a.ReadyThreadStack, leafFirst, options.Symbols)));
}

var interrupts = pendingInterrupts.HasResult
    ? pendingInterrupts.Result.Activity.Select(x => (Start: Ns(x.Interval.StartTime), Stop: Ns(x.Interval.StopTime), Ms: (double)x.Duration.TotalMilliseconds,
        Type: x.Interval.Type.ToString(), Image: x.Interval.HandlerImage?.FileName ?? "?")).OrderBy(x => x.Start).ToList()
    : [];

var results = new List<WindowResult>();
foreach (var w in request.Windows)
{
    var start = (long)(w.StartMs * 1e6) + alignment.OffsetNs;
    var end = (long)(w.EndMs * 1e6) + alignment.OffsetNs;
    var span = (end - start) / 1e6;
    var inWindow = Range(dcsSamples, x => x.Ns, start, end);

    var threads = request.Threads.Select(kv =>
    {
        var running = dcsThreads.TryGetValue(kv.Value, out var runs) ? runs.Overlap(start, end) / 1e6 : 0;
        var mine = inWindow.Where(x => x.Tid == kv.Value).ToList();
        return new ThreadResult(kv.Key, kv.Value, Name(names, kv.Value), Math.Round(running, 2), Math.Round(span - running, 2),
            Top(mine.GroupBy(x => x.In), strings), Top(mine.GroupBy(x => x.Caller), strings),
            waits.Where(x => x.Tid == kv.Value && x.End > start && x.Start < end)
                .Select(x => new WaitResult(Math.Round((x.Start - alignment.OffsetNs) / 1e6, 2), Math.Round((x.End - x.Start) / 1e6, 2),
                    Math.Round(x.ReadyNs / 1e6, 2), x.By, x.Stack))
                .OrderByDescending(x => x.Ms).Take(5).ToList());
    }).ToList();

    var others = dcsThreads.Where(kv => !roles.ContainsKey(kv.Key))
        .Select(kv => (Tid: kv.Key, Ms: kv.Value.Overlap(start, end) / 1e6)).Where(x => x.Ms > 0.5)
        .OrderByDescending(x => x.Ms).Take(6)
        .Select(x => new OtherThread(x.Tid, Name(names, x.Tid), Math.Round(x.Ms, 2),
            Top(inWindow.Where(s => s.Tid == x.Tid).GroupBy(s => s.In), strings, 3)))
        .ToList();

    var processes = all.Overlaps(start, end).GroupBy(x => x.Pid).Where(g => g.Key > 0)
        .Select(g => new ProcessCpu(processNames.GetValueOrDefault(g.Key, "?"), g.Key, Math.Round(g.Sum(x => x.Ms), 2)))
        .OrderByDescending(x => x.Ms).Take(8).ToList();

    var irq = Range(interrupts, x => x.Start, start, end);
    var interrupt = new InterruptResult(
        Math.Round(irq.Where(x => x.Type.Contains("Deferred", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Ms), 2),
        Math.Round(irq.Where(x => !x.Type.Contains("Deferred", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Ms), 2),
        irq.GroupBy(x => x.Image).Select(g => new Share(g.Key, Math.Round(g.Sum(x => x.Ms), 2))).OrderByDescending(x => x.Ms).Take(4).ToList());

    results.Add(new WindowResult(w.Id, w.StartMs, w.EndMs, threads, others, processes, interrupt));
}

var output = new Output(Path.GetFullPath(options.Etl), alignment.OffsetNs / 1e6, alignment.Score, alignment.RunnerUp, alignment.Records,
    options.Symbols, results);
File.WriteAllText(options.Out, JsonSerializer.Serialize(output, Json.Options));
Console.WriteLine($"{results.Count} windows written to {options.Out}");
return 0;

static long Ns(TraceTimestamp t) => t.RelativeTimestamp.Nanoseconds;

static string Frame(string? image, string? function) =>
    function is null ? image ?? "?" : $"{image ?? "?"}!{function}";

static string Name(Dictionary<int, string> names, int tid) => names.TryGetValue(tid, out var n) ? n : "";

static List<Share> Top<T>(IEnumerable<IGrouping<int, T>> groups, StringTable strings, int count = 6) where T : IWeighted =>
    groups.Select(g => new Share(strings[g.Key], Math.Round(g.Sum(x => x.Ms), 2))).OrderByDescending(x => x.Ms).Take(count).ToList();

static List<T> Range<T>(List<T> sorted, Func<T, long> key, long start, long end)
{
    int lo = 0, hi = sorted.Count;
    while (lo < hi) { var mid = (lo + hi) / 2; if (key(sorted[mid]) < start) lo = mid + 1; else hi = mid; }
    var result = new List<T>();
    for (var i = lo; i < sorted.Count && key(sorted[i]) < end; i++) result.Add(sorted[i]);
    return result;
}

static List<string> StackSummary(IStackSnapshot? stack, bool leafFirst, bool symbols)
{
    var result = new List<string>();
    if (stack?.Frames is not { } frames) return result;
    for (var k = 0; k < frames.Count && result.Count < 8; k++)
    {
        var f = frames[leafFirst ? k : frames.Count - 1 - k];
        var text = Frame(f.Image?.FileName, symbols ? f.Symbol?.FunctionName : null);
        if (result.Count == 0 || result[^1] != text) result.Add(text);
    }
    return result;
}

static DateTimeOffset OfxrStart(string path)
{
    var m = Regex.Match(Path.GetFileName(path), @"ofxr-bridge-flight-(\d{8}-\d{6})-pid(\d+)\.log$", RegexOptions.IgnoreCase);
    if (!m.Success) throw new InvalidDataException("not an OFXR flight log name: " + path);
    var local = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
    return new DateTimeOffset(local);
}

static List<(long Ns, int Tid)> ReadOfxrRecords(string path, HashSet<int> tids)
{
    var result = new List<(long, int)>();
    using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    while (reader.ReadLine() is { } line)
    {
        var ms = Field(line, " ms=");
        var tid = Field(line, " tid=");
        if (ms is null || tid is null || !int.TryParse(tid, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || !tids.Contains(t)) continue;
        if (double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) result.Add(((long)(v * 1e6), t));
    }
    return result;
}

static string? Field(string line, string key)
{
    var at = line.IndexOf(key, StringComparison.Ordinal);
    if (at < 0) return null;
    at += key.Length;
    var end = line.IndexOf(' ', at);
    return end < 0 ? line[at..] : line[at..end];
}

// The offset at which OFXR's records of mostly idle DCS threads fall inside those threads' time slices: 1 ms steps
// from half a second before the log's named start to a second and a half after it (the name is truncated to the
// second), then 10 us steps around the best.
static Alignment Align(List<(long Ns, int Tid)> records, Dictionary<int, Runs> threads, long centre)
{
    var idle = threads.Where(kv => kv.Value.Duty < 0.5).Select(kv => kv.Key).ToHashSet();
    var usable = records.Where(r => idle.Contains(r.Tid)).ToList();
    if (usable.Count == 0) throw new InvalidDataException("no OFXR records on a DCS thread the trace shows mostly idle");
    var step = Math.Max(1, usable.Count / 5000);
    var sample = usable.Where((_, i) => i % step == 0).ToList();
    double Score(long offset) => sample.Count(r => threads[r.Tid].Contains(r.Ns + offset, 20_000)) / (double)sample.Count;
    var coarse = Enumerable.Range(-500, 2001).Select(i => centre + i * 1_000_000L).Select(o => (Offset: o, Score: Score(o))).ToList();
    var best = coarse.MaxBy(x => x.Score);
    var fine = Enumerable.Range(-100, 201).Select(i => best.Offset + i * 10_000L).Select(o => (Offset: o, Score: Score(o))).MaxBy(x => x.Score);
    var runnerUp = coarse.Where(x => Math.Abs(x.Offset - fine.Offset) > 5_000_000).Select(x => x.Score).DefaultIfEmpty(0).Max();
    return new Alignment(fine.Offset, fine.Score, runnerUp, sample.Count);
}

static Options? ParseArgs(string[] args)
{
    string? Get(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    var etl = Get("--etl"); var ofxr = Get("--ofxr"); var windows = Get("--windows"); var output = Get("--out");
    return etl is null || ofxr is null || windows is null || output is null ? null : new Options(etl, ofxr, windows, output, args.Contains("--symbols"));
}

partial class Program
{
    // Callers' frames in these are skipped when attributing a sample to the code that asked for the work.
    static readonly HashSet<string> WindowsLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntoskrnl.exe", "ntdll.dll", "kernelbase.dll", "kernel32.dll", "ucrtbase.dll", "vcruntime140.dll", "vcruntime140_1.dll",
        "msvcp140.dll", "win32u.dll", "hal.dll", "win32kbase.sys", "win32kfull.sys", "combase.dll", "user32.dll", "gdi32.dll",
    };
}

interface IWeighted { double Ms { get; } }
record Sample(long Ns, int Tid, double Ms, int In, int Caller) : IWeighted;
record Wait(int Tid, long Start, long End, long ReadyNs, string By, List<string> Stack);
record Options(string Etl, string Ofxr, string Windows, string Out, bool Symbols);
record Alignment(long OffsetNs, double Score, double RunnerUp, int Records);
record WindowRequest(int Pid, Dictionary<string, int> Threads, List<WindowSpec> Windows);
record WindowSpec(string Id, double StartMs, double EndMs);
record Share(string Name, double Ms);
record WaitResult(double StartMs, double Ms, double ReadyMs, string ReadiedBy, List<string> ReadyStack);
record ThreadResult(string Role, int Tid, string Name, double RunningMs, double NotRunningMs, List<Share> In, List<Share> CalledFrom, List<WaitResult> Waits);
record OtherThread(int Tid, string Name, double RunningMs, List<Share> In);
record ProcessCpu(string Name, int Pid, double Ms);
record InterruptResult(double DpcMs, double IsrMs, List<Share> Top);
record WindowResult(string Id, double StartMs, double EndMs, List<ThreadResult> Threads, List<OtherThread> OtherDcsThreads,
    List<ProcessCpu> Processes, InterruptResult Interrupts);
record Output(string Etl, double OffsetMs, double AlignmentScore, double AlignmentRunnerUp, int AlignmentRecords, bool Symbols,
    List<WindowResult> Windows);

static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
}

sealed class StringTable
{
    readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    readonly List<string> _values = [];
    public int Id(string value)
    {
        if (!_ids.TryGetValue(value, out var id)) { id = _values.Count; _values.Add(value); _ids[value] = id; }
        return id;
    }
    public string this[int id] => _values[id];
}

// One thread's time slices, sorted and non-overlapping.
sealed class Runs
{
    readonly long[] _start, _stop;
    public double Duty { get; }
    public Runs(IEnumerable<(long Start, long Stop)> slices)
    {
        var sorted = slices.OrderBy(s => s.Start).ToArray();
        _start = sorted.Select(s => s.Start).ToArray();
        _stop = sorted.Select(s => s.Stop).ToArray();
        var span = sorted.Length == 0 ? 0 : sorted[^1].Stop - sorted[0].Start;
        Duty = span <= 0 ? 1 : sorted.Sum(s => (double)(s.Stop - s.Start)) / span;
    }
    public bool Contains(long t, long tolerance)
    {
        var i = Array.BinarySearch(_start, t + tolerance);
        if (i < 0) i = ~i - 1;
        return i >= 0 && t <= _stop[i] + tolerance;
    }
    public long Overlap(long start, long end)
    {
        var i = Array.BinarySearch(_start, start);
        if (i < 0) i = Math.Max(0, ~i - 1);
        long sum = 0;
        for (; i < _start.Length && _start[i] < end; i++) sum += Math.Max(0, Math.Min(end, _stop[i]) - Math.Max(start, _start[i]));
        return sum;
    }
}

// Every time slice, sorted by start, for CPU per process in a window.
sealed class SliceIndex
{
    readonly (long Start, long Stop, int Pid, int Tid)[] _slices;
    readonly long[] _starts;
    readonly long _longest;
    public SliceIndex(IEnumerable<(long, long, int, int)> slices)
    {
        _slices = slices.OrderBy(s => s.Item1).ToArray();
        _starts = _slices.Select(s => s.Start).ToArray();
        _longest = _slices.Length == 0 ? 0 : _slices.Max(s => s.Stop - s.Start);
    }
    public IEnumerable<(int Pid, double Ms)> Overlaps(long start, long end)
    {
        var i = Array.BinarySearch(_starts, start - _longest);
        if (i < 0) i = ~i;
        for (; i < _slices.Length && _slices[i].Start < end; i++)
        {
            var s = _slices[i];
            var ms = (Math.Min(end, s.Stop) - Math.Max(start, s.Start)) / 1e6;
            if (ms > 0) yield return (s.Pid, ms);
        }
    }
}
