using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DcsVr.Core;

/// <summary>
/// The flight helper's once-a-second record of the PC while DCS runs (<see cref="VrProfile.RecordLoad"/>), read by Last
/// flight beside OFXR's per-frame CPU and GPU time:
/// <list type="bullet">
/// <item>the whole CPU's use, DCS's CPU and its three busiest threads, and the busiest other process on the CPU, from one
/// NtQuerySystemInformation snapshot a second (every process's and thread's CPU time; nothing is opened per process);</item>
/// <item>the GPU's utilisation, graphics clock, temperature, power draw, the reasons it holds its clocks back and the
/// memory in use, from NVML (the NVIDIA driver's own library; the columns stay empty without it);</item>
/// <item>every <see cref="GpuEngineEverySeconds"/> seconds, DCS's own share of the GPU and the busiest other process on it,
/// from Windows' "GPU Engine" counters (what Task Manager shows).</item>
/// </list>
/// It runs on a below-normal thread of the helper process, never inside DCS, and changes nothing. One CSV per DCS process
/// in the helper's folder (flight-YYYYMMDD-HHMMSS-pidN.csv, local time); the newest <see cref="KeepFiles"/> are kept.
/// </summary>
public sealed partial class FlightMonitor : IDisposable
{
    public const string FilePrefix = "flight-";
    public const string Columns = "ms,cpu_pct,dcs_cpu_pct,t1,t1_pct,t2,t2_pct,t3,t3_pct,other_cpu,other_cpu_pct,gpu_pct,gpu_mhz,gpu_c,gpu_w,throttle,vram_mib,dcs_gpu_pct,other_gpu,other_gpu_pct";
    public const int KeepFiles = 10;
    public const int GpuEngineEverySeconds = 5;

    private readonly int _dcsPid;
    private readonly StreamWriter _writer;
    private readonly Action<string> _log;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private readonly DateTime _startLocal;
    private readonly long _startTicks;

    private FlightMonitor(int dcsPid, StreamWriter writer, Action<string> log, DateTime startLocal)
    {
        _dcsPid = dcsPid; _writer = writer; _log = log; _startLocal = startLocal;
        _startTicks = Environment.TickCount64;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Flight monitor" };
    }

