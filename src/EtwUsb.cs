using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Real-time USB byte counter built only on Windows' own ETW (no drivers, no third-party tools).
// Listens to the Microsoft-Windows-USB-USBXHCI provider, event 41 ("USB transfer"), and sums
// fid_BytesTotal per device slot and direction. Needs to run as administrator.
//   OUT (PC -> device): exact transferred size.
//   IN  (device -> PC): requested buffer size, so it can overstate (about 10-15 % under load).
public class EtwUsb {
    const string SessionName = "LinkWatchUsb";
    static Guid XhciProvider = new Guid("30E1D284-5D88-459C-83FD-6345B39B19EC");

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong h, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong h, string name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong h, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr p);
    [DllImport("advapi32.dll")] static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] h, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong h);
    [DllImport("tdh.dll")] static extern uint TdhGetPropertySize(IntPtr ev, uint cc, IntPtr ctx, uint n, ref PDD d, out uint size);
    [DllImport("tdh.dll")] static extern uint TdhGetProperty(IntPtr ev, uint cc, IntPtr ctx, uint n, ref PDD d, uint size, byte[] buf);

    [StructLayout(LayoutKind.Sequential)] struct PDD { public IntPtr PropertyName; public uint ArrayIndex; public uint Reserved; }
    delegate void RecordCallback(IntPtr record);

    const int PropsSize = 120, LogfileSize = 448;
    ulong session, trace = ulong.MaxValue;
    IntPtr logfile = IntPtr.Zero, loggerName = IntPtr.Zero;
    RecordCallback cb;            // kept alive while ProcessTrace runs
    Thread thread;
    volatile bool running;
    readonly object gate = new object();
    Dictionary<int, long[]> slots = new Dictionary<int, long[]>();   // slot -> {out, in}
    IntPtr pSlot = Marshal.StringToHGlobalUni("fid_SlotId");
    IntPtr pEp = Marshal.StringToHGlobalUni("fid_EndpointContextIndex");
    IntPtr pBytes = Marshal.StringToHGlobalUni("fid_BytesTotal");
    public string LastError = "";

    public bool Running { get { return running; } }

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

    static void StopSession() {
        IntPtr p = NewProps();
        ControlTraceW(0, SessionName, p, 1);            // EVENT_TRACE_CONTROL_STOP
        Marshal.FreeHGlobal(p);
    }

    public bool Start() {
        Stop();
        StopSession();                                  // a stale session from a crashed run
        IntPtr props = NewProps();
        uint r = StartTraceW(out session, SessionName, props);
        Marshal.FreeHGlobal(props);
        if (r != 0) { LastError = r == 5 ? "needs administrator" : "StartTrace error " + r; return false; }
        r = EnableTraceEx2(session, ref XhciProvider, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (r != 0) { LastError = "EnableTrace error " + r; StopSession(); return false; }

        cb = OnEvent;
        loggerName = Marshal.StringToHGlobalUni(SessionName);
        logfile = Marshal.AllocHGlobal(LogfileSize);
        for (int i = 0; i < LogfileSize; i++) Marshal.WriteByte(logfile, i, 0);
        Marshal.WriteIntPtr(logfile, 8, loggerName);                    // LoggerName
        Marshal.WriteInt32(logfile, 28, 0x100 | 0x10000000);            // REAL_TIME | EVENT_RECORD
        Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(cb));
        trace = OpenTraceW(logfile);
        if (trace == ulong.MaxValue) { LastError = "OpenTrace error " + Marshal.GetLastWin32Error(); StopSession(); return false; }

        running = true; LastError = "";
        thread = new Thread(() => {
            ProcessTrace(new ulong[] { trace }, 1, IntPtr.Zero, IntPtr.Zero);   // blocks until the session stops
            running = false;
        });
        thread.IsBackground = true; thread.Start();
        return true;
    }

    public void Stop() {
        if (trace != ulong.MaxValue) { CloseTrace(trace); trace = ulong.MaxValue; }
        StopSession();
        running = false;
        if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
        if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
    }

    long Prop(IntPtr ev, IntPtr name) {
        PDD d = new PDD { PropertyName = name, ArrayIndex = uint.MaxValue };
        uint size;
        if (TdhGetPropertySize(ev, 0, IntPtr.Zero, 1, ref d, out size) != 0 || size == 0 || size > 8) return -1;
        byte[] b = new byte[8];
        if (TdhGetProperty(ev, 0, IntPtr.Zero, 1, ref d, size, b) != 0) return -1;
        return BitConverter.ToInt64(b, 0);
    }

    void OnEvent(IntPtr rec) {
        // EVENT_RECORD: ProviderId at 24, EventDescriptor.Id at 40
        if (Marshal.ReadInt16(rec, 40) != 41) return;
        byte[] g = new byte[16]; Marshal.Copy(new IntPtr(rec.ToInt64() + 24), g, 0, 16);
        if (new Guid(g) != XhciProvider) return;
        long slot = Prop(rec, pSlot), ep = Prop(rec, pEp), bytes = Prop(rec, pBytes);
        if (slot < 0 || ep < 0 || bytes < 0) return;
        if (ep == 1) return;                            // control endpoint
        int dir = (ep % 2 == 0) ? 0 : 1;                // even DCI = OUT, odd DCI = IN
        lock (gate) {
            long[] v;
            if (!slots.TryGetValue((int)slot, out v)) { v = new long[2]; slots[(int)slot] = v; }
            v[dir] += bytes;
        }
    }

    // Returns {out, in} bytes since the last call for the busiest device slot
    // (the Quest during Link; keyboards and mice move only a few bytes).
    public long[] Take() {
        lock (gate) {
            long[] best = new long[2];
            foreach (var v in slots.Values) if (v[0] + v[1] > best[0] + best[1]) best = new long[] { v[0], v[1] };
            slots.Clear();
            return best;
        }
    }
}
