using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

var sb = new StringBuilder();
try { Dump(sb); }
catch (Exception e) { sb.AppendLine("FATAL: " + e); }
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "out.txt"), sb.ToString());

static void Dump(StringBuilder sb)
{
    var global = new Guid("8BE4DF61-93CA-11D2-AA0D-00E098032B8C");

    OpenProcessToken(GetCurrentProcess(), 0x28, out var tok);
    LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out var luid);
    var tp = new TP { Count = 1, Luid = luid, Attr = 2 };
    bool ok = AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
    sb.AppendLine($"AdjustTokenPrivileges={ok} err={Marshal.GetLastWin32Error()}");

    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NextBoot", "snapshots");
    Directory.CreateDirectory(root);
    string dir = Path.Combine(root, "UEFI-Snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(dir);
    var manifest = new StringBuilder();
    sb.AppendLine("dump dir: " + dir);

    int size = 0x40000;
    var buf = new byte[size];
    int status = NtEnumerateSystemEnvironmentValuesEx(2, buf, ref size);
    sb.AppendLine($"NTSTATUS=0x{status:X8} size={size}");
    if (status != 0) return;

    var bootOpts = new List<(string name, Guid vendor, byte[] data)>();
    int offset = 0, count = 0;
    while (offset + 32 <= size && count < 400)
    {
        uint next = BitConverter.ToUInt32(buf, offset);
        uint vOff = BitConverter.ToUInt32(buf, offset + 4);
        uint vSize = BitConverter.ToUInt32(buf, offset + 8);
        uint attrs = BitConverter.ToUInt32(buf, offset + 12);
        var vendor = new Guid(buf.Skip(offset + 16).Take(16).ToArray());
        int n = offset + 32;
        while (n + 1 < size && (buf[n] != 0 || buf[n + 1] != 0)) n += 2;
        string name = Encoding.Unicode.GetString(buf, offset + 32, n - (offset + 32));

        if (vSize > 0 && offset + (int)vOff + vSize <= size)
        {
            var data = new byte[vSize];
            Array.Copy(buf, offset + (int)vOff, data, 0, vSize);
            var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            File.WriteAllBytes(Path.Combine(dir, $"{safe}__{vendor:N}.bin"), data);
            manifest.AppendLine($"{name}\t{vendor}\t0x{attrs:X}\t{vSize}");
            if (vendor == global && name.StartsWith("Boot") && name.Length == 8 &&
                ushort.TryParse(name[4..], System.Globalization.NumberStyles.HexNumber, null, out _))
                bootOpts.Add((name, vendor, data));
        }
        if (next == 0) break;
        offset += (int)next;
        count++;
    }
    File.WriteAllText(Path.Combine(dir, "manifest.tsv"), manifest.ToString());

    // BootOrder / BootCurrent / BootNext
    foreach (var nm in new[] { "BootOrder", "BootCurrent", "BootNext" })
    {
        var f = Path.Combine(dir, $"{nm}__{global:N}.bin");
        if (File.Exists(f))
        {
            var d = File.ReadAllBytes(f);
            if (nm == "BootOrder")
                sb.AppendLine("BootOrder = " + string.Join(",", Enumerable.Range(0, d.Length / 2)
                    .Select(i => $"Boot{BitConverter.ToUInt16(d, i * 2):X4}")) + $"  ({d.Length} bytes)");
            else
                sb.AppendLine($"{nm} = 0x{(d.Length >= 2 ? BitConverter.ToUInt16(d, 0) : 65535):X4}  ({d.Length} bytes)");
        }
        else sb.AppendLine($"{nm} = <absent>");
    }

    sb.AppendLine($"--- Boot#### entries ({bootOpts.Count}) ---");
    foreach (var (name, _, data) in bootOpts.OrderBy(b => b.name))
    {
        var (desc, file, attr) = Parse(data);
        sb.AppendLine($"{name}  attr=0x{attr:X}  len={data.Length}  desc=\"{desc}\"  path={file ?? "-"}");
    }
}

static (string, string?, uint) Parse(byte[] d)
{
    uint attr = BitConverter.ToUInt32(d, 0);
    ushort fpLen = BitConverter.ToUInt16(d, 4);
    int i = 6, st = 6;
    while (i + 1 < d.Length && (d[i] != 0 || d[i + 1] != 0)) i += 2;
    string desc = Encoding.Unicode.GetString(d, st, i - st);
    i += 2;
    string? file = null;
    int end = Math.Min(i + fpLen, d.Length), p = i;
    while (p + 4 <= end)
    {
        byte ty = d[p], su = d[p + 1]; ushort ln = BitConverter.ToUInt16(d, p + 2);
        if (ln < 4) break;
        if (ty == 4 && su == 4 && ln > 4)
        {
            var s = Encoding.Unicode.GetString(d, p + 4, ln - 4);
            int z = s.IndexOf('\0'); if (z >= 0) s = s[..z];
            if (s.Length > 0) file = s;
        }
        if (ty == 0x7F) break;
        p += ln;
    }
    return (desc, file, attr);
}

[DllImport("ntdll.dll")] static extern int NtEnumerateSystemEnvironmentValuesEx(int cls, byte[] buf, ref int size);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? s, string n, out LUID l);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP n, int l, IntPtr p, IntPtr r);
[DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
struct LUID { public uint Low; public int High; }
struct TP { public int Count; public LUID Luid; public int Attr; }
