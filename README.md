# NextBoot

**A tiny Windows GUI for choosing the *next* boot — plus real UEFI boot-order editing.**

Reads the firmware boot list straight from UEFI NVRAM (`Boot####` variables, not the OpenCore config), lets you pick one entry for a single boot (`BootNext`), or reorder the permanent list (`BootOrder`). Built for a **Windows 11 + macOS (OpenCore/Hackintosh)** dual-boot machine: switch over for a single boot, or change the default system — without racing the F11 key at power-on (2.4G/bluetooth keyboards often aren't visible to the firmware at all). Also works for Windows + Linux.

Zero third-party dependencies — plain Win32 P/Invoke.

## 动机 / Why

一台机器双系统：**Windows 11 + macOS（Hackintosh / OpenCore）**，日常要来回切。原本的做法都很别扭：

- 开机狂按 F11 / ESC 抢固件启动菜单——慢，而且 2.4G / 蓝牙键盘在固件阶段根本不被识别，按了等于没按；
- 进 BIOS 改默认启动项——为了切一次系统去动全局配置，切几轮就忘了当前默认是谁；
- 用 OpenCore 自己的启动菜单——前提是已经进了 OpenCore，解决不了「连固件这一关都过不去」。

所以写了 NextBoot，把这件事变成两个动作：

- **临时切一次**（Windows → macOS，或者反过来）：选中目标 →「设为 BootNext」→ 重启，一次性生效，之后自动回到默认系统；
- **换默认系统**：上移 / 下移 →「保存为永久启动顺序」，相当于图形化操作固件的启动顺序（底层就是 `BootNext` / `BootOrder` 两个标准 UEFI 变量，等价于 `bcdedit /set {fwbootmgr} bootsequence`）。

设计上刻意保守：只写这两个变量，不删启动项、不碰 Secure Boot 相关变量，每次写前自动全量备份 NVRAM。

## 中文说明

NextBoot 用一个图形界面完成 `bcdedit /set {fwbootmgr} bootsequence` 能做的事，但做得更完整：

- **读取的是主板固件（UEFI）启动列表**（`BootOrder` + 各 `Boot####` 变量），不是 OpenCore 自己的启动项。
- 选择一项 → 写入 **`BootNext`**：**仅下一次开机生效**，固件用完自动删除该变量，之后恢复默认顺序。
- 调整列表顺序 → 写入 **`BootOrder`**：**永久默认启动顺序**（写前自动全量备份 NVRAM，写后回读校验）。

## 功能 / Features

- 列出启动项：`Boot0000 Windows Boot Manager`、`Boot0002 UEFI OS`（OpenCore）等，显示描述、`\EFI\...\xx.efi` 路径、属性（Active/Hidden）。
- **「顺序」列**：显示每个条目在 `BootOrder` 中的位次；不在 BootOrder 里的条目显示 `—`，不参与排序。
- **「所在磁盘」列**：从设备路径的 MEDIA_HD 节点取 GPT 分区 GUID，经 WMI `MSFT_Partition` 换算成「磁盘X·分区Y」，再附 `MSFT_Disk` 的磁盘型号（例如 `Disk Model 1TB`），方便区分多块盘。
- 高亮「本次启动」(`BootCurrent`, 红) 与「已设置 BootNext」(`BootNext`, 绿)。
- 按钮：设为 BootNext ｜ 上移 ▲ ｜ 下移 ▼ ｜ 保存为永久启动顺序 ｜ 清除 BootNext ｜ 刷新 ｜ 重启生效。
- 双击某行 = 设为 BootNext。
- 状态栏：固件超时、当前顺序、本次启动项、BootNext、未保存的顺序改动提示。

## 实现 / How it works

纯 Win32 P/Invoke，`NextBoot/MainForm.cs` 里的 `Uefi` 静态类：

| 操作 | API | UEFI 语义 |
|---|---|---|
| 开启权限 | `AdjustTokenPrivileges(SeSystemEnvironmentPrivilege)` | 变量读写的先决条件（非管理员连读都不行） |
| 枚举变量 | `NtEnumerateSystemEnvironmentValuesEx(2, …)` | 记录头 = next / ValueOffset / ValueSize / attrs / GUID / 名字(UTF-16)，过滤 `EFI_GLOBAL_VARIABLE` = `8BE4DF61-93CA-11D2-AA0D-00E098032B8C` |
| 读 `BootCurrent`/`BootNext`/`Timeout`/`BootOrder` | `GetFirmwareEnvironmentVariableExW` | 变量不存在返回 null，不算错误 |
| 解析启动项 | 自行解析 `EFI_LOAD_OPTION`（u32 属性 + u16 FilePathListLength + UTF-16 描述 + 设备路径节点表） | 每个 `Boot####` |
| 设一次性启动 | `SetFirmwareEnvironmentVariableExW("BootNext", …, 2 字节, 0x7)` | NV\|BS\|RT |
| 设永久顺序 | 同上写 `BootOrder`（保持原属性位），写后回读校验 | — |
| 清除 BootNext | 0 长度写入即删除变量 | — |

`BootNext` 与 `bcdedit /set {fwbootmgr} bootsequence {GUID}` 等价（bcdedit 底层就是写固件 BootNext），但这里不依赖本地化文本解析，能直接显示真正的 `.efi` 路径。

## 构建 / Build

```powershell
cd NextBoot
dotnet build -c Release
# 输出: bin\Release\net10.0-windows\NextBoot.exe
```

- 需要 .NET 10 Windows Desktop Runtime。
- `app.manifest` 声明 `requireAdministrator`（写 UEFI 变量需要 `SeSystemEnvironmentPrivilege`）。
- 备份文件默认落在 `%USERPROFILE%\Documents\NextBoot\backups\`（每次写入 BootOrder 前自动全量备份 NVRAM）。

## 免 UAC 启动（可选）

管理员权限绕不过，但可以只确认一次 UAC：双击 `setup-noUAC.bat` → 把 exe 注册成最高权限计划任务 `NextBoot`,并把桌面快捷方式改为 `schtasks /run /tn NextBoot`,之后启动不再弹窗。

## 诊断工具 / Tools

`tools/UefiDiag` — 只读快照：枚举全部 UEFI 变量并落盘（含 manifest），输出 `BootOrder` 与各 `Boot####` 的解析结果。
`tools/UefiClean` — 清理固件生成的冗余硬件式启动项（描述以 `UEFI:` 开头且设备路径无 FILE_PATH 节点）。护栏：不删当前启动项、同一 GPT 分区必须另有带 FILE_PATH 的条目兜底；执行前自动全量备份。

两个工具都以管理员运行，结果写在 exe 同目录的 `out.txt`。

## 注意 / Caveats

- **固件可能重排或重建启动项**：在 AMI 固件主板上实测，开机时固件会对账 NVRAM——此前用「重命名描述」（改 `Boot####` 里的描述字段）写入后回读正确，但重启即被固件丢弃并重建条目（多出一条 + BootOrder 重排）。因此该功能已从程序移除；「永久顺序」写入后也建议重启一次确认是否被固件覆盖。**日常一次性切换优先用 BootNext**。
- 只写 `BootNext` / `BootOrder` 两个标准变量；不删除启动项（`UefiClean` 除外，且有上述护栏），不动 Secure Boot 相关变量。
- 若固件启用了“快速启动/跳过启动菜单”，BootNext 是否被尊重取决于固件。
- 2.4G/蓝牙双模键盘在固件阶段收不到按键，进入 OpenCore 菜单后需要有线键盘操作 —— 这正是 NextBoot 想解决的问题。

## License

MIT
