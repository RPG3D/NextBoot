using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NextBoot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 自检：用当前 BootOrder 原样写回一次（空操作），验证写入/校验链路
        if (args.Length > 0 && args[0] == "--selftest-order")
        {
            var outPath = Path.Combine(AppContext.BaseDirectory, "order-selftest.txt");
            try
            {
                var snap = Uefi.ReadSnapshot();
                var bak = Uefi.BackupAll();
                var got = Uefi.SaveBootOrder(snap.LoadOrder);
                File.WriteAllText(outPath,
                    "RESULT: OK\n" +
                    "backup=" + bak + "\n" +
                    "saved=" + string.Join(",", got.Select(i => $"Boot{i:X4}")) + "\n");
            }
            catch (Exception ex)
            {
                File.WriteAllText(outPath, "RESULT: FAIL\n" + ex);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("未处理异常：\n" + e.Exception.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
    }
}

internal sealed record BootEntry(
    ushort Index,
    string Description,
    string? FilePath,
    string? Disk,
    uint Attributes,
    bool InOrder = true);

internal sealed class FirmwareSnapshot
{
    public List<BootEntry> Entries { get; init; } = new();
    public ushort? BootCurrent { get; init; }
    public ushort? BootNext { get; init; }
    public ushort? Timeout { get; init; }
    public ushort[] LoadOrder { get; init; } = Array.Empty<ushort>();
}

/// <summary>
/// 直连 Windows UEFI 变量 API（Get/SetFirmwareEnvironmentVariableEx + NtEnumerateSystemEnvironmentValuesEx），
/// 不经过第三方库。所有变量都位于 EFI_GLOBAL_VARIABLE。
/// </summary>
internal static class Uefi
{
    public static readonly Guid GlobalVariable = new("8BE4DF61-93CA-11D2-AA0D-00E098032B8C"); // EFI_GLOBAL_VARIABLE
    private const uint NvBsRt = 0x7; // NON_VOLATILE | BOOTSERVICE_ACCESS | RUNTIME_ACCESS

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public LUID Luid;
        public int Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtEnumerateSystemEnvironmentValuesEx(int informationClass, byte[] buffer, ref int bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariableExW(string name, string guid, byte[] buffer, uint size, ref uint attributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetFirmwareEnvironmentVariableExW(string name, string guid, byte[]? buffer, uint size, uint attributes);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int bufferLen, IntPtr prev, IntPtr ret);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    #endregion

    public static void EnsurePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* TOKEN_QUERY|TOKEN_ADJUST_PRIVILEGES */, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken 失败");
        if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out var luid))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue 失败");
        var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
        if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges 失败（需要管理员）");
    }

    public sealed record RawVariable(string Name, Guid Vendor, uint Attributes, byte[] Data);

    /// <summary>枚举固件变量（informationClass = EnvironmentValues）。只保留 EFI_GLOBAL_VARIABLE。</summary>
    public static List<RawVariable> EnumerateVariables()
    {
        int size = 0x10000;
        byte[] buf = new byte[size];
        int status;
        while (true)
        {
            status = NtEnumerateSystemEnvironmentValuesEx(2, buf, ref size);
            if (status == unchecked((int)0xC0000023)) { buf = new byte[size]; continue; } // STATUS_BUFFER_TOO_SMALL
            break;
        }
        if (status != 0)
            throw new Win32Exception(RtlNtStatusToDosError(status), $"枚举 UEFI 变量失败 (NTSTATUS 0x{status:X8})");

        var result = new List<RawVariable>();
        int offset = 0;
        while (offset + 32 <= size)
        {
            uint next = BitConverter.ToUInt32(buf, offset);
            uint valueOffset = BitConverter.ToUInt32(buf, offset + 4);
            uint valueSize = BitConverter.ToUInt32(buf, offset + 8);
            uint attrs = BitConverter.ToUInt32(buf, offset + 12);
            var vendor = new Guid(buf.Skip(offset + 16).Take(16).ToArray());

            // 记录名 = offset+32 起的 UTF-16，到 0 结束
            int n = offset + 32;
            while (n + 1 < size && (buf[n] != 0 || buf[n + 1] != 0)) n += 2;
            string name = Encoding.Unicode.GetString(buf, offset + 32, n - (offset + 32));

            if (vendor == GlobalVariable && valueSize > 0 && offset + (int)valueOffset + valueSize <= size)
            {
                var data = new byte[valueSize];
                Array.Copy(buf, offset + (int)valueOffset, data, 0, valueSize);
                result.Add(new RawVariable(name, vendor, attrs, data));
            }

            if (next == 0) break;
            offset += (int)next;
        }
        return result;
    }

    public static byte[]? GetVariable(string name)
    {
        var buf = new byte[1024];
        uint attrs = 0;
        uint len = GetFirmwareEnvironmentVariableExW(name, GlobalVariable.ToString("B"), buf, (uint)buf.Length, ref attrs);
        if (len == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 203 /* ERROR_ENVVAR_NOT_FOUND */ || err == 1 || err == 13) return null;
            throw new Win32Exception(err, $"读取变量 {name} 失败");
        }
        var data = new byte[len];
        Array.Copy(buf, data, len);
        return data;
    }

    public static void SetVariable(string name, byte[]? data, uint attrs = NvBsRt)
    {
        if (!SetFirmwareEnvironmentVariableExW(name, GlobalVariable.ToString("B"), data, (uint)(data?.Length ?? 0), attrs))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"写入变量 {name} 失败");
    }

    /// <summary>读变量的属性位（写回时保持原属性，避免改变变量的持久性语义）。</summary>
    public static uint? GetVariableAttributes(string name)
    {
        var buf = new byte[4];
        uint attrs = 0;
        uint len = GetFirmwareEnvironmentVariableExW(name, GlobalVariable.ToString("B"), buf, (uint)buf.Length, ref attrs);
        if (len == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 203 || err == 1 || err == 13) return null;
            throw new Win32Exception(err, $"读取变量 {name} 属性失败");
        }
        return attrs;
    }

    /// <summary>把新的启动顺序永久写入 BootOrder；写后回读校验。</summary>
    public static ushort[] SaveBootOrder(ushort[] order)
    {
        EnsurePrivilege();
        if (order.Length == 0) throw new InvalidOperationException("启动顺序不能为空");

        var data = new byte[order.Length * 2];
        for (int i = 0; i < order.Length; i++) BitConverter.GetBytes(order[i]).CopyTo(data, i * 2);

        uint attrs = GetVariableAttributes("BootOrder") ?? NvBsRt;
        SetVariable("BootOrder", data, attrs);

        var back = GetVariable("BootOrder") ?? Array.Empty<byte>();
        var got = new ushort[back.Length / 2];
        for (int i = 0; i < got.Length; i++) got[i] = BitConverter.ToUInt16(back, i * 2);
        if (!got.SequenceEqual(order))
            throw new InvalidOperationException(
                $"回读校验失败：期望 {string.Join(",", order.Select(i => $"Boot{i:X4}"))}，" +
                $"实际 {string.Join(",", got.Select(i => $"Boot{i:X4}"))}。" +
                "可能是固件拒绝或已被重排。");
        return got;
    }

    /// <summary>把当前所有 EFI 全局变量（含 Boot####/BootOrder）落盘备份，返回目录。</summary>
    public static string BackupAll()
    {
        EnsurePrivilege();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NextBoot", "backups");
        Directory.CreateDirectory(root);
        string dir = Path.Combine(root, "UEFI-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        var manifest = new StringBuilder();
        foreach (var v in EnumerateVariables())
        {
            var safe = string.Join("_", v.Name.Split(Path.GetInvalidFileNameChars()));
            try { File.WriteAllBytes(Path.Combine(dir, $"{safe}__{v.Vendor:N}.bin"), v.Data); } catch { }
            manifest.AppendLine($"{v.Name}\t{v.Vendor}\t0x{v.Attributes:X}\t{v.Data.Length}");
        }
        File.WriteAllText(Path.Combine(dir, "manifest.tsv"), manifest.ToString());
        return dir;
    }

    // ---- EFI_LOAD_OPTION ----

    public static (string Description, string? FilePath, string? PartHint, uint Attributes) ParseLoadOption(byte[] d)
    {
        uint attr = BitConverter.ToUInt32(d, 0);
        ushort fpLen = BitConverter.ToUInt16(d, 4);

        int i = 6;
        int start = i;
        while (i + 1 < d.Length && (d[i] != 0 || d[i + 1] != 0)) i += 2;
        string desc = Encoding.Unicode.GetString(d, start, i - start);
        i += 2;

        // 设备路径列表：fpLen 字节，逐节点 (Type, SubType, Length:2, Data)
        string? file = null;
        string? partHint = null;
        int end = Math.Min(i + fpLen, d.Length);
        int p = i;
        while (p + 4 <= end)
        {
            byte type = d[p], sub = d[p + 1];
            ushort len = BitConverter.ToUInt16(d, p + 2);
            if (len < 4) break;
            if (type == 0x04 && sub == 0x04 && len > 4) // MEDIA / FILE_PATH
            {
                var s = Encoding.Unicode.GetString(d, p + 4, len - 4);
                int z = s.IndexOf('\0');
                if (z >= 0) s = s[..z];
                if (s.Length > 0 && s.All(c => !char.IsControl(c))) { file = s; break; }
            }
            if (type == 0x04 && sub == 0x01 && len >= 42) // MEDIA / HD（分区定位）
                partHint ??= DescribeHdNode(d, p);
            if (type == 0x7F) break; // END_OF_ENTIRE
            p += len;
        }
        return (desc, file, partHint, attr);
    }

    // MEDIA_HD 节点（42B）: u32 分区号 @+4, 签名 16B @+24, 格式 @+40(1=MBR,2=GPT), 签名类型 @+41
    private static string? DescribeHdNode(byte[] d, int p)
    {
        try
        {
            uint partNo = BitConverter.ToUInt32(d, p + 4);
            byte format = d[p + 40], sigType = d[p + 41];
            if (format == 0x02 && sigType == 0x02) // GPT 分区 GUID
            {
                var guid = new Guid(d.Skip(p + 24).Take(16).ToArray());
                var loc = DiskMap.Lookup(guid);
                if (!loc.HasValue)
                    return $"GPT分区{partNo}";
                var disk = $"磁盘{loc.Value.disk}·分区{loc.Value.part}";
                var name = DiskMap.DiskName(loc.Value.disk);
                return name == null ? disk : $"{disk} {name}";
            }
            if (format == 0x01)
                return $"MBR分区{partNo}";
            return $"分区{partNo}";
        }
        catch { return null; }
    }

    public static FirmwareSnapshot ReadSnapshot()
    {
        EnsurePrivilege();

        ushort? bootNext = ReadU16("BootNext");
        ushort? bootCurrent = ReadU16("BootCurrent");
        ushort? timeout = ReadU16("Timeout");
        ushort[] order = Array.Empty<ushort>();
        var orderRaw = GetVariable("BootOrder");
        if (orderRaw is { Length: >= 2 } && orderRaw.Length % 2 == 0)
        {
            order = new ushort[orderRaw.Length / 2];
            for (int i = 0; i < order.Length; i++) order[i] = BitConverter.ToUInt16(orderRaw, i * 2);
        }

        var byIndex = new Dictionary<ushort, byte[]>();
        foreach (var v in EnumerateVariables())
        {
            if (v.Name.Length == 8 && v.Name.StartsWith("Boot") &&
                ushort.TryParse(v.Name[4..], System.Globalization.NumberStyles.HexNumber, null, out var idx))
            {
                byIndex[idx] = v.Data;
            }
        }

        var entries = new List<BootEntry>();
        foreach (var idx in order)
        {
            if (!byIndex.Remove(idx, out var data)) continue;
            try
            {
                var (desc, file, part, attr) = ParseLoadOption(data);
                entries.Add(new BootEntry(idx, string.IsNullOrWhiteSpace(desc) ? "（无描述）" : desc, file, part, attr));
            }
            catch (Exception ex)
            {
                entries.Add(new BootEntry(idx, $"（解析失败：{ex.Message}）", null, null, 0));
            }
        }
        // BootOrder 里没有、但固件里存在的条目，排在后面
        foreach (var (idx, data) in byIndex.OrderBy(kv => kv.Key))
        {
            try
            {
                var (desc, file, part, attr) = ParseLoadOption(data);
                entries.Add(new BootEntry(idx, desc + "（不在 BootOrder）", file, part, attr, InOrder: false));
            }
            catch { /* 忽略 */ }
        }

        return new FirmwareSnapshot
        {
            Entries = entries,
            BootCurrent = bootCurrent,
            BootNext = bootNext,
            Timeout = timeout,
            LoadOrder = order,
        };
    }

    private static ushort? ReadU16(string name)
    {
        var d = GetVariable(name);
        return d is { Length: >= 2 } ? BitConverter.ToUInt16(d, 0) : null;
    }
}

