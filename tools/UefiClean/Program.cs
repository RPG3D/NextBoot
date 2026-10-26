using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// 只删除「固件生成的硬件式启动项」：描述以 "UEFI:" 开头、设备路径里没有 FILE_PATH 节点。
// 安全护栏：不删 BootCurrent；同一 GPT 分区必须还有另一条带 FILE_PATH 的条目兜底；否则跳过。
// 执行前自动全量备份 NVRAM，执行后回读校验。

var log = new StringBuilder();
int exit = 0;
try { Run(log); }
catch (Exception e) { log.AppendLine("FATAL: " + e); exit = 1; }
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "out.txt"), log.ToString());
Environment.Exit(exit);

static void Run(StringBuilder log)
{
    var global = new Guid("8BE4DF61-93CA-11D2-AA0D-00E098032B8C");
    EnablePrivilege();

    var vars = Enumerate(global, log, backup: true);

    // ---- 读 BootOrder ----
    var orderVar = vars.FirstOrDefault(v => v.Name == "BootOrder");
    if (orderVar == null) { log.AppendLine("ABORT: BootOrder 不存在"); return; }
    var order = new List<ushort>();
    for (int i = 0; i + 1 < orderVar.Data.Length; i += 2)
        order.Add(BitConverter.ToUInt16(orderVar.Data, i));
    var curVar = vars.FirstOrDefault(v => v.Name == "BootCurrent");
    ushort? bootCurrent = curVar is { Data.Length: >= 2 } ? BitConverter.ToUInt16(curVar.Data, 0) : null;
    log.AppendLine($"BootCurrent = {(bootCurrent.HasValue ? $"Boot{bootCurrent:X4}" : "?")}");
    log.AppendLine("BootOrder = " + string.Join(",", order.Select(i => $"Boot{i:X4}")));

    // ---- 解析所有 Boot#### ----
    var entries = new List<Entry>();
    foreach (var v in vars)
    {
        if (!TryIndex(v.Name, out var idx)) continue;
        var parsed = Parse(v.Data);
        entries.Add(new Entry(idx, v.Name, parsed.Desc, parsed.FilePath, parsed.PartGuid, v.Data, v.Attributes));
        log.AppendLine($"  Boot{idx:X4}  desc=\"{parsed.Desc}\"  file={parsed.FilePath ?? "<none>"}  part={parsed.PartGuid?.ToString() ?? "-"}");
    }

    // ---- 找候选（无效项） ----
    var candidates = new List<Entry>();
    foreach (var e in entries)
    {
        if (e.FilePath != null) continue;                        // 有 EFI 路径 = 有效项
        if (!e.Desc.StartsWith("UEFI:", StringComparison.OrdinalIgnoreCase)) continue;
        if (bootCurrent == e.Index) { log.AppendLine($"  SKIP Boot{e.Index:X4}: 是当前启动项"); continue; }
        var sibling = entries.Any(o => o.Index != e.Index && o.FilePath != null &&
                                       o.PartGuid != null && o.PartGuid == e.PartGuid);
        if (!sibling) { log.AppendLine($"  SKIP Boot{e.Index:X4}: 同分区没有其他有效条目，删了会丢启动路径"); continue; }
        candidates.Add(e);
    }

    if (candidates.Count == 0) { log.AppendLine("RESULT: 没有需要清理的无效条目。"); return; }

    // ---- 逐个删除 + 同步 BootOrder ----
    foreach (var c in candidates)
    {
        log.AppendLine($"DELETE Boot{c.Index:X4} \"{c.Desc}\" (无 FILE_PATH，同分区有有效条目兜底)");
        DeleteVariable(c.Name, c.Attributes);
        order.RemoveAll(i => i == c.Index);
    }

    var newOrder = new byte[order.Count * 2];
    for (int i = 0; i < order.Count; i++) BitConverter.GetBytes(order[i]).CopyTo(newOrder, i * 2);
    SetVariable("BootOrder", newOrder);
    log.AppendLine("新 BootOrder = " + string.Join(",", order.Select(i => $"Boot{i:X4}")));

    // ---- 回读校验 ----
    var after = Enumerate(global, log, backup: false);
    var afterOrder = after.FirstOrDefault(v => v.Name == "BootOrder");
    var afterList = new List<ushort>();
    if (afterOrder != null)
        for (int i = 0; i + 1 < afterOrder.Data.Length; i += 2)
            afterList.Add(BitConverter.ToUInt16(afterOrder.Data, i));
    log.AppendLine("校验 BootOrder = " + string.Join(",", afterList.Select(i => $"Boot{i:X4}")));

    bool allGone = true;
    foreach (var c in candidates)
    {
        bool still = after.Any(v => v.Name == c.Name);
        log.AppendLine($"  校验 {c.Name}: {(still ? "仍存在 ✗" : "已删除 ✓")}");
        if (still) allGone = false;
    }
    log.AppendLine(allGone ? "RESULT: 清理完成。" : "RESULT: 部分条目未能删除。");
}

static bool TryIndex(string name, out ushort idx)
{
    idx = 0;
    return name.Length == 8 && name.StartsWith("Boot") &&
           ushort.TryParse(name[4..], System.Globalization.NumberStyles.HexNumber, null, out idx);
}

