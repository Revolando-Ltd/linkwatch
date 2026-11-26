using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Real-time USB byte counter for ONE device, built only on Windows' own ETW (no drivers, no third-party tools).
// Listens to Microsoft-Windows-USB-UCX:
//   - rundown events 4 (root hub: controller + device path) and 5 (device: controller, device handle, port path)
//     tell which device handle is the one on a given root hub + port chain,
//   - event 27 (bulk/interrupt transfer completed) carries that device handle, the direction and the number of
//     bytes actually transferred.
// So traffic of other USB devices is never counted, and both directions are real byte counts.
// Needs to run as administrator.
public class EtwUsb {
    const string SessionName = "LinkWatchUsb";
    static readonly string[] StaleSessions = { "LinkWatchUsb", "OvrMonitorUsb" };   // ours, incl. the old app name
    static Guid UcxProvider = new Guid("36DA592D-E43A-4E28-AF6F-4BC57C5A11E8");
    // keywords: Rundown (0x8000) for events 4/5, HeadersBusTrace (0x40) for transfer events. Not the data-carrying ones.
    const ulong Keywords = 0x8040;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong h, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong h, string name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong h, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr p);
    [DllImport("advapi32.dll", SetLastError = true)] static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] h, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong h);
    delegate void RecordCallback(IntPtr record);

    const int PropsSize = 120, LogfileSize = 448;
    ulong session, trace = ulong.MaxValue;
    IntPtr logfile = IntPtr.Zero, loggerName = IntPtr.Zero;
    RecordCallback cb;            // kept alive while ProcessTrace runs
    Thread thread;
    volatile bool running;
    public string LastError = "";
    public bool Running { get { return running; } }

    // ---- which device to count ----
    readonly object gate = new object();
    readonly Dictionary<ulong, string> rootHubOfController = new Dictionary<ulong, string>();   // controller -> root hub path (upper case)
    class Dev { public ulong Controller; public int[] Path; }
    readonly Dictionary<ulong, Dev> devices = new Dictionary<ulong, Dev>();                       // device handle -> where it sits
    string targetRoot; int[] targetPath;
    long targetHandle;            // 0 = not resolved (yet)
    public bool TargetResolved { get { return Interlocked.Read(ref targetHandle) != 0; } }

    // ---- counters ----
    long outBytes, inBytes, events;
    int urbPipeOffset = -1;       // where the URB's PipeHandle sits in event 27, found from the first event
    public long EventsSeen { get { return Interlocked.Read(ref events); } }

    static IntPtr NewProps() {
        IntPtr p = Marshal.AllocHGlobal(PropsSize + 1024);
        for (int i = 0; i < PropsSize + 1024; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, PropsSize + 1024);     // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                   // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, 0x20000);             // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 256);                 // BufferSize (KB)
        Marshal.WriteInt32(p, 52, 16);                  // MinimumBuffers
        Marshal.WriteInt32(p, 56, 64);                  // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x100);               // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);                   // FlushTimer = 1 s
        Marshal.WriteInt32(p, 116, PropsSize);          // LoggerNameOffset
        return p;
    }
    static void StopSession(string name) {
        IntPtr p = NewProps();
        ControlTraceW(0, name, p, 1);                   // EVENT_TRACE_CONTROL_STOP
        Marshal.FreeHGlobal(p);
    }
    // Events and real-time buffers the session had to drop (0 = the numbers are complete)
    public long Lost() {
        if (!running) return 0;
        IntPtr p = NewProps();
        try { return ControlTraceW(0, SessionName, p, 0) != 0 ? 0 : (long)(uint)Marshal.ReadInt32(p, 88) + (uint)Marshal.ReadInt32(p, 100); }
        finally { Marshal.FreeHGlobal(p); }
    }

    // rootHubId: PnP instance id of the root hub (USB\ROOT_HUB30\...), ports: port numbers from the root hub down to the device
    public void SetTarget(string rootHubId, int[] ports) {
        lock (gate) {
            targetRoot = rootHubId == null ? null : rootHubId.ToUpperInvariant();
            targetPath = ports;
            Resolve();
        }
    }
    static string RootFromPath(string interfacePath) {
        // \??\USB#ROOT_HUB30#5&1931f968&0&0#{guid}  ->  USB\ROOT_HUB30\5&1931F968&0&0
        string s = interfacePath.ToUpperInvariant();
        int i = s.IndexOf("USB#"); if (i < 0) return s;
        s = s.Substring(i); int g = s.IndexOf("#{"); if (g > 0) s = s.Substring(0, g);
        return s.Replace('#', '\\');
    }
    void Resolve() {   // call with gate held
        long found = 0;
        if (targetRoot != null && targetPath != null)
            foreach (var kv in devices) {
                string root;
                if (!rootHubOfController.TryGetValue(kv.Value.Controller, out root) || root != targetRoot) continue;
                var p = kv.Value.Path; if (p.Length != targetPath.Length) continue;
                bool same = true; for (int i = 0; i < p.Length; i++) if (p[i] != targetPath[i]) { same = false; break; }
                if (same) { found = (long)kv.Key; break; }
            }
        Interlocked.Exchange(ref targetHandle, found);
    }

    public bool Start() {
        Stop();
        foreach (var s in StaleSessions) StopSession(s);   // left behind by a killed or crashed run
        lock (gate) { rootHubOfController.Clear(); devices.Clear(); Interlocked.Exchange(ref targetHandle, 0); }
        Interlocked.Exchange(ref events, 0);
        IntPtr props = NewProps();
        uint r = StartTraceW(out session, SessionName, props);
        Marshal.FreeHGlobal(props);
        if (r != 0) { LastError = r == 5 ? "needs administrator" : "StartTrace error " + r; return false; }
        r = EnableTraceEx2(session, ref UcxProvider, 1, 5, Keywords, 0, 0, IntPtr.Zero);
        if (r != 0) { LastError = "EnableTrace error " + r; StopSession(SessionName); return false; }

        cb = OnEvent;
        loggerName = Marshal.StringToHGlobalUni(SessionName);
        logfile = Marshal.AllocHGlobal(LogfileSize);
        for (int i = 0; i < LogfileSize; i++) Marshal.WriteByte(logfile, i, 0);
        Marshal.WriteIntPtr(logfile, 8, loggerName);                    // LoggerName
        Marshal.WriteInt32(logfile, 28, 0x100 | 0x10000000);            // REAL_TIME | EVENT_RECORD
        Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(cb));
        trace = OpenTraceW(logfile);
        if (trace == ulong.MaxValue) { LastError = "OpenTrace error " + Marshal.GetLastWin32Error(); StopSession(SessionName); FreeBuffers(); return false; }

        running = true; LastError = "";
        ulong h = trace;
        thread = new Thread(() => {
            ProcessTrace(new ulong[] { h }, 1, IntPtr.Zero, IntPtr.Zero);   // blocks until the session stops
            running = false;
        });
        thread.IsBackground = true; thread.Start();
        return true;
    }

    public void Stop() {
        if (trace != ulong.MaxValue) { CloseTrace(trace); trace = ulong.MaxValue; }
        if (running || thread != null) StopSession(SessionName);
        // the callback may still be running: let ProcessTrace return before its buffers are freed
        if (thread != null && thread != Thread.CurrentThread) thread.Join(3000);
        thread = null; running = false;
        FreeBuffers();
    }
    void FreeBuffers() {
        if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
        if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
    }

    // EVENT_RECORD: EventDescriptor.Id at 40, UserDataLength at 86, UserData at 96
    void OnEvent(IntPtr rec) {
        try {
            Interlocked.Increment(ref events);
            int id = (ushort)Marshal.ReadInt16(rec, 40);
            if (id != 27 && id != 4 && id != 5) return;
            int len = (ushort)Marshal.ReadInt16(rec, 86);
            IntPtr data = Marshal.ReadIntPtr(rec, 96);
            if (data == IntPtr.Zero) return;

            if (id == 27) {
                // UcxController(8) UsbDevice(8) PipeHandle(8) Irp(8) Urb(8) then the URB: ... PipeHandle, TransferFlags(4), TransferBufferLength(4)
                if (len < 60) return;
                long target = Interlocked.Read(ref targetHandle);
                if (target == 0 || Marshal.ReadInt64(data, 8) != target) return;
                int off = urbPipeOffset;
                if (off < 0) {
                    long pipe = Marshal.ReadInt64(data, 16);
                    for (int o = 40; o + 16 <= len; o += 4) if (Marshal.ReadInt64(data, o) == pipe) { off = o; break; }
                    if (off < 0) return;
                    urbPipeOffset = off;
                }
                if (off + 16 > len) return;
                uint flags = (uint)Marshal.ReadInt32(data, off + 8), bytes = (uint)Marshal.ReadInt32(data, off + 12);
                if ((flags & 1) != 0) Interlocked.Add(ref inBytes, bytes); else Interlocked.Add(ref outBytes, bytes);   // USBD_TRANSFER_DIRECTION_IN
            } else if (id == 4) {
                // root hub: UcxController(8) UsbDevice(8) DeviceInterfacePath (UTF-16, zero terminated)
                if (len < 18) return;
                ulong controller = (ulong)Marshal.ReadInt64(data, 0);
                string path = Marshal.PtrToStringUni(new IntPtr(data.ToInt64() + 16), (len - 16) / 2);
                int z = path.IndexOf('\0'); if (z >= 0) path = path.Substring(0, z);
                lock (gate) { rootHubOfController[controller] = RootFromPath(path); Resolve(); }
            } else {
                // device: UcxController(8) HubDevice(8) UsbDevice(8) DeviceSpeed(4) PortPathDepth(4) PortPath(6 x 4)
                if (len < 56) return;
                ulong controller = (ulong)Marshal.ReadInt64(data, 0), dev = (ulong)Marshal.ReadInt64(data, 16);
                int depth = Marshal.ReadInt32(data, 28); if (depth < 1 || depth > 6) return;
                var p = new int[depth]; for (int i = 0; i < depth; i++) p[i] = Marshal.ReadInt32(data, 32 + 4 * i);
                lock (gate) { devices[dev] = new Dev { Controller = controller, Path = p }; Resolve(); }
            }
        } catch { }   // never let an exception escape into the ETW thread
    }

    // {out, in} bytes of the target device since the last call
    public long[] Take() { return new[] { Interlocked.Exchange(ref outBytes, 0), Interlocked.Exchange(ref inBytes, 0) }; }

    // for diagnostics: what the rundown found
    public string Describe() {
        lock (gate) {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in devices) {
                string root; rootHubOfController.TryGetValue(kv.Value.Controller, out root);
                sb.AppendLine(kv.Key.ToString("X") + "  " + root + "  ports " + string.Join(".", kv.Value.Path));
            }
            sb.Append("target " + targetRoot + " ports " + (targetPath == null ? "-" : string.Join(".", targetPath)) + " -> handle " + targetHandle.ToString("X"));
            return sb.ToString();
        }
    }
}