/// <summary>GPT 分区 GUID → (磁盘号, 分区号)，磁盘号 → 型号名；经 WMI 查询，首次用时构建并缓存。</summary>
internal static class DiskMap
{
    private static Dictionary<Guid, (int disk, int part)>? _map;
    private static Dictionary<int, string>? _names;
    private static bool _failed;

    public static (int disk, int part)? Lookup(Guid partitionGuid)
    {
        if (_map == null && !_failed) Build();
        return _map != null && _map.TryGetValue(partitionGuid, out var v) ? v : null;
    }

    public static string? DiskName(int disk)
    {
        if (_map == null && !_failed) Build();
        return _names?.GetValueOrDefault(disk);
    }

    private static void Build()
    {
        try
        {
            var map = new Dictionary<Guid, (int, int)>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DiskNumber, PartitionNumber, Guid FROM MSFT_Partition");
            foreach (var o in searcher.Get())
            {
                try
                {
                    string? g = o["Guid"]?.ToString();
                    if (string.IsNullOrEmpty(g)) continue;
                    var guid = Guid.Parse(g.Trim('{', '}'));
                    map[guid] = (Convert.ToInt32(o["DiskNumber"]), Convert.ToInt32(o["PartitionNumber"]));
                }
                catch { /* 单条解析失败不影响整体 */ }
            }
            _map = map;

            var names = new Dictionary<int, string>();
            using var ds = new System.Management.ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT Number, FriendlyName, Model FROM MSFT_Disk");
            foreach (var o in ds.Get())
            {
                try
                {
                    int no = Convert.ToInt32(o["Number"]);
                    string name = (o["FriendlyName"] ?? o["Model"])?.ToString()?.Trim() ?? "";
                    if (name.Length > 0) names[no] = name;
                }
                catch { }
            }
            _names = names;
        }
        catch
        {
            _failed = true; // WMI 不可用时退化为显示 GPT分区N
        }
    }
}