static (string Desc, string? FilePath, Guid? PartGuid) Parse(byte[] d)
{
    string desc = "";
    string? file = null;
    Guid? partGuid = null;
    if (d.Length < 6) return (desc, file, partGuid);

    ushort fpLen = BitConverter.ToUInt16(d, 4);
    int i = 6, st = 6;
    while (i + 1 < d.Length && (d[i] != 0 || d[i + 1] != 0)) i += 2;
    desc = Encoding.Unicode.GetString(d, st, i - st);
    i += 2;

    int end = Math.Min(i + fpLen, d.Length), p = i;
    while (p + 4 <= end)
    {
        byte ty = d[p], su = d[p + 1];
        ushort ln = BitConverter.ToUInt16(d, p + 2);
        if (ln < 4) break;
        if (ty == 0x04 && su == 0x01 && ln >= 42 && d[p + 41] == 0x02) // HD, GPT
            partGuid = new Guid(d.Skip(p + 24).Take(16).ToArray());
        if (ty == 0x04 && su == 0x04 && ln > 4)
        {
            var s = Encoding.Unicode.GetString(d, p + 4, ln - 4);
            int z = s.IndexOf('\0'); if (z >= 0) s = s[..z];
            if (s.Length > 0) file = s;
        }
        if (ty == 0x7F) break;
        p += ln;
    }
    return (desc, file, partGuid);
}

static List<Var> Enumerate(Guid global, StringBuilder log, bool backup)
{
    string? dir = null;
    if (backup)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NextBoot", "backups");
        Directory.CreateDirectory(root);
        dir = Path.Combine(root, "UEFI-Before-Clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        log.AppendLine("备份目录: " + dir);
    }

    int size = 0x40000;
    var buf = new byte[size];
    int status;
    while (true)
    {
        status = NtEnumerateSystemEnvironmentValuesEx(2, buf, ref size);
        if (status == unchecked((int)0xC0000023)) { buf = new byte[size]; continue; }
        break;
    }
    if (status != 0) throw new Win32Exception(RtlNtStatusToDosError(status), $"枚举失败 0x{status:X8}");

    var list = new List<Var>();
    var manifest = new StringBuilder();
    int off = 0, count = 0;
    while (off + 32 <= size && count < 400)
    {
        uint next = BitConverter.ToUInt32(buf, off);
        uint vOff = BitConverter.ToUInt32(buf, off + 4);
        uint vSize = BitConverter.ToUInt32(buf, off + 8);
        uint attrs = BitConverter.ToUInt32(buf, off + 12);
        var vendor = new Guid(buf.Skip(off + 16).Take(16).ToArray());
        int n = off + 32;
        while (n + 1 < size && (buf[n] != 0 || buf[n + 1] != 0)) n += 2;
        string name = Encoding.Unicode.GetString(buf, off + 32, n - (off + 32));

        if (vSize > 0 && off + (int)vOff + vSize <= size)
        {
            var data = new byte[vSize];
            Array.Copy(buf, off + (int)vOff, data, 0, vSize);
            if (dir != null)
            {
                var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                File.WriteAllBytes(Path.Combine(dir, $"{safe}__{vendor:N}.bin"), data);
                manifest.AppendLine($"{name}\t{vendor}\t0x{attrs:X}\t{vSize}");
            }
            if (vendor == global) list.Add(new Var(name, attrs, data));
        }
        if (next == 0) break;
        off += (int)next;
        count++;
    }
    if (dir != null) File.WriteAllText(Path.Combine(dir, "manifest.tsv"), manifest.ToString());
    return list;
}

static void EnablePrivilege()
{
    OpenProcessToken(GetCurrentProcess(), 0x28, out var tok);
    LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out var luid);
    var tp = new TP { Count = 1, Luid = luid, Attr = 2 };
    if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
        throw new Win32Exception(Marshal.GetLastWin32Error(), "需要管理员权限");
}

static void SetVariable(string name, byte[] data)
{
    var global = new Guid("8BE4DF61-93CA-11D2-AA0D-00E098032B8C");
    if (!SetFirmwareEnvironmentVariableExW(name, global.ToString("B"), data, (uint)data.Length, 0x7))
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"写 {name} 失败");
}

static void DeleteVariable(string name, uint attrs)
{
    var global = new Guid("8BE4DF61-93CA-11D2-AA0D-00E098032B8C");
    if (!SetFirmwareEnvironmentVariableExW(name, global.ToString("B"), null, 0, attrs == 0 ? 0x7u : attrs))
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"删除 {name} 失败");
}

[DllImport("ntdll.dll")] static extern int NtEnumerateSystemEnvironmentValuesEx(int cls, byte[] buf, ref int size);
[DllImport("ntdll.dll")] static extern int RtlNtStatusToDosError(int status);
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetFirmwareEnvironmentVariableExW(string name, string guid, byte[]? buffer, uint size, uint attrs);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool LookupPrivilegeValue(string? s, string n, out LUID l);
[DllImport("advapi32.dll", SetLastError = true)]
static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP n, int l, IntPtr p, IntPtr r);
[DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
struct LUID { public uint Low; public int High; }
struct TP { public int Count; public LUID Luid; public int Attr; }

record Var(string Name, uint Attributes, byte[] Data);
record Entry(ushort Index, string Name, string Desc, string? FilePath, Guid? PartGuid, byte[] Data, uint Attributes);

