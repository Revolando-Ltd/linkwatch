using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

// Finds the Quest on USB and reads, from Windows itself: PC-side port type, hubs in between,
// device name and the negotiated USB speed. Uses only cfgmgr32 and USB hub IOCTLs.
public class UsbLink {
    public bool Connected;
    public string Speed = "", PcPort = "?", DevicePort = "USB-C", Device = "";
    public List<string> Hubs = new List<string>();
}

public static class UsbInfo {
    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; }
    static DEVPROPKEY KeyAddress = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 30 };
    static DEVPROPKEY KeyBusDesc = new DEVPROPKEY { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
    const string HubGuid = "#{f18a0e88-c30c-11d0-8815-00a0c906bed8}";

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out int len, string filter, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string filter, char[] buf, int len, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out int inst, string id, int flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out int parent, int inst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(int inst, char[] buf, int len, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(int inst, ref DEVPROPKEY key, out int type, byte[] buf, ref int size, int flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sec, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, int il, byte[] o, int ol, out int r, IntPtr ov);

    static string Id(int inst) { var b = new char[512]; return CM_Get_Device_IDW(inst, b, b.Length, 0) == 0 ? new string(b).TrimEnd('\0') : null; }
    static int Parent(int inst) { int p; return CM_Get_Parent(out p, inst, 0) == 0 ? p : 0; }
    static byte[] Prop(int inst, DEVPROPKEY k) {
        int type, size = 1024; var b = new byte[size];
        return CM_Get_DevNode_PropertyW(inst, ref k, out type, b, ref size, 0) == 0 ? b : null;
    }
    static int Address(int inst) { var b = Prop(inst, KeyAddress); return b == null ? 0 : BitConverter.ToInt32(b, 0); }
    static string BusDesc(int inst) {
        var b = Prop(inst, KeyBusDesc); if (b == null) return null;
        string s = Encoding.Unicode.GetString(b); int z = s.IndexOf('\0'); return z >= 0 ? s.Substring(0, z) : s;
    }
    static string HubPath(int hubInst) { return @"\\?\" + Id(hubInst).Replace('\\', '#') + HubGuid; }

    static int FindQuest() {
        int len; const int flags = 0x1 | 0x100;   // filter by enumerator, present devices only
        if (CM_Get_Device_ID_List_SizeW(out len, "USB", flags) != 0) return 0;
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW("USB", buf, len, flags) != 0) return 0;
        foreach (var id in new string(buf).Split('\0')) {
            if (!Regex.IsMatch(id, @"^USB\\VID_2833&PID_[0-9A-F]{4}\\", RegexOptions.IgnoreCase)) continue;
            int inst; if (CM_Locate_DevNodeW(out inst, id, 0) == 0) return inst;
        }
        return 0;
    }

    // IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX(_V2): negotiated speed of the device on a hub port
    static string Speed(string hub, int port) {
        using (var h = CreateFile(hub, 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (h.IsInvalid) return "cannot open hub (" + Marshal.GetLastWin32Error() + ")";
            int r; string note = "";
            var v2 = new byte[16];
            BitConverter.GetBytes(port).CopyTo(v2, 0); BitConverter.GetBytes(16).CopyTo(v2, 4); BitConverter.GetBytes(7).CopyTo(v2, 8);
            if (DeviceIoControl(h, 0x22045C, v2, 16, v2, 16, out r, IntPtr.Zero)) {
                uint fl = BitConverter.ToUInt32(v2, 12);
                if ((fl & 4) != 0) return "USB 3.2 Gen 2 (10 Gbps, SuperSpeed+)";
                if ((fl & 1) != 0) return "USB 3.x Gen 1 (5 Gbps, SuperSpeed)";
                if ((fl & 2) != 0) note = " (device is USB 3 capable, port/cable is at USB 2)";
            }
            var ex = new byte[512]; BitConverter.GetBytes(port).CopyTo(ex, 0);
            if (!DeviceIoControl(h, 0x220448, ex, ex.Length, ex, ex.Length, out r, IntPtr.Zero)) return "query failed (" + Marshal.GetLastWin32Error() + ")";
            switch (ex[23]) {
                case 0: return "USB 1 (Low Speed)";
                case 1: return "USB 1 (Full Speed, 12 Mbps)";
                case 2: return "USB 2.0 (High Speed, 480 Mbps)" + note;
                case 3: return "USB 3.x (SuperSpeed)";
            }
            return "unknown (" + ex[23] + ")";
        }
    }

    // IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES: bit 3 = Type-C connector
    static string PortType(string hub, int port) {
        using (var h = CreateFile(hub, 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (h.IsInvalid) return "?";
            var v = new byte[512]; BitConverter.GetBytes(port).CopyTo(v, 0); int r;
            if (!DeviceIoControl(h, 0x220458, v, v.Length, v, v.Length, out r, IntPtr.Zero)) return "?";
            return (BitConverter.ToUInt32(v, 8) & 8) != 0 ? "USB-C" : "USB-A";
        }
    }

    public static UsbLink Get() {
        var link = new UsbLink();
        int dev = FindQuest(); if (dev == 0) return link;
        link.Connected = true;
        int port = Address(dev), hub = Parent(dev);
        string pcPort = null;
        int cur = hub;
        while (cur != 0 && Id(cur).IndexOf("ROOT_HUB", StringComparison.OrdinalIgnoreCase) < 0) {
            link.Hubs.Add(BusDesc(cur) ?? "hub");
            int p = Parent(cur);
            if (p != 0 && Id(p).IndexOf("ROOT_HUB", StringComparison.OrdinalIgnoreCase) >= 0) pcPort = PortType(HubPath(p), Address(cur));
            cur = p;
        }
        link.PcPort = pcPort ?? PortType(HubPath(hub), port);
        string name = BusDesc(dev);
        link.Device = string.IsNullOrEmpty(name) || name.Contains("Composite") ? "Meta Quest" : name;
        link.Speed = Speed(HubPath(hub), port);
        return link;
    }
}