internal sealed class MainForm : Form
{
    private readonly ListView _list;
    private readonly Button _btnSet, _btnClear, _btnRefresh, _btnReboot;
    private readonly Button _btnUp, _btnDown, _btnSaveOrder;
    private readonly Label _status;
    private List<BootEntry> _entries = new();
    private FirmwareSnapshot? _snap;
    private bool _dirty;

    public MainForm()
    {
        Text = "BootNext — UEFI 固件启动项一次性选择器";
        Font = new Font("Microsoft YaHei UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        // 客户区 = 列表(表头+10行) + 状态栏 + 按钮区；列表自带滚动条
        ClientSize = new Size(1180, 456);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* 无图标不致命 */ }

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
        };
        _list.Columns.Add("顺序", 56);
        _list.Columns.Add("启动项", 96);
        _list.Columns.Add("描述", 180);
        _list.Columns.Add("EFI 程序路径", 240);
        _list.Columns.Add("所在磁盘", 268);
        _list.Columns.Add("属性", 80);
        _list.DoubleClick += (_, _) => SetBootNext();

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 104,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 12, 10, 8),
        };
        _btnSet = MakeButton("设为 BootNext（仅下次一次）", 200);
        _btnUp = MakeButton("上移 ▲", 84);
        _btnDown = MakeButton("下移 ▼", 84);
        _btnSaveOrder = MakeButton("保存为永久启动顺序", 170);
        _btnClear = MakeButton("清除 BootNext", 130);
        _btnRefresh = MakeButton("刷新", 84);
        _btnReboot = MakeButton("重启生效", 110);
        _btnSet.Click += (_, _) => SetBootNext();
        _btnUp.Click += (_, _) => MoveSelected(-1);
        _btnDown.Click += (_, _) => MoveSelected(+1);
        _btnSaveOrder.Click += (_, _) => SaveBootOrder();
        _btnClear.Click += (_, _) => ClearBootNext();
        _btnRefresh.Click += (_, _) => RefreshAll();
        _btnReboot.Click += (_, _) => AskReboot();
        bottom.Controls.AddRange(new Control[] { _btnSet, _btnUp, _btnDown, _btnSaveOrder, _btnClear, _btnRefresh, _btnReboot });

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            AutoEllipsis = true,
        };

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(bottom);

        RefreshAll();
    }

    /// <summary>按钮自动量宽（含内边距），避免不同字号/DPI 下文字被截断。</summary>
    private static Button MakeButton(string text, int minWidth) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(minWidth, 46),
        Padding = new Padding(12, 0, 12, 0),
        Margin = new Padding(3, 3, 6, 3),
    };

    private void RefreshAll()
    {
        try
        {
            var snap = Uefi.ReadSnapshot();
            _snap = snap;
            _entries = snap.Entries.ToList();
            _dirty = false;
            Render(selectIndex: snap.BootNext ?? (snap.Entries.Count > 0 ? snap.Entries[0].Index : (ushort)0xFFFF));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取 UEFI 启动信息失败：\n" + ex.Message +
                "\n\n请确认以管理员身份运行，且主板 UEFI 变量可访问。",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>按 _entries 当前顺序重绘列表（不重新读固件，便于保存前预览顺序）。</summary>
    private void Render(ushort selectIndex)
    {
        if (_snap == null) return;
        _list.BeginUpdate();
        _list.Items.Clear();
        int pos = 0;
        foreach (var entry in _entries)
        {
            pos++;
            var item = new ListViewItem(entry.InOrder ? pos.ToString() : "—") { Tag = entry };
            item.SubItems.Add($"Boot{entry.Index:X4}");
            item.SubItems.Add(entry.Description);
            item.SubItems.Add(entry.FilePath ?? "");
            item.SubItems.Add(entry.Disk ?? "");
            item.SubItems.Add(DescribeAttributes(entry.Attributes));
            if (_snap.BootCurrent == entry.Index) { item.BackColor = Color.MistyRose; }
            if (_snap.BootNext == entry.Index) { item.BackColor = Color.Honeydew; }
            if (_snap.BootCurrent == entry.Index) item.SubItems[1].Text += "  ◀ 本次";
            if (_snap.BootNext == entry.Index) item.SubItems[1].Text += "  ✔ BootNext";
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        var saved = string.Join(",", _snap.LoadOrder.Select(i => $"Boot{i:X4}"));
        var pending = string.Join(",", _entries.Where(e => e.InOrder).Select(e => $"Boot{e.Index:X4}"));
        _status.Text =
            $"固件超时 {(_snap.Timeout is ushort t ? $"{t}s" : "未知")} ｜ 当前顺序 {saved} ｜ 本次启动 Boot{(_snap.BootCurrent ?? 0xFFFF):X4}" +
            $" ｜ BootNext：{(_snap.BootNext is ushort bn ? $"Boot{bn:X4}" : "未设置")}" +
            (_dirty ? $" ｜ ⚠ 顺序已修改未保存 → {pending}" : "");

        int sel = _list.Items.Cast<ListViewItem>().ToList().FindIndex(i => i.Tag is BootEntry be && be.Index == selectIndex);
        if (sel < 0 && _list.Items.Count > 0) sel = 0;
        if (sel >= 0)
        {
            _list.Items[sel].Selected = true;
            _list.Items[sel].Focused = true;
            _list.EnsureVisible(sel);
        }
    }

    private void MoveSelected(int delta)
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BootEntry entry) return;
        int i = _entries.FindIndex(e => e.Index == entry.Index);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _entries.Count) return;
        if (!entry.InOrder || !_entries[j].InOrder)
        {
            MessageBox.Show(this, "只有原本在 BootOrder 里的条目才能参与排序。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        (_entries[i], _entries[j]) = (_entries[j], _entries[i]);
        _dirty = true;
        Render(entry.Index);
    }

    private void SaveBootOrder()
    {
        if (_snap == null) return;
        var target = _entries.Where(e => e.InOrder).Select(e => e.Index).ToArray();
        if (target.Length == 0) return;

        var preview = string.Join(" → ", _entries.Where(e => e.InOrder)
            .Select((e, n) => $"{n + 1}.{e.Description}"));
        var answer = MessageBox.Show(this,
            "即将把启动顺序永久写入固件（BootOrder）：\n\n" + preview +
            "\n\n注意：部分主板固件会在开机时重排该顺序，写入后仍可能被固化策略覆盖。\n继续？",
            "确认写入永久启动顺序", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        try
        {
            var bak = Uefi.BackupAll();
            var result = Uefi.SaveBootOrder(target);
            RefreshAll();
            MessageBox.Show(this,
                "已写入并校验通过：\n" + string.Join(" → ", result.Select(i => $"Boot{i:X4}")) +
                "\n\n备份：" + bak,
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "写入启动顺序失败：\n" + ex.Message,
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetBootNext()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BootEntry entry) return;
        try
        {
            Uefi.EnsurePrivilege();
            Uefi.SetVariable("BootNext", BitConverter.GetBytes(entry.Index));
            RefreshAll();
            MessageBox.Show(this,
                $"已设置 BootNext = Boot{entry.Index:X4}（{entry.Description}）。\n" +
                "下次重启将一次性从该项启动，之后自动恢复默认顺序（Windows）。",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "写入 BootNext 失败：\n" + ex.Message,
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearBootNext()
    {
        try
        {
            Uefi.EnsurePrivilege();
            Uefi.SetVariable("BootNext", null); // 0 长度 = 删除变量
            RefreshAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "清除 BootNext 失败：\n" + ex.Message,
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AskReboot()
    {
        var r = MessageBox.Show(this, "立即重启以进入所选启动项？", "重启",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.OK) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 5 /d p 4 1")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重启命令执行失败：\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string DescribeAttributes(uint a)
    {
        var parts = new List<string>();
        if ((a & 0x1) != 0) parts.Add("Active");
        if ((a & 0x2) != 0) parts.Add("Hidden");
        if ((a & 0x4) != 0) parts.Add("ForceReconnect");
        if ((a & 0x200) != 0) parts.Add("App");
        else if ((a & 0x100) != 0) parts.Add("Boot");
        return parts.Count > 0 ? string.Join("|", parts) : $"0x{a:X}";
    }
}