    /// <summary>Starts recording for <paramref name="dcsPid"/> into <paramref name="directory"/>; null (logged) when the
    /// file cannot be created.</summary>
    public static FlightMonitor? Start(string directory, int dcsPid, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Prune(directory, KeepFiles - 1);
            var start = DateTime.Now;
            var path = Path.Combine(directory, $"{FilePrefix}{start:yyyyMMdd-HHmmss}-pid{dcsPid}.csv");
            var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
            var monitor = new FlightMonitor(dcsPid, writer, log, start);
            monitor._thread.Start();
            log($"Recording CPU and GPU use to {path}.");
            return monitor;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log("Could not record CPU and GPU use: " + e.Message);
            return null;
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> flight-*.csv in <paramref name="directory"/>.</summary>
    public static void Prune(string directory, int keep)
    {
        foreach (var old in new DirectoryInfo(directory).EnumerateFiles(FilePrefix + "*.csv").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Math.Max(0, keep)))
            try { old.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        try { _writer.Dispose(); } catch (IOException) { }
    }

    private void Run()
    {
        using var processes = new ProcessSampler(_dcsPid);
        using var nvml = Nvml.Open();
        using var engines = GpuEngines.Open();
        var cpu = new SystemCpu();
        var names = new Dictionary<int, string>();
        _log(nvml is null ? "NVML is not available: the GPU columns stay empty." : $"GPU: {nvml.Name}.");
        try
        {
            _writer.WriteLine("# DCS Control flight monitor v1");
            _writer.WriteLine(FormattableString.Invariant($"# start={_startLocal:yyyy-MM-ddTHH:mm:ss.fff} pid={_dcsPid} logical_cpus={Environment.ProcessorCount} gpu={Clean(nvml?.Name ?? "unknown").Replace(' ', '_')}"));
            _writer.WriteLine(Columns);
        }
        catch (IOException) { return; }
        cpu.Sample();
        var previous = processes.Take();
        var second = 0;
        while (!_stop.Wait(TimeSpan.FromSeconds(1)))
        {
            second++;
            try
            {
                var snapshot = processes.Take();
                var row = new FlightRow { Ms = Environment.TickCount64 - _startTicks, CpuPct = cpu.Sample() };
                if (snapshot is not null && previous is not null) FillCpu(row, previous, snapshot, names);
                previous = snapshot ?? previous;
                nvml?.Fill(row);
                if (engines is not null && second % GpuEngineEverySeconds == 0 && engines.Sample() is { } shares)
                {
                    var byPid = GpuEngines.ByProcess(shares);
                    row.DcsGpuPct = byPid.TryGetValue(_dcsPid, out var own) ? own : 0;
                    if (byPid.Where(p => p.Key != _dcsPid && p.Key != 0).OrderByDescending(p => p.Value).FirstOrDefault() is { Value: > 0 } other)
                    {
                        row.OtherGpu = snapshot?.Names.GetValueOrDefault(other.Key) ?? "pid " + other.Key;
                        row.OtherGpuPct = other.Value;
                    }
                }
                _writer.WriteLine(row.Format());
            }
            catch (IOException) { return; }
            catch (Exception e) when (e is Win32Exception or ExternalException or InvalidOperationException) { }
        }
    }

    private void FillCpu(FlightRow row, ProcessSnapshot before, ProcessSnapshot after, Dictionary<int, string> names)
    {
        var wall = (after.TakenTicks - before.TakenTicks) / 1e7;
        if (wall <= 0) return;
        double Pct(long delta) => Math.Max(0, delta / 1e7 / wall * 100);
        if (after.Processes.TryGetValue(_dcsPid, out var dcs) && before.Processes.TryGetValue(_dcsPid, out var dcsBefore))
            row.DcsCpuPct = Pct(dcs - dcsBefore);
        var top = after.DcsThreads.Select(t => (t.Key, Delta: t.Value.Cpu - (before.DcsThreads.TryGetValue(t.Key, out var b) ? b.Cpu : t.Value.Cpu)))
            .Where(t => t.Delta > 0).OrderByDescending(t => t.Delta).Take(3).ToArray();
        var main = after.DcsThreads.Count == 0 ? -1 : after.DcsThreads.MinBy(t => t.Value.Created).Key;
        for (var i = 0; i < top.Length; i++)
        {
            var tid = top[i].Key;
            if (!names.TryGetValue(tid, out var name)) names[tid] = name = ThreadName(tid, tid == main);
            row.Threads[i] = (name, Pct(top[i].Delta));
        }
        var other = after.Processes.Where(p => p.Key is not 0 && p.Key != _dcsPid && before.Processes.ContainsKey(p.Key))
            .Select(p => (p.Key, Delta: p.Value - before.Processes[p.Key])).OrderByDescending(p => p.Delta).FirstOrDefault();
        if (other.Delta > 0) { row.OtherCpu = after.Names.GetValueOrDefault(other.Key) ?? "pid " + other.Key; row.OtherCpuPct = Pct(other.Delta); }
    }

    /// <summary>The thread's own name (GetThreadDescription) with its id, "main" for DCS's first thread when it has no
    /// name, else just the id.</summary>
    private static string ThreadName(int tid, bool main)
    {
        string? description = null;
        var handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)tid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (GetThreadDescription(handle, out var text) >= 0 && text != IntPtr.Zero)
                {
                    description = Marshal.PtrToStringUni(text);
                    LocalFree(text);
                }
            }
            catch (EntryPointNotFoundException) { }
            finally { CloseHandle(handle); }
        }
        description = string.IsNullOrWhiteSpace(description) ? (main ? "main" : null) : description.Trim();
        return Clean(description is null ? "#" + tid : description + " #" + tid);
    }

    /// <summary>A CSV-safe cell: no commas or quotes, and whitespace (line breaks included) as single spaces.</summary>
    public static string Clean(string text) => CleanRegex().Replace(text, " ").Trim();
    [GeneratedRegex(@"[,""\s]+")] private static partial Regex CleanRegex();

    // ---- One row ----------------------------------------------------------------------------------------------------

    /// <summary>One second of the record. Percentages: <see cref="CpuPct"/> of the whole CPU; DCS, its threads and the
    /// other process of one logical CPU (a thread at 100 is saturated); the GPU ones of the GPU.</summary>
    public sealed class FlightRow
    {
        public long Ms;
        public double? CpuPct, DcsCpuPct, OtherCpuPct, GpuPct, GpuMhz, GpuC, GpuW, VramMiB, DcsGpuPct, OtherGpuPct;
        public ulong? Throttle;
        public string? OtherCpu, OtherGpu;
        public readonly (string Name, double Pct)?[] Threads = new (string, double)?[3];

        public string Format()
        {
            static string N(double? v) => v is { } d && double.IsFinite(d) ? Math.Round(d, 1).ToString(CultureInfo.InvariantCulture) : "";
            var cells = new List<string> { Ms.ToString(CultureInfo.InvariantCulture), N(CpuPct), N(DcsCpuPct) };
            foreach (var t in Threads) { cells.Add(t is { } x ? Clean(x.Name) : ""); cells.Add(N(t?.Pct)); }
            cells.AddRange([OtherCpu is null ? "" : Clean(OtherCpu), N(OtherCpuPct), N(GpuPct), N(GpuMhz), N(GpuC), N(GpuW),
                Throttle is { } r ? "0x" + r.ToString("x", CultureInfo.InvariantCulture) : "", N(VramMiB), N(DcsGpuPct),
                OtherGpu is null ? "" : Clean(OtherGpu), N(OtherGpuPct)]);
            return string.Join(',', cells);
        }
    }

    // ---- Whole CPU ----------------------------------------------------------------------------------------------------

    private sealed class SystemCpu
    {
        private long _idle, _total;
        public double? Sample()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            var total = kernel + user; // kernel time includes idle
            var (dIdle, dTotal) = (idle - _idle, total - _total);
            var first = _total == 0;
            (_idle, _total) = (idle, total);
            return first || dTotal <= 0 ? null : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
        }
    }

    // ---- Every process's and DCS's threads' CPU time ------------------------------------------------------------------

    /// <summary>One system snapshot: each process's CPU time (100 ns) and name, and DCS's threads' CPU time and creation.</summary>
    public sealed record ProcessSnapshot(long TakenTicks, Dictionary<int, long> Processes, Dictionary<int, string> Names, Dictionary<int, (long Cpu, long Created)> DcsThreads);

    /// <summary>NtQuerySystemInformation(SystemProcessInformation) into one native buffer, grown when Windows asks, and
    /// copied out (a few hundred KB a second) so the walk stays in safe code.</summary>
    private sealed class ProcessSampler(int dcsPid) : IDisposable
    {
        private IntPtr _buffer = Marshal.AllocHGlobal(1 << 20);
        private int _size = 1 << 20;
        private byte[] _copy = [];

        public ProcessSnapshot? Take()
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out var returned);
                if (status == StatusInfoLengthMismatch)
                {
                    _size = Math.Max(_size * 2, returned + (1 << 16));
                    Marshal.FreeHGlobal(_buffer); _buffer = Marshal.AllocHGlobal(_size);
                    continue;
                }
                if (status < 0 || returned <= 0 || returned > _size) return null;
                if (_copy.Length < returned) _copy = new byte[_size];
                Marshal.Copy(_buffer, _copy, 0, returned);
                return ParseProcesses(_copy.AsSpan(0, returned), (ulong)_buffer, dcsPid, DateTime.UtcNow.Ticks);
            }
            return null;
        }
        public void Dispose() { if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; } }
    }

    // SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION (x64), as the engine module's run_threads.h reads them.
    private const int ProcNext = 0x00, ProcThreadCount = 0x04, ProcCreate = 0x20, ProcUser = 0x28, ProcKernel = 0x30,
        ProcNameLength = 0x38, ProcNameBuffer = 0x40, ProcPid = 0x50, ProcThreads = 0x100;
    private const int ThreadSize = 0x50, ThreadKernel = 0x00, ThreadUser = 0x08, ThreadCreate = 0x10, ThreadTid = 0x30;

    /// <summary>Walks a SystemProcessInformation buffer whose first byte is at address <paramref name="baseAddress"/> (the
    /// image names point into it). Entries that run past the buffer end the walk.</summary>
    public static ProcessSnapshot ParseProcesses(ReadOnlySpan<byte> buffer, ulong baseAddress, int dcsPid, long takenTicks)
    {
        var processes = new Dictionary<int, long>(); var names = new Dictionary<int, string>(); var threads = new Dictionary<int, (long, long)>();
        var at = 0;
        while (at >= 0 && at + ProcThreads <= buffer.Length)
        {
            var entry = buffer[at..];
            var next = BinaryPrimitives.ReadInt32LittleEndian(entry[ProcNext..]);
            var pid = (int)BinaryPrimitives.ReadInt64LittleEndian(entry[ProcPid..]);
            processes[pid] = BinaryPrimitives.ReadInt64LittleEndian(entry[ProcUser..]) + BinaryPrimitives.ReadInt64LittleEndian(entry[ProcKernel..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[ProcNameLength..]);
            var namePointer = BinaryPrimitives.ReadUInt64LittleEndian(entry[ProcNameBuffer..]);
            if (nameLength > 0 && namePointer >= baseAddress && namePointer - baseAddress + nameLength <= (ulong)buffer.Length)
            {
                var name = Encoding.Unicode.GetString(buffer.Slice((int)(namePointer - baseAddress), nameLength));
                names[pid] = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            }
            if (pid == dcsPid)
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(entry[ProcThreadCount..]);
                for (var i = 0; i < count && ProcThreads + (i + 1) * ThreadSize <= entry.Length; i++)
                {
                    var thread = entry.Slice(ProcThreads + i * ThreadSize, ThreadSize);
                    var tid = (int)BinaryPrimitives.ReadInt64LittleEndian(thread[ThreadTid..]);
                    threads[tid] = (BinaryPrimitives.ReadInt64LittleEndian(thread[ThreadKernel..]) + BinaryPrimitives.ReadInt64LittleEndian(thread[ThreadUser..]),
                        BinaryPrimitives.ReadInt64LittleEndian(thread[ThreadCreate..]));
                }
            }
            if (next <= 0) break;
            at += next;
        }
        return new(takenTicks, processes, names, threads);
    }

    // ---- NVML ---------------------------------------------------------------------------------------------------------

    private sealed class Nvml : IDisposable
    {
        private readonly IntPtr _device;
        public string Name { get; }
        private Nvml(IntPtr device, string name) { _device = device; Name = name; }

        /// <summary>The NVIDIA GPU with the most memory, or null without NVML (no NVIDIA driver).</summary>
        public static Nvml? Open()
        {
            try
            {
                if (nvmlInit_v2() != 0) return null;
                if (nvmlDeviceGetCount_v2(out var count) != 0 || count == 0) { nvmlShutdown(); return null; }
                IntPtr best = IntPtr.Zero; ulong bestMemory = 0;
                for (uint i = 0; i < count; i++)
                    if (nvmlDeviceGetHandleByIndex_v2(i, out var device) == 0)
                    {
                        var memory = nvmlDeviceGetMemoryInfo(device, out var info) == 0 ? info.Total : 0;
                        if (best == IntPtr.Zero || memory > bestMemory) (best, bestMemory) = (device, memory);
                    }
                if (best == IntPtr.Zero) { nvmlShutdown(); return null; }
                var name = new byte[96];
                var text = nvmlDeviceGetName(best, name, (uint)name.Length) == 0 ? Encoding.ASCII.GetString(name).TrimEnd('\0') : "NVIDIA GPU";
                return new Nvml(best, text);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
        }

        public void Fill(FlightRow row)
        {
            if (nvmlDeviceGetUtilizationRates(_device, out var use) == 0) row.GpuPct = use.Gpu;
            if (nvmlDeviceGetClockInfo(_device, 0 /* graphics */, out var mhz) == 0) row.GpuMhz = mhz;
            if (nvmlDeviceGetTemperature(_device, 0 /* GPU */, out var c) == 0) row.GpuC = c;
            if (nvmlDeviceGetPowerUsage(_device, out var mw) == 0) row.GpuW = mw / 1000.0;
            if (nvmlDeviceGetCurrentClocksThrottleReasons(_device, out var reasons) == 0) row.Throttle = reasons;
            if (nvmlDeviceGetMemoryInfo(_device, out var memory) == 0) row.VramMiB = memory.Used / (1024.0 * 1024.0);
        }

        public void Dispose() { try { nvmlShutdown(); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { } }

        [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
    }

    /// <summary>NVML's clock reasons that mean the GPU slowed itself: for temperature (software and hardware thermal
    /// slowdown, hardware slowdown) and at its power limit (software power cap, hardware power brake).</summary>
    public const ulong ThermalReasons = 0x20 | 0x40 | 0x08, PowerReasons = 0x04 | 0x80;

    // ---- GPU engine counters ------------------------------------------------------------------------------------------

    private sealed class GpuEngines : IDisposable
    {
        private IntPtr _query, _counter;
        private GpuEngines(IntPtr query, IntPtr counter) { _query = query; _counter = counter; }

        public static GpuEngines? Open()
        {
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return null;
                if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var counter) != 0 || PdhCollectQueryData(query) != 0)
                {
                    PdhCloseQuery(query);
                    return null;
                }
                return new GpuEngines(query, counter);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }

        /// <summary>Each engine instance's utilisation since the previous sample.</summary>
        public List<(string Instance, double Pct)>? Sample()
        {
            if (PdhCollectQueryData(_query) != 0) return null;
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, buffer) != 0) return null;
                var items = new List<(string, double)>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var item = buffer + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, status, padding, double
                    if (Marshal.ReadInt32(item, 8) != 0) continue;
                    items.Add((Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
                }
                return items;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>Each process's share of the GPU as Task Manager shows it: per engine type the sum of its engines, and
        /// the busiest type. Instances are "pid_1234_luid_…_phys_0_eng_3_engtype_3D".</summary>
        public static Dictionary<int, double> ByProcess(IEnumerable<(string Instance, double Pct)> shares)
        {
            var byType = new Dictionary<(int, string), double>();
            foreach (var (instance, pct) in shares)
            {
                var m = EngineRegex().Match(instance);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || !double.IsFinite(pct)) continue;
                var key = (pid, m.Groups[2].Value);
                byType[key] = byType.GetValueOrDefault(key) + pct;
            }
            return byType.GroupBy(e => e.Key.Item1).ToDictionary(g => g.Key, g => Math.Min(100, g.Max(e => e.Value)));
        }

        public void Dispose() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; _counter = IntPtr.Zero; } }
    }
    [GeneratedRegex(@"^pid_(\d+)_.*_engtype_(.+)$")] private static partial Regex EngineRegex();

    /// <summary><see cref="GpuEngines.ByProcess"/>, for tests.</summary>
    public static Dictionary<int, double> GpuShareByProcess(IEnumerable<(string Instance, double Pct)> shares) => GpuEngines.ByProcess(shares);

    private const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    private const int PdhMoreData = unchecked((int)0x800007D2);
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ThreadQueryLimitedInformation = 0x0800;

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
