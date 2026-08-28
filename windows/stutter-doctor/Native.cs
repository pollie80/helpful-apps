using System.Runtime.InteropServices;
using System.Text;

namespace StutterDoctor;

/// <summary>Win32 / NT / PDH interop. Everything here is read-only telemetry: no injection,
/// no process memory reads, no hooks. Anti-cheat safe by construction.</summary>
internal static class Native
{
    // ---------------------------------------------------------------- timing

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint ms);

    [DllImport("ntdll.dll")]
    public static extern int NtSetTimerResolution(uint desired100ns, bool set, out uint current100ns);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryTimerResolution(out uint min100ns, out uint max100ns, out uint cur100ns);

    /// <summary>THREAD_PRIORITY_TIME_CRITICAL. In any non-realtime process class this pins the
    /// thread to base priority 15 — above every normal game thread, and unaffected by dropping the
    /// process priority class. System.Threading.ThreadPriority tops out at Highest (base 10), which
    /// is not enough to distinguish "the OS stalled" from "we got preempted".</summary>
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetThreadPriority(IntPtr hThread, int priority);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr h, int cls,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    /// <summary>
    /// Opts this process out of Windows 11 power throttling (EcoQoS).
    ///
    /// This is not an optimisation, it is a correctness requirement. By default Windows ignores a
    /// background process's timeBeginPeriod request and clamps it to the stock ~15.6 ms tick. Since
    /// this tool is *always* in the background while you are playing, without this call every
    /// Sleep(1) would return in ~15.6 ms and the stall probe would report a continuous stream of
    /// phantom 16 ms freezes that exist only because we were throttled.
    /// </summary>
    public static bool DisablePowerThrottling()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = POWER_THROTTLING_EXECUTION_SPEED | POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask = 0,   // 0 = opt out of both behaviours
        };
        try
        {
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { return false; }
    }

    /// <summary>Timer resolution currently in effect, in milliseconds. Should read ~1.0 while a
    /// session is running; ~15.6 means the request is not being honoured.</summary>
    public static double CurrentTimerResolutionMs()
    {
        try
        {
            if (NtQueryTimerResolution(out _, out _, out uint cur) == 0) return cur / 10000.0;
        }
        catch { }
        return double.NaN;
    }

    // ---------------------------------------------------------------- window / input

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint VK_F8 = 0x77;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    // ---------------------------------------------------------------- displays

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    public const int DISPLAY_DEVICE_ACTIVE = 0x1;
    public const int DISPLAY_DEVICE_PRIMARY = 0x4;
    public const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE dm);

    public sealed record DisplayInfo(
        string Device, string Adapter, string MonitorName, string MonitorId,
        int Width, int Height, int Hz, bool Primary);

    public static List<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        for (uint i = 0; i < 16; i++)
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) == 0) continue;

            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) continue;

            // Second-level enumeration gives the physical monitor behind this adapter output,
            // which is what Valorant records in DefaultMonitorDeviceID.
            string monName = "", monId = "";
            var md = new DISPLAY_DEVICE();
            md.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (EnumDisplayDevices(dd.DeviceName, 0, ref md, 0))
            {
                monName = md.DeviceString.Trim();
                monId = md.DeviceID.Trim();
            }

            list.Add(new DisplayInfo(
                dd.DeviceName, dd.DeviceString.Trim(), monName, monId,
                (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency,
                (dd.StateFlags & DISPLAY_DEVICE_PRIMARY) != 0));
        }
        return list;
    }

    // ---------------------------------------------------------------- PDH

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>A tiny PDH query wrapper. Uses AddEnglishCounter so it works on localised Windows.</summary>
    public sealed class PdhQuery : IDisposable
    {
        private IntPtr _query;
        private readonly List<(string path, IntPtr h, bool noCap)> _counters = new();
        private bool _primed;

        public PdhQuery()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
        }

        /// <summary>Returns a handle index, or -1 if the counter does not exist on this machine.</summary>
        public int Add(string path, bool noCap100 = false)
        {
            if (_query == IntPtr.Zero) return -1;
            if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var h) != 0) return -1;
            _counters.Add((path, h, noCap100));
            return _counters.Count - 1;
        }

        public bool Collect()
        {
            if (_query == IntPtr.Zero) return false;
            var rc = PdhCollectQueryData(_query);
            if (rc != 0) return false;
            if (!_primed) { _primed = true; return false; }   // rate counters need two samples
            return true;
        }

        public double Read(int index)
        {
            if (index < 0 || index >= _counters.Count) return double.NaN;
            var (_, h, noCap) = _counters[index];
            var fmt = PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0);
            if (PdhGetFormattedCounterValue(h, fmt, out _, out var v) != 0) return double.NaN;
            if (v.CStatus != PDH_CSTATUS_VALID_DATA && v.CStatus != PDH_CSTATUS_NEW_DATA) return double.NaN;
            return v.doubleValue;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        }
    }

    // ---------------------------------------------------------------- process table (NtQuerySystemInformation)

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    public struct RawProc
    {
        public int Pid;
        public string Name;
        public long KernelTime;     // 100 ns
        public long UserTime;       // 100 ns
        public ulong CycleTime;
        public uint HardFaults;     // cumulative hard page faults
        public uint PageFaults;     // cumulative soft+hard faults
        public long WorkingSet;     // bytes
        public int Threads;
    }

    /// <summary>Snapshots every process in one syscall. Much cheaper (and more complete —
    /// it sees protected processes such as vgk/vgc) than Process.GetProcesses().</summary>
    public static unsafe List<RawProc> SnapshotProcesses()
    {
        var result = new List<RawProc>(400);
        int size = 512 * 1024;
        IntPtr buf = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                buf = Marshal.AllocHGlobal(size);
                int status = NtQuerySystemInformation(SystemProcessInformation, buf, size, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    size = Math.Max(needed + 64 * 1024, size * 2);
                    continue;
                }
                if (status != 0) return result;
                break;
            }
            if (buf == IntPtr.Zero) return result;

            byte* p = (byte*)buf;
            while (true)
            {
                uint next = *(uint*)(p + 0x00);
                var e = new RawProc
                {
                    Threads = *(int*)(p + 0x04),
                    HardFaults = *(uint*)(p + 0x10),
                    CycleTime = *(ulong*)(p + 0x18),
                    UserTime = *(long*)(p + 0x28),
                    KernelTime = *(long*)(p + 0x30),
                    Pid = (int)*(nint*)(p + 0x50),
                    PageFaults = *(uint*)(p + 0x80),
                    WorkingSet = (long)*(nuint*)(p + 0x90),
                };

                ushort nameLen = *(ushort*)(p + 0x38);
                nint namePtr = *(nint*)(p + 0x40);
                e.Name = (namePtr != 0 && nameLen > 0)
                    ? new string((char*)namePtr, 0, nameLen / 2)
                    : (e.Pid == 0 ? "Idle" : "System");

                result.Add(e);
                if (next == 0) break;
                p += next;
            }
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
        return result;
    }

    // ---------------------------------------------------------------- misc

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int processId);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static string ReadRegString(string key, string value)
    {
        try { return Microsoft.Win32.Registry.GetValue(key, value, null)?.ToString(); }
        catch { return null; }
    }

    public static int? ReadRegInt(string key, string value)
    {
        try
        {
            var o = Microsoft.Win32.Registry.GetValue(key, value, null);
            if (o == null) return null;
            return Convert.ToInt32(o);
        }
        catch { return null; }
    }
}
