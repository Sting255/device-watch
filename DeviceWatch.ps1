#Requires -Version 5.1
<#
    设备连接监控 DeviceWatch  ——  实时监测电脑上所有设备的连接 / 断联情况
    ------------------------------------------------------------------
    监测范围：
      1. 所有 PnP 设备（USB、蓝牙、硬盘、显示器、键鼠、摄像头、声卡、网卡…）的接入与断开
      2. 设备故障码（拔掉 vs 驱动出错，区分开）
      3. 网络适配器 Up / Down
      4. 外网连通性（网关 + 公网 IP 延迟），断网立刻告警
      5. 系统日志里与硬件相关的错误（USB 端口重置、磁盘超时、驱动加载失败等）

    发现异常时：Windows 通知弹窗 + 提示音 + 写日志。
    日志位置：脚本目录下 logs\ 文件夹。
#>
[CmdletBinding()]
param(
    # 显示控制台窗口输出（调试用）
    [switch]$Console,
    # 不显示托盘图标
    [switch]$NoTray,
    # 只记录不弹窗
    [switch]$NoNotify,
    # 学习模式：N 分钟内只记录不弹窗，用来找出到底哪个设备在反复掉
    [int]$LearnMinutes = 0,
    # 发一条测试通知然后退出
    [switch]$TestNotify,
    # 开机自启 安装 / 卸载
    [switch]$InstallStartup,
    [switch]$UninstallStartup,
    # 打印当前状态后退出
    [switch]$ShowStatus,
    # 停止正在运行的监控
    [switch]$StopMonitor,
    # 不启动网页面板
    [switch]$NoWeb,
    # 打开网页面板（必要时先启动监控）
    [switch]$OpenWeb,
    # 不要自动提权（默认会自动请求管理员权限，用来读内存/主板/风扇温度）
    [switch]$NoElevate
    ,
    # 运行模式 —— 外设监测和硬件监测可以完全分开跑，互不拖累：
    #   All      两边都监控（默认）
    #   Device   只监控外设接入/拔出，几乎不占 CPU、响应最快
    #   Hardware 只监控硬件传感器（温度/功耗/频率/硬盘）
    [ValidateSet('All','Device','Hardware')]
    [string]$StartMode = ''
    ,
    # 生成设备清单后退出（等同于以前双击 生成设备清单.cmd）
    [switch]$Inventory,
    # 生成清单后直接用记事本打开
    [switch]$OpenInventory
)

$ErrorActionPreference = 'Continue'
$script:Version = '1.1'

# ============================================================================
#  自动提权
#  内存温度、主板温度、风扇转速这些传感器挂在 SMBus 低层总线上，
#  读它们必须加载内核驱动，而加载内核驱动必须要管理员权限。
#  所以：不是管理员就重新以管理员身份启动自己（弹一次 UAC），然后本进程退出。
#  用户拒绝 UAC 也能继续跑，只是这几项读不到，其它功能一切正常。
# ============================================================================
# 正常使用请走「设备连接监控.exe」—— 那个 exe 是 GUI 程序，它用 runas 重启"自己"
# 来完成提权，提权后依然没有控制台窗口，再用 CreateNoWindow 启动本脚本，
# 所以全程不会出现任何窗口。
#
# 下面这段是给「直接右键运行 DeviceWatch.ps1」准备的退路。这条路径避不开窗口：
# 用 runas 启动控制台程序时，窗口是 UAC 提权机制先创建的，脚本还没开始跑就出现了，
# 用 -WindowStyle Hidden 藏不掉。所以别走这条路，用 exe。
#
# 只有这些操作不需要管理员：停止、测试通知、开网页、看状态。
# 其余（正常监控、安装/卸载自启）都要提权，否则内存/主板温度读不到。
# 当前进程是否已经具备管理员权限 —— 这个判断无论走哪条分支都要先算出来，
# 因为后面 Initialize-LhmSensors 靠它决定要不要加载低层传感器库。
$script:IsAdmin = $false
try {
    $script:IsAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

$utilityOnly = ($StopMonitor -or $TestNotify -or $OpenWeb -or $ShowStatus -or $Inventory)
if (-not $NoElevate -and -not $utilityOnly -and -not $script:IsAdmin) {
    if ($true) {
        try {
            # 注意：-WindowStyle 必须排在 -File 之前，否则 powershell 会把它当成脚本参数，
            # 脚本因为不认识这个参数直接报错退出 —— 表现就是"提权后什么都没发生"。
            if ($Console) {
                $psArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-Console')
            } else {
                $psArgs = @('-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
            }
            if ($NoTray) { $psArgs += '-NoTray' }
            # 提权重启会丢参数 —— 忘了传 -StartMode 的话，子进程永远用默认模式
            if ($StartMode) { $psArgs += @('-StartMode', $StartMode) }
            if ($Inventory) { $psArgs += '-Inventory' }
            if ($OpenInventory) { $psArgs += '-OpenInventory' }
            if ($LearnMinutes -gt 0) { $psArgs += @('-LearnMinutes', "$LearnMinutes") }
            if ($NoNotify) { $psArgs += '-NoNotify' }
            if ($NoWeb) { $psArgs += '-NoWeb' }
            Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $psArgs
            exit
        } catch {
            # 用户点了"否"，继续以普通权限运行
        }
    }
}


# ============================================================================
#  路径与全局状态
# ============================================================================
$script:Root       = Split-Path -Parent $MyInvocation.MyCommand.Path

# ============================================================================
#  运行模式
#  外设监测和设备监测原本挤在一个循环里串行跑 —— 硬件采样那几十毫秒一执行，
#  设备轮询就被推迟。分开跑之后两边各自有独立的互斥锁 / 端口 / PID / 日志，
#  可以同时运行，互不干扰，也可以只跑其中一个。
# ============================================================================
# $script:Mode 是"当前生效的模式"，运行期间会被网页切换改掉。
# 参数故意叫 $StartMode 而不是 $Mode —— 脚本参数就在脚本作用域里，
# 叫 $Mode 的话和 $script:Mode 会是同一个变量，后面赋值会互相覆盖。
$script:Mode = 'All'
$script:LogDir     = Join-Path $script:Root 'logs'
$script:ConfigPath = Join-Path $script:Root 'settings.json'
$script:AppId      = 'DeviceWatch.Monitor'
$script:Running    = $true
# 注意：此处不能引用 $script:Settings —— 配置在下面才加载。先只记录命令行开关。
$script:UseNotify  = -not $NoNotify
$script:ConsoleOut = [bool]$Console
$script:Tray       = $null
$script:TrayIcons  = @{}
$script:ToastMgr   = $null
$script:LastEvent  = $null
$script:LearnUntil = if ($LearnMinutes -gt 0) { (Get-Date).AddMinutes($LearnMinutes) } else { [datetime]::MinValue }

# 设备缓存：InstanceId -> @{Name;Class;Problem;Enumerator}
$script:Known      = @{}
# 统计：InstanceId -> @{Name;Off;On}
$script:Stats      = @{}
# 按物理设备统计：一次拔插算一次，而不是复合设备的 8 个节点算 8 次
$script:GroupStats = @{}
# 异常设备清单（真故障）与已禁用清单（正常状态，不该报警）
$script:ProblemList = @()
$script:BenignList  = @()
# 最近事件环形缓冲
$script:Recent     = New-Object System.Collections.ArrayList
# 已忽略的 InstanceId（本次运行内）
$script:Muted      = @{}
# 事件日志去重
$script:SeenEvents = @{}
$script:LastEvtId  = 0
# 网页面板
$script:WebListener    = $null
$script:WebContextTask = $null
$script:WebUrl         = ''
$script:WebNetSnap     = $null
$script:WebPingText    = ''
$script:WebPingResults = @()
$script:Seq            = 0
# 待合并的设备变化（同一设备的本体和各接口可能差几秒才枚举完）
$script:Pending        = New-Object System.Collections.ArrayList
$script:PendingSince   = [datetime]::MinValue
# 同一台设备刚刚通知过的记录，避免本体和子接口分两批时弹两次
$script:NotifyCooldown = @{}
# 硬件采集（由 _build/hw.ps1 提供 Initialize-Hardware / Update-Hardware）
$script:Hw             = $null
$script:NextHwAt       = [datetime]::MinValue
$script:WebRestartPending = $false
$script:ChangeAt = $null      # 本轮变化第一次被发现的时刻（算端到端延迟用）
$script:Phase = '启动'          # 看门狗用：当前正在跑哪一段
$script:MaxBlockWhere = ''
$script:AutoStartOn = $false     # 开机自启状态缓存（Get-ScheduledTask 一次要 500ms，绝不能放进 /api/state）
$script:AutoStartChecked = [datetime]::MinValue
$script:SensorsExt  = $null      # 低层传感器（内存温度 / CPU 功耗 / GPU 热点等）
$script:NextExtAt   = [datetime]::MinValue
$script:LastPumpAt   = $null
$script:MaxBlockMs   = 0.0

# ============================================================================
#  配置
# ============================================================================
$script:Settings = [ordered]@{
    PollMs                = 200     # 设备在线列表轮询间隔（毫秒）。实测一次只要 0.06ms，调小几乎不花 CPU，但直接减少插拔延迟
    NetCheckMs            = 8000    # 网卡状态检查间隔
    FullScanSec           = 90      # 完整设备详情扫描间隔（秒），用于发现"设备还在但出故障"
    EventLogCheckSec      = 20       # 系统日志检查间隔
    LhmSampleSec          = 8       # 低层传感器（内存温度/CPU 功耗/GPU 热点）采样间隔（秒），变化慢，调大省 CPU
HwSampleMs            = 2000    # 硬件（温度/占用/速率）采样间隔
    HwMetric              = 'temp'  # 硬件主指标：temp=温度 / load=占用 / both=都显示
    Notify                = $true   # 弹窗通知
    NotifyEventLog        = $true   # 系统日志里的硬件错误也弹窗
    Sound                 = $true   # 提示音
    WatchNetwork          = $true   # 监控网卡 Up/Down
    WatchInternet         = $true   # 监控外网连通性
    WatchEventLog         = $true   # 监控系统日志硬件错误
    ShowTray              = $true   # 显示托盘图标
    PingTargets           = @('223.5.5.5', '119.29.29.29', '1.1.1.1')
    PingTimeoutMs         = 1500
    InternetFailThreshold = 2       # 连续失败几次才算断网
    IgnorePatterns        = @()     # 忽略规则，支持通配符，例：'*WPDBUSENUM*'
    FocusPatterns         = @()     # 重点关注设备，匹配到的通知会加 ⭐
    IgnoreSoftwareDevices = $false  # 忽略 SW\ / SWD\ / ROOT\ 软件虚拟设备
    ToastDuration         = 'long'  # short / long
    WebEnabled            = $true   # 开启本地网页面板（只监听 127.0.0.1，外网访问不到）
    RunMode               = 'All'   # 运行模式：All=外设+硬件 / Device=只外设 / Hardware=只硬件（可在网页上改）
WebPort               = 8787    # 网页面板端口
    CoalesceSec           = 0.2     # 变化合并窗口（秒，0.2 = 200ms）：等这么久没有新变化就发通知。同设备的后续批次靠 NotifyCooldownSec 抑制
    DebounceMs            = 200     # 变化去抖（毫秒）：USB 插拔会连触发多次，等稳定后再比对。调大更稳但更慢
    NotifyCooldownSec     = 8       # 同一台设备这么多秒内只通知一次（设备本体和子接口常分两批出现）
    BusEventThreshold     = 3       # 同一窗口内这么多台物理设备一起变化，就合并成一条"总线事件"
    LogRetentionDays      = 60
    MaxLogFileMB          = 20
}

if (Test-Path -LiteralPath $script:ConfigPath) {
    try {
        $raw = Get-Content -LiteralPath $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($k in @($script:Settings.Keys)) {
            if ($raw.PSObject.Properties.Name -contains $k) { $script:Settings[$k] = $raw.$k }
        }
    } catch {
        Write-Host "配置文件读取失败，使用默认配置：$($_.Exception.Message)" -ForegroundColor Yellow
    }
} else {
    try {
        $json = [pscustomobject]$script:Settings | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText($script:ConfigPath, $json, (New-Object System.Text.UTF8Encoding($true)))
    } catch { }
}

# 配置文件加载完成后，才把 settings.json 的 Notify 开关合并进来
if (-not $script:Settings.Notify) { $script:UseNotify = $false }

# 运行模式：命令行 -Mode 优先，否则用 settings.json 里存的
# 优先级：命令行 -StartMode > settings.json 里的 RunMode > All
if ($StartMode -and @('All','Device','Hardware') -contains $StartMode) {
    $script:Mode = $StartMode
} elseif ($script:Settings.RunMode -and @('All','Device','Hardware') -contains [string]$script:Settings.RunMode) {
    $script:Mode = [string]$script:Settings.RunMode
}


# ============================================================================
#  日志
# ============================================================================
if (-not (Test-Path -LiteralPath $script:LogDir)) {
    New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
}
$script:LogFile = Join-Path $script:LogDir ("DeviceWatch-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$script:CsvFile = Join-Path $script:LogDir 'events.csv'
$script:StatsFile = Join-Path $script:LogDir 'summary.txt'
$script:StopFlag  = Join-Path $script:Root '.stop-request'
$script:Utf8Bom = New-Object System.Text.UTF8Encoding($true)

function Write-Log {
    param(
        [Parameter(Mandatory)][string]$Text,
        [ValidateSet('Info', 'On', 'Off', 'Warn', 'Error', 'Net')][string]$Kind = 'Info',
        [switch]$NoConsole
    )
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Text
    try { [System.IO.File]::AppendAllText($script:LogFile, $line + "`r`n", $script:Utf8Bom) } catch { }

    if (-not $NoConsole) {
        $color = switch ($Kind) {
            'On'    { 'Green' }
            'Off'   { 'Red' }
            'Warn'  { 'Yellow' }
            'Error' { 'Magenta' }
            'Net'   { 'Cyan' }
            default { 'Gray' }
        }
        if ($script:ConsoleOut) { Write-Host $line -ForegroundColor $color }
    }
}

function Write-CsvEvent {
    param([string]$Kind, [string]$Id, [string]$Name, [string]$Class, [string]$Extra)
    try {
        if (-not (Test-Path -LiteralPath $script:CsvFile)) {
            [System.IO.File]::AppendAllText($script:CsvFile,
                "时间,类型,设备名称,类别,设备实例ID,详情`r`n", $script:Utf8Bom)
        }
        $row = '{0},{1},"{2}",{3},"{4}","{5}"' -f `
            (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
            $Kind,
            ($Name -replace '"', "'"),
            $Class,
            ($Id -replace '"', "'"),
            ($Extra -replace '"', "'")
        [System.IO.File]::AppendAllText($script:CsvFile, $row + "`r`n", $script:Utf8Bom)
    } catch { }
}

# ============================================================================
#  Windows 原生接口
# ============================================================================
if (-not ('DevNative' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class DevNative {
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiOpenDeviceInfo(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType, byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint need, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint CM_Get_Device_ID_ListW(string pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);

    const uint DIGCF_PRESENT = 0x02, DIGCF_ALLCLASSES = 0x04;
    const uint SPDRP_DEVICEDESC = 0x00, SPDRP_CLASS = 0x07, SPDRP_FRIENDLYNAME = 0x0C, SPDRP_ENUMERATOR_NAME = 0x16;
    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    // DEVPKEY_Device_BusReportedDeviceDesc —— 设备自己上报的型号字符串
    static Guid BusReportedKey = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2");

    static string Str(IntPtr set, ref SP_DEVINFO_DATA d, uint prop) {
        uint t, req; byte[] buf = new byte[2048];
        if (SetupDiGetDeviceRegistryProperty(set, ref d, prop, out t, buf, (uint)buf.Length, out req) && req > 2)
            return Encoding.Unicode.GetString(buf, 0, (int)req - 2);
        return "";
    }

    // 设备自报的型号（读不到就返回空串）
    static string BusReported(IntPtr set, ref SP_DEVINFO_DATA d) {
        DEVPROPKEY k = new DEVPROPKEY(); k.fmtid = BusReportedKey; k.pid = 4;
        uint t, req; byte[] buf = new byte[4096];
        try {
            if (SetupDiGetDevicePropertyW(set, ref d, ref k, out t, buf, (uint)buf.Length, out req, 0) && req > 2 && t == 18)
                return Encoding.Unicode.GetString(buf, 0, (int)req - 2);
        } catch { }
        return "";
    }

    // 极快：只取当前在线的设备实例 ID 列表（1~5 毫秒）
    public static List<string> PresentIds() {
        var res = new List<string>();
        uint len = 0;
        if (CM_Get_Device_ID_List_SizeW(out len, null, CM_GETIDLIST_FILTER_PRESENT) != 0 || len == 0) return res;
        char[] buf = new char[len + 16];
        if (CM_Get_Device_ID_ListW(null, buf, (uint)buf.Length, CM_GETIDLIST_FILTER_PRESENT) != 0) return res;
        int start = 0;
        for (int i = 0; i < buf.Length; i++) {
            if (buf[i] == '\0') { if (i == start) break; res.Add(new string(buf, start, i - start)); start = i + 1; }
        }
        return res;
    }

    // 只查指定的几个设备，不枚举全部。
    // 给"刚插上"的设备取名字用：全量 Details() 要 600 毫秒（289 个设备 × 4 次属性读），
    // 而一次插拔通常只涉及 1~3 个设备，实测只要 1~3 毫秒。
    // 这是插入延迟从约 1.2 秒降到约 0.6 秒的关键。
    public static List<string[]> DetailsFor(string[] ids) {
        var list = new List<string[]>();
        if (ids == null || ids.Length == 0) return list;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try {
            foreach (string id in ids) {
                if (string.IsNullOrEmpty(id)) continue;
                SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                if (!SetupDiOpenDeviceInfo(set, id, IntPtr.Zero, 0, ref d)) continue;
                string desc = Str(set, ref d, SPDRP_FRIENDLYNAME);
                if (desc.Length == 0) desc = Str(set, ref d, SPDRP_DEVICEDESC);
                uint status = 0, problem = 0;
                uint cr = CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0);
                list.Add(new string[] { id, desc, Str(set, ref d, SPDRP_CLASS), Str(set, ref d, SPDRP_ENUMERATOR_NAME),
                    (cr == 0 ? problem : 0).ToString(), (cr == 0 ? status : 0).ToString(), BusReported(set, ref d) });
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }
    // 完整详情：ID / 名称 / 类别 / 枚举器 / 故障码 / 状态位（约 600 毫秒）
    public static List<string[]> Details() {
        var list = new List<string[]>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try {
            SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
            d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++) {
                StringBuilder sb = new StringBuilder(1024); uint need;
                SetupDiGetDeviceInstanceId(set, ref d, sb, (uint)sb.Capacity, out need);
                string desc = Str(set, ref d, SPDRP_FRIENDLYNAME);
                if (desc.Length == 0) desc = Str(set, ref d, SPDRP_DEVICEDESC);
                uint status = 0, problem = 0;
                uint cr = CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0);
                // [6] 是设备自报型号，命名时优先用它
                list.Add(new string[] { sb.ToString(), desc, Str(set, ref d, SPDRP_CLASS), Str(set, ref d, SPDRP_ENUMERATOR_NAME),
                    (cr == 0 ? problem : 0).ToString(), (cr == 0 ? status : 0).ToString(), BusReported(set, ref d) });
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }
}
'@
}

if (-not ('IconNative' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class IconNative {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
'@
}

# 设备故障码中文解释
$script:ProblemText = @{
    1  = '设备未配置'
    2  = '内存不足'
    3  = '驱动未安装'
    4  = '注册表信息不完整'
    5  = '系统资源不足'
    6  = 'BIOS 未分配资源'
    7  = '需要手动配置'
    8  = 'BIOS 报告资源冲突'
    9  = '驱动加载失败'
    10 = '设备无法启动'
    11 = '设备故障'
    12 = '资源不足'
    13 = '需要重新枚举'
    14 = '需要重启电脑'
    15 = '正在重新枚举'
    16 = '资源重新分配失败'
    17 = '需要更多资源'
    18 = '驱动需要重新安装'
    19 = '注册表损坏'
    20 = '驱动加载失败'
    21 = '设备正在移除'
    22 = '设备已被禁用'
    23 = '驱动加载失败'
    24 = '设备不存在'
    25 = '设备已被移除'
    26 = '驱动安装失败'
    27 = '驱动未安装'
    28 = '驱动未安装'
    29 = '固件未提供必需资源'
    30 = '不支持 IRQ 共享'
    31 = '设备无法使用'
    32 = '驱动加载失败'
    33 = '驱动加载失败'
    34 = '需要重启'
    35 = '固件版本不匹配'
    36 = '设备正在关机'
    37 = '驱动加载失败'
    38 = '驱动重复安装'
    39 = '驱动加载失败'
    40 = '信息不完整'
    41 = '加载驱动失败'
    42 = '设备正在等待重启'
    43 = '驱动报告设备出错，已被停止'
    44 = '设备被软件阻止启动'
    45 = '设备当前未连接'
    46 = '设备被系统安全策略阻止'
    47 = '设备正在等待删除'
    48 = '设备被软件阻止启动'
    49 = '设备被系统策略阻止启动'
    50 = '设备被系统策略阻止启动'
    51 = '设备被用户手动禁用'
    52 = '设备被组策略禁用'
    53 = '设备被管理员禁用'
    54 = '设备正在等待驱动安装'
}

# Windows 的兜底通用名，信息量太低，命名时让位给更具体的名字
$script:GenericDeviceName = '^(USB 输入设备|USB 复合设备|USB Composite Device|符合 HID 标准的.*|HID-compliant .*|HID Keyboard Device|HID 设备|I2C HID 设备|蓝牙 HID 设备|Microsoft Input Configuration Device|网络控制器|以太网控制器|视频控制器.*|多媒体控制器|SM 总线控制器|PCI 主桥|通用串行总线.*控制器|蓝牙外围设备|未知设备|Generic .*|Standard .*|.*控制器|卷|磁盘驱动器|基本系统设备)$'

function Get-BestDeviceName {
    # 取"不通用且最长"的那个：USB 设备自报的型号最准，
    # 但 PCI 设备自报的往往是"以太网控制器"这种兜底名，这时该用驱动给的型号名。
    param([string]$Friendly, [string]$Reported)
    $cands = @()
    foreach ($c in @($Friendly, $Reported)) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        if ($c -match $script:GenericDeviceName) { continue }
        $cands += $c
    }
    if ($cands.Count -gt 0) { return ($cands | Sort-Object Length -Descending | Select-Object -First 1) }
    if (-not [string]::IsNullOrWhiteSpace($Friendly)) { return $Friendly }
    if (-not [string]::IsNullOrWhiteSpace($Reported)) { return $Reported }
    return ''
}

# 下面这些故障码表示"被主动禁用/未连接"，是正常状态，不是故障：
#   21 正在移除   22 已被禁用   45 当前未连接   51/52/53 被策略或手动禁用
$script:BenignProblemCodes = @(21, 22, 45, 51, 52, 53)

function Test-BenignProblem([int]$code) {
    return ($script:BenignProblemCodes -contains $code)
}

function Get-ProblemText([int]$code) {
    if ($code -le 0) { return '正常' }
    if ($script:ProblemText.ContainsKey($code)) { return $script:ProblemText[$code] }
    return "未知故障(代码 $code)"
}

# ---------------------------------------------------------------------------
#  通知合并
#  一个物理 USB 设备会被 Windows 拆成多个设备节点（复合设备本体 + 各接口 +
#  HID 集合），比如一个鼠标能拆出 8 个。这里按物理设备分组，只弹一条通知。
# ---------------------------------------------------------------------------
function Get-DeviceGroupKey {
    param([string]$Id)
    if (-not $Id) { return '' }
    $m = [regex]::Match($Id, 'VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}')
    if ($m.Success) { return 'USB\' + $m.Value.ToUpper() }
    $i = $Id.LastIndexOf('\')
    if ($i -gt 0) { return $Id.Substring(0, $i) }
    return $Id
}

function Get-DeviceNameScore {
    # 决定这一组里拿哪个名字做标题：优先「鼠标」「键盘」，而不是「USB 输入设备」
    param([string]$Name, [string]$Class)
    $s = 0
    switch -Regex ([string]$Class) {
        'Mouse'                             { $s += 40 }
        'Keyboard'                          { $s += 30 }
        'DiskDrive|USBSTOR|SCSIAdapter|HDC' { $s += 25 }
        'Camera|Image'                      { $s += 25 }
        'AudioEndpoint|MEDIA|Volume'        { $s += 20 }
        '^Net$'                             { $s += 20 }
        'Monitor'                           { $s += 15 }
    }
    if ($Name -match '鼠标|Mouse') { $s += 20 }
    if ($Name -match '键盘|Keyboard') { $s += 15 }
    if ($Name -match '摄像头|Camera|耳机|Headset|手柄|Gamepad|打印机|Printer') { $s += 15 }
    if ([string]::IsNullOrWhiteSpace($Name)) { $s -= 100 }
    elseif ($Name -match $script:GenericDeviceName) { $s -= 40 }   # 系统兜底名，让位给真实型号
    else { $s += 25 }                                              # 像真实型号的名字，优先显示
    return $s
}

function Test-NotifyCooled {
    param([string]$Key)
    if (-not $script:NotifyCooldown.ContainsKey($Key)) { return $false }
    return (((Get-Date) - $script:NotifyCooldown[$Key]).TotalSeconds -lt [double]$script:Settings.NotifyCooldownSec)
}

function Set-NotifyCooldown {
    param([string]$Key)
    $script:NotifyCooldown[$Key] = Get-Date
}

function Get-GroupBestEvent {
    param([object[]]$Events)
    return ($Events | Sort-Object -Property @{ Expression = { Get-DeviceNameScore -Name $_.Name -Class $_.Class } } -Descending | Select-Object -First 1)
}

function Test-GroupIsFocus {
    param($Best)
    foreach ($fp in @($script:Settings.FocusPatterns)) {
        if ($fp -and ("$($Best.Id) $($Best.Name) $($Best.Class)" -like $fp)) { return $true }
    }
    return $false
}

# 发一条"单个物理设备"的通知
function Send-OneGroupAlert {
    param([object[]]$Events, [ValidateSet('Off', 'On')][string]$Kind)
    if (-not $Events -or $Events.Count -eq 0) { return }

    $best = Get-GroupBestEvent -Events $Events
    $gk = Get-DeviceGroupKey -Id $best.Id
    $n = $Events.Count

    if ($Kind -eq 'Off') {
        $title = "⚠ 设备断联：$($best.Name)"
    } else {
        $allKnown = $true
        foreach ($ev in $Events) { if (-not $ev.WasKnown) { $allKnown = $false; break } }
        $title = if ($allKnown) { "✅ 已重新连接：$($best.Name)" } else { "🔌 新设备接入：$($best.Name)" }
    }
    if (Test-GroupIsFocus -Best $best) { $title = '⭐ ' + $title }

    $body = New-Object System.Collections.ArrayList
    [void]$body.Add(("类别：{0}" -f $best.Class))
    if ($n -gt 1) { [void]$body.Add(("同一物理设备的 {0} 个子设备一起变化" -f $n)) }
    [void]$body.Add((Get-ShortId $gk))
    [void]$body.Add((Get-Date -Format 'HH:mm:ss'))

    if (-not $script:GroupStats.ContainsKey($gk)) { $script:GroupStats[$gk] = @{ Name = $best.Name; Off = 0; On = 0 } }
    $script:GroupStats[$gk].Name = $best.Name
    if ($Kind -eq 'Off') { $script:GroupStats[$gk].Off++ } else { $script:GroupStats[$gk].On++ }

    Add-Recent -Kind $(if ($Kind -eq 'Off') { '断联' } else { '接入' }) `
        -Label $best.Name -Id $gk -Detail $(if ($n -gt 1) { "$n 个子设备" } else { $best.Class })
    $script:LastEvent = Get-Date
    $script:EventCount++

    Set-NotifyCooldown -Key ("$Kind|$gk")
    # 记下端到端延迟：从"轮询发现变化"到"弹窗"，用来判断响应够不够快
    if ($script:ChangeAt) {
        try {
            $lm = [int]((Get-Date) - $script:ChangeAt).TotalMilliseconds
            Write-Log ("  [延迟] 从发现变化到弹窗 {0} ms（轮询 {1} + 去抖 {2} + 查名字 + 合并 {3}）" -f `
                $lm, $script:Settings.PollMs, $script:Settings.DebounceMs, ([int]([double]$script:Settings.CoalesceSec * 1000)))
        } catch { }
    }
    Show-Alert -Title $title -Body ($body -join "`n") -Kind $Kind
}

# 把一小段时间内攒下的变化合并后统一通知
# 这样同一个物理设备的"本体 + 各接口"即使相隔两三秒枚举出来，也只会提醒一次
function Invoke-PendingNotify {
    if ($script:Pending.Count -eq 0) { return }
    $latMs = -1
    if ($script:ChangeAt) { try { $latMs = [int]((Get-Date) - $script:ChangeAt).TotalMilliseconds } catch { } }
    $batch = @($script:Pending)
    $script:Pending.Clear()

    $groups = @{}
    foreach ($ev in $batch) {
        $gk = Get-DeviceGroupKey -Id $ev.Id
        $key = "$($ev.Kind)|$gk"
        if (-not $groups.ContainsKey($key)) { $groups[$key] = New-Object System.Collections.ArrayList }
        [void]$groups[$key].Add($ev)
    }

    $offKeys = @($groups.Keys | Where-Object { $_.StartsWith('Off|') })
    $onKeys = @($groups.Keys | Where-Object { $_.StartsWith('On|') })
    $threshold = [int]$script:Settings.BusEventThreshold

    # 刚通知过的同一台设备（同方向）直接跳过：设备本体和它的子接口经常差几秒才枚举完，
    # 第一批已经弹过窗了，第二批不该再弹
    $freshOff = @(); foreach ($k in $offKeys) { if (-not (Test-NotifyCooled -Key $k)) { $freshOff += $k } }
    $freshOn = @(); foreach ($k in $onKeys) { if (-not (Test-NotifyCooled -Key $k)) { $freshOn += $k } }

    $plans = @(
        [pscustomobject]@{ Keys = $freshOff; Kind = 'Off' },
        [pscustomobject]@{ Keys = $freshOn; Kind = 'On' }
    )

    foreach ($plan in $plans) {
        $keys = @($plan.Keys)
        $kind = [string]$plan.Kind
        if ($keys.Count -eq 0) { continue }

        if ($keys.Count -ge $threshold) {
            # 同一时刻好几个物理设备一起变化 —— 基本都是集线器复位或者供电问题。
            # 合成一条"总线事件"，比刷一堆弹窗有用得多。
            $pairs = @()
            foreach ($k in $keys) {
                $bp = Get-GroupBestEvent -Events @($groups[$k])
                $pairs += [pscustomobject]@{ Gk = $k.Substring($k.IndexOf('|') + 1); Best = $bp }
            }
            $pairs = @($pairs | Sort-Object -Property @{ Expression = { Get-DeviceNameScore -Name $_.Best.Name -Class $_.Best.Class } } -Descending)
            $items = @($pairs | ForEach-Object { $_.Best })

            # 总线事件里每台设备也确实掉过一次，同样计入断联排行
            foreach ($pr in $pairs) {
                if (-not $script:GroupStats.ContainsKey($pr.Gk)) { $script:GroupStats[$pr.Gk] = @{ Name = $pr.Best.Name; Off = 0; On = 0 } }
                $script:GroupStats[$pr.Gk].Name = $pr.Best.Name
                if ($kind -eq 'Off') { $script:GroupStats[$pr.Gk].Off++ } else { $script:GroupStats[$pr.Gk].On++ }
            }

            $isHub = [bool](@($items | Where-Object { $_.Name -match '集线器|Hub|HUB' }).Count -gt 0)
            $busName = if ($isHub) { 'USB 集线器复位' } else { 'USB 总线事件' }

            $title = if ($kind -eq 'Off') { "⚠ $busName：$($keys.Count) 个设备同时断开" }
            else { "✅ 总线恢复：$($keys.Count) 个设备同时接回" }

            $body = New-Object System.Collections.ArrayList
            $show = [Math]::Min(4, $items.Count)
            for ($i = 0; $i -lt $show; $i++) { [void]$body.Add(("· " + $items[$i].Name)) }
            if ($items.Count -gt $show) { [void]$body.Add(("· 等共 {0} 个设备" -f $items.Count)) }
            [void]$body.Add('')
            [void]$body.Add('多个设备共用的链路（集线器或供电）出了问题，不是单个设备的毛病')
            [void]$body.Add((Get-Date -Format 'HH:mm:ss'))

            if ($kind -eq 'Off') {
                Write-Log ("[总线事件] {0} 个设备同时断开：{1}" -f $keys.Count, (($items | ForEach-Object { $_.Name }) -join '、')) -Kind 'Off'
            } else {
                Write-Log ("[总线恢复] {0} 个设备同时接回" -f $keys.Count) -Kind 'On'
            }

            Add-Recent -Kind $(if ($kind -eq 'Off') { '总线事件' } else { '总线恢复' }) `
                -Label ("$busName（$($keys.Count) 个设备）") -Id 'BUS' `
                -Detail (($items | Select-Object -First 3 | ForEach-Object { $_.Name }) -join '、')
            $script:LastEvent = Get-Date
            $script:EventCount++

            foreach ($k in $keys) { Set-NotifyCooldown -Key $k }
            Show-Alert -Title $title -Body ($body -join "`n") -Kind $kind
        } else {
            foreach ($k in $keys) { Send-OneGroupAlert -Events @($groups[$k]) -Kind $kind }
        }
    }
}

function Add-PendingChange {
    param([object[]]$Events, [ValidateSet('Off', 'On')][string]$Kind)
    $cnt = 0
    foreach ($ev in $Events) {
        [void]$script:Pending.Add([pscustomobject]@{
                Id = $ev.Id; Name = $ev.Name; Class = $ev.Class; WasKnown = $ev.WasKnown; Kind = $Kind
            })
        $cnt++
    }
    if ($cnt -gt 0) { $script:PendingSince = Get-Date }
}

# ============================================================================
#  通知（Windows 弹窗 + 提示音）
# ============================================================================
function Initialize-Notifier {
    try {
        $key = "HKCU:\SOFTWARE\Classes\AppUserModelId\$($script:AppId)"
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name DisplayName -Value '设备连接监控' -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $key -Name IconUri -Value "$env:SystemRoot\System32\imageres.dll,-1015" -ErrorAction SilentlyContinue
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $script:ToastMgr = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($script:AppId)
    } catch {
        $script:ToastMgr = $null
    }
}

function Show-Alert {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$Body,
        [ValidateSet('Off', 'On', 'Warn', 'Info')][string]$Kind = 'Info',
        [switch]$Force
    )
    if (-not $Force) {
        if (-not $script:UseNotify) { return }
        if (-not $script:Settings.Notify) { return }
        if ((Get-Date) -lt $script:LearnUntil) { return }
    }

    $shown = $false
    if ($script:ToastMgr) {
        try {
            $t = [System.Security.SecurityElement]::Escape($Title)
            $b = [System.Security.SecurityElement]::Escape($Body)
            $dur = [string]$script:Settings.ToastDuration
            $xmlStr = "<toast duration='$dur'><visual><binding template='ToastGeneric'><text>$t</text><text>$b</text></binding></visual><audio silent='true'/></toast>"
            $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
            $xml.LoadXml($xmlStr)
            $toast = New-Object Windows.UI.Notifications.ToastNotification $xml
            $script:ToastMgr.Show($toast)
            $shown = $true
        } catch { $shown = $false }
    }

    if (-not $shown -and $script:Tray) {
        try {
            $ico = switch ($Kind) { 'Off' { 'Error' } 'Warn' { 'Warning' } default { 'Info' } }
            $script:Tray.ShowBalloonTip(8000, $Title, $Body, $ico)
            $shown = $true
        } catch { }
    }

    if ($script:Settings.Sound) {
        try {
            switch ($Kind) {
                'Off'  { [System.Media.SystemSounds]::Hand.Play() }
                'Warn' { [System.Media.SystemSounds]::Exclamation.Play() }
                'On'   { [System.Media.SystemSounds]::Asterisk.Play() }
                default { }
            }
        } catch { }
    }
}

# ============================================================================
#  托盘图标
# ============================================================================
function New-DotIcon {
    param([System.Drawing.Color]$Color)
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $brush = New-Object System.Drawing.SolidBrush $Color
    $g.FillEllipse($brush, 3, 3, 26, 26)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 255, 255, 255)), 4
    $g.DrawEllipse($pen, 3, 3, 26, 26)
    $g.Dispose(); $brush.Dispose(); $pen.Dispose()
    $h = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($h)
    $clone = $icon.Clone()
    [IconNative]::DestroyIcon($h) | Out-Null
    $bmp.Dispose()
    return $clone
}

function Initialize-Tray {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        [System.Windows.Forms.Application]::EnableVisualStyles()

        $script:TrayIcons['Ok']      = New-DotIcon ([System.Drawing.Color]::FromArgb(46, 204, 113))
        $script:TrayIcons['Event']   = New-DotIcon ([System.Drawing.Color]::FromArgb(243, 156, 18))
        $script:TrayIcons['Problem'] = New-DotIcon ([System.Drawing.Color]::FromArgb(231, 76, 60))

        $script:Tray = New-Object System.Windows.Forms.NotifyIcon
        $script:Tray.Icon = $script:TrayIcons['Ok']
        $script:Tray.Text = $script:ModeName + ' 启动中...'
        $script:Tray.Visible = $true

        $menu = New-Object System.Windows.Forms.ContextMenuStrip
        $script:MenuStatus = New-Object System.Windows.Forms.ToolStripMenuItem
        $script:MenuStatus.Enabled = $false
        [void]$menu.Items.Add($script:MenuStatus)
        [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

        $miLog = New-Object System.Windows.Forms.ToolStripMenuItem '打开今天的日志 (&L)'
        $miLog.add_Click({ try { Start-Process notepad.exe -ArgumentList "`"$script:LogFile`"" } catch { } })
        [void]$menu.Items.Add($miLog)

        $miDir = New-Object System.Windows.Forms.ToolStripMenuItem '打开日志文件夹 (&F)'
        $miDir.add_Click({ try { Start-Process explorer.exe -ArgumentList "`"$script:LogDir`"" } catch { } })
        [void]$menu.Items.Add($miDir)

        $miWeb = New-Object System.Windows.Forms.ToolStripMenuItem '打开监测网页 (&W)'
        $miWeb.add_Click({
            if ($script:WebUrl) { try { Start-Process $script:WebUrl } catch { } }
            else { Show-Alert -Title '设备监控' -Body '网页面板没有启动（端口可能被占用），可查看 settings.json 里的 WebPort。' -Force }
        })
        [void]$menu.Items.Add($miWeb)

        $miStat = New-Object System.Windows.Forms.ToolStripMenuItem '打开断联统计文件 (&S)'
        $miStat.add_Click({ Show-Stats -Open })
        [void]$menu.Items.Add($miStat)

        [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

        $script:MenuPause = New-Object System.Windows.Forms.ToolStripMenuItem '暂停弹窗通知'
        $script:MenuPause.add_Click({
            $script:UseNotify = -not $script:UseNotify
            $script:MenuPause.Checked = -not $script:UseNotify
            $script:MenuPause.Text = if ($script:UseNotify) { '暂停弹窗通知' } else { '恢复弹窗通知' }
            Write-Log ("通知已{0}" -f $(if ($script:UseNotify) { '恢复' } else { '暂停' }))
            Update-Tray
        })
        [void]$menu.Items.Add($script:MenuPause)

        $miLearn = New-Object System.Windows.Forms.ToolStripMenuItem '学习模式 10 分钟（只记录不弹窗）'
        $miLearn.add_Click({
            $script:LearnUntil = (Get-Date).AddMinutes(10)
            Write-Log '进入学习模式 10 分钟'
            Show-Alert -Title '设备监控' -Body '已进入学习模式：10 分钟内只记录、不弹窗。' -Force
            Update-Tray
        })
        [void]$menu.Items.Add($miLearn)

        $miMute = New-Object System.Windows.Forms.ToolStripMenuItem '忽略最近断联的设备'
        $miMute.add_Click({
            if ($script:Recent.Count -eq 0) { return }
            $last = $script:Recent[$script:Recent.Count - 1]
            if ($last.Id) {
                $script:Muted[$last.Id] = $true
                Write-Log ("已忽略设备：{0}  ({1})" -f $last.Label, $last.Id)
                Show-Alert -Title '设备监控' -Body ("已忽略：{0}（本次运行有效，彻底忽略请写入 settings.json 的 IgnorePatterns）" -f $last.Label) -Force
            }
        })
        [void]$menu.Items.Add($miMute)

        $miScan = New-Object System.Windows.Forms.ToolStripMenuItem '立即完整扫描 (&R)'
        $miScan.add_Click({ $script:ForceFullScan = $true })
        [void]$menu.Items.Add($miScan)

        [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))


        $miTest = New-Object System.Windows.Forms.ToolStripMenuItem '发送测试通知'
        $miTest.add_Click({
            Show-Alert -Title '设备连接监控 - 测试' -Body ("通知通道正常。当前在线设备 {0} 个。" -f $script:Known.Count) -Kind 'Warn' -Force
        })
        [void]$menu.Items.Add($miTest)

        $miExit = New-Object System.Windows.Forms.ToolStripMenuItem '退出监控 (&X)'
        $miExit.add_Click({ $script:Running = $false })
        [void]$menu.Items.Add($miExit)

        $script:Tray.ContextMenuStrip = $menu
        $script:Tray.add_MouseDoubleClick({ try { Start-Process notepad.exe -ArgumentList "`"$script:LogFile`"" } catch { } })
    } catch {
        $script:Tray = $null
        Write-Log ("托盘初始化失败（不影响监控）：{0}" -f $_.Exception.Message) -Kind 'Warn'
    }
}

function Update-Tray {
    if (-not $script:Tray) { return }
    try {
        $state = '正常'
        $icon = 'Ok'
        if ($script:NetDown) { $state = '外网断开'; $icon = 'Problem' }
        elseif ($script:ProblemCount -gt 0) {
            $state = if ($script:ProblemCount -eq 1 -and @($script:ProblemList).Count -eq 1) { "故障: $($script:ProblemList[0].name)" } else { "有 $($script:ProblemCount) 个设备故障" }
            $icon = 'Problem'
        }
        elseif ($script:LastEvent -and ((Get-Date) - $script:LastEvent).TotalSeconds -lt 20) { $state = '刚刚有变动'; $icon = 'Event' }

        $tip = "设备监控：{0} | 在线 {1} 个 | {2}" -f $state, $script:Known.Count, $state
        if ($script:LearnUntil -gt (Get-Date)) { $tip = "设备监控：学习模式中 | 在线 $($script:Known.Count) 个" }
        if (-not $script:UseNotify) { $tip = "设备监控：通知已暂停 | 在线 $($script:Known.Count) 个" }
        if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 63) }

        $script:Tray.Text = $tip
        $script:Tray.Icon = $script:TrayIcons[$icon]

        if ($script:MenuStatus) {
            $script:MenuStatus.Text = ("在线设备 {0} 个 | 今日事件 {1} 条 | 状态：{2}" -f $script:Known.Count, $script:EventCount, $state)
        }
    } catch { }
}

# ============================================================================
#  设备扫描
# ============================================================================
function Test-Ignored {
    param([string]$Id, [string]$Name, [string]$Class)
    if ($script:Muted.ContainsKey($Id)) { return $true }
    if ($script:Muted.ContainsKey((Get-DeviceGroupKey -Id $Id))) { return $true }
    $hay = "$Id $Name $Class"
    if ($script:Settings.IgnoreSoftwareDevices) {
        if ($Id -like 'SW\*' -or $Id -like 'SWD\*' -or $Id -like 'ROOT\*') { return $true }
    }
    foreach ($p in @($script:Settings.IgnorePatterns)) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        if ($hay -like $p) { return $true }
    }
    return $false
}

function Get-ShortId {
    param([string]$Id)
    if ($Id.Length -le 46) { return $Id }
    return $Id.Substring(0, 43) + '...'
}

function Register-KnownDevice {
    param([string]$Id, [string]$Name, [string]$Class, [int]$Problem, [string]$Enumerator)
    $script:Known[$Id] = @{
        Name       = $Name
        Class      = $Class
        Problem    = $Problem
        Enumerator = $Enumerator
        Since      = if ($script:Known.ContainsKey($Id)) { $script:Known[$Id].Since } else { Get-Date }
    }
}

function Add-Stat {
    param([string]$Id, [string]$Name, [string]$Field)
    if (-not $script:Stats.ContainsKey($Id)) {
        $script:Stats[$Id] = @{ Name = $Name; Off = 0; On = 0 }
    }
    if ($Name) { $script:Stats[$Id].Name = $Name }
    $script:Stats[$Id][$Field] = $script:Stats[$Id][$Field] + 1
}

function Add-Recent {
    param([string]$Kind, [string]$Label, [string]$Id, [string]$Detail)
    $script:Seq++
    [void]$script:Recent.Add([pscustomobject]@{
            Seq    = $script:Seq
            Time   = Get-Date
            Kind   = $Kind
            Label  = $Label
            Id     = $Id
            Detail = $Detail
        })
    while ($script:Recent.Count -gt 100) { $script:Recent.RemoveAt(0) }
}

function Resolve-DeviceInfo {
    param([string[]]$Ids)
    # 只为发生变化的设备取名称，避免整体扫描
    $map = @{}
    if (-not $Ids -or $Ids.Count -eq 0) { return $map }
    $want = @{}
    foreach ($i in $Ids) { $want[$i] = $true }
    try {
        foreach ($row in [DevNative]::DetailsFor([string[]]$Ids)) {
            if ($want.ContainsKey($row[0])) {
                $map[$row[0]] = @{ Name = (Get-BestDeviceName -Friendly $row[1] -Reported $row[6]); Class = $row[2]; Problem = [int]$row[4]; Enumerator = $row[3] }
            }
        }
    } catch { }
    return $map
}

function Get-NetworkSnapshot {
    $res = @{}
    try {
        foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($ni.NetworkInterfaceType -eq [System.Net.NetworkInformation.NetworkInterfaceType]::Loopback) { continue }
            $speed = 0
            try { $speed = [int64]$ni.Speed } catch { }
            $res[$ni.Id] = @{
                Name  = $ni.Name
                Desc  = $ni.Description
                State = $ni.OperationalStatus.ToString()
                Speed = $speed
                Type  = $ni.NetworkInterfaceType.ToString()
            }
        }
    } catch { }
    return $res
}

function Format-Speed([int64]$bps) {
    if ($bps -le 0) { return '-' }
    if ($bps -ge 1e9) { return ('{0:0.#} Gbps' -f ($bps / 1e9)) }
    if ($bps -ge 1e6) { return ('{0:0.#} Mbps' -f ($bps / 1e6)) }
    return ('{0:0.#} Kbps' -f ($bps / 1e3))
}

function Get-DefaultGateway {
    $fallback = $null
    try {
        foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($ni.OperationalStatus -ne [System.Net.NetworkInformation.OperationalStatus]::Up) { continue }
            if ($ni.NetworkInterfaceType -eq [System.Net.NetworkInformation.NetworkInterfaceType]::Loopback) { continue }
            foreach ($g in $ni.GetIPProperties().GatewayAddresses) {
                if (-not $g.Address) { continue }
                if ($g.Address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { continue }
                if ($ni.NetworkInterfaceType -eq [System.Net.NetworkInformation.NetworkInterfaceType]::Tunnel) {
                    if (-not $fallback) { $fallback = $g.Address.ToString() }
                } else {
                    return $g.Address.ToString()
                }
            }
        }
    } catch { }
    return $fallback
}

# ============================================================================
#  系统日志（硬件相关错误）
# ============================================================================
$script:HwProviders = @(
    @{ P = 'Kernel-PnP';                                  N = '设备管理器' }
    @{ P = 'DriverFrameworks-UserMode';                   N = 'USB 驱动框架' }
    @{ P = '^disk$|^Disk$|volmgr|partmgr';                N = '磁盘' }
    @{ P = 'Ntfs';                                        N = '文件系统' }
    @{ P = 'storahci|stornvme|storport|msahci|iaStor';    N = '存储控制器' }
    @{ P = 'USB|usbhub|usbxhci|usbccgp|UcmUcsi';          N = 'USB' }
    @{ P = 'BTHUSB|BTHPORT|Bluetooth';                    N = '蓝牙' }
    @{ P = 'WHEA-Logger';                                 N = '硬件错误' }
    @{ P = 'ndis|Netwtw|e1[ide]|rt6|rtw|mtkwl|netwtw';    N = '网卡' }
    @{ P = 'Kernel-Power|Kernel-Boot';                    N = '电源' }
    @{ P = 'Display|nvlddmkm|amdkmdag|igfx';              N = '显卡' }
    @{ P = 'HDAudBus|IntcAudioBus|ksthunk';               N = '音频' }
)

function Get-HwEventName([string]$provider) {
    foreach ($h in $script:HwProviders) {
        if ($provider -match $h.P) { return $h.N }
    }
    return $null
}

function Invoke-EventLogCheck {
    try {
        if ($script:SeenEvents.Count -gt 5000) { $script:SeenEvents = @{} }
        $start = $script:LastEvtTime
        if (-not $start) { $script:LastEvtTime = (Get-Date).AddSeconds(-5); return }
        $evts = Get-WinEvent -FilterHashtable @{
            LogName   = 'System'
            StartTime = $start
            Level     = @(1, 2, 3)
        } -ErrorAction SilentlyContinue
        foreach ($e in @($evts)) {
            if (-not $e) { continue }
            if ($script:SeenEvents.ContainsKey($e.RecordId)) { continue }
            $script:SeenEvents[$e.RecordId] = $true
            if ($e.RecordId -gt $script:LastEvtId) { $script:LastEvtId = $e.RecordId }

            $cat = Get-HwEventName $e.ProviderName
            if (-not $cat) { continue }

            $msg = ''
            try { $msg = ($e.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1) } catch { }
            if ($msg.Length -gt 180) { $msg = $msg.Substring(0, 180) + '...' }

            $lvlName = switch ($e.Level) { 1 { '严重' } 2 { '错误' } 3 { '警告' } default { '信息' } }
            $text = "[系统日志/{0}] {1} ({2}) ID={3}  {4}" -f $cat, $lvlName, $e.ProviderName, $e.Id, $msg
            Write-Log $text -Kind $(if ($e.Level -le 2) { 'Error' } else { 'Warn' })
            Write-CsvEvent -Kind '系统日志' -Id ("EventLog:{0}" -f $e.Id) -Name $cat -Class $e.ProviderName -Extra $msg
            $script:EventCount++

            $ignore = $false
            foreach ($p in @($script:Settings.IgnorePatterns)) {
                if ($p -and ("$($e.ProviderName) $msg" -like $p)) { $ignore = $true; break }
            }
            if ($ignore) { continue }

            $key = "{0}|{1}|{2}" -f $e.ProviderName, $e.Id, $msg
            if ($script:SeenEvents.ContainsKey($key)) { continue }
            $script:SeenEvents[$key] = $true

            if ($script:Settings.NotifyEventLog) {
                Show-Alert -Title ("[硬件告警] {0}" -f $cat) `
                    -Body ("{0}`n{1}`nID={2}  {3}" -f $lvlName, $e.ProviderName, $e.Id, $msg) -Kind 'Warn'
            }
            Add-Recent -Kind '系统' -Label ("{0}/{1}" -f $cat, $e.ProviderName) -Id '' -Detail $msg
        }
    } catch { }
}

# ============================================================================
#  外网探测（异步，不阻塞界面）
# ============================================================================
$script:PingTasks = $null
$script:PingStart = [datetime]::MinValue
$script:NextPingAt = [datetime]::MinValue
$script:NetDown = $false
$script:NetFail = 0
$script:ProblemCount = 0
$script:EventCount = 0
$script:ForceFullScan = $false

function Start-PingProbe {
    $list = New-Object System.Collections.ArrayList
    $targets = @($script:Settings.PingTargets)
    $gw = Get-DefaultGateway
    foreach ($t in $targets) {
        try {
            $p = New-Object System.Net.NetworkInformation.Ping
            $task = $p.SendPingAsync($t, [int]$script:Settings.PingTimeoutMs)
            [void]$list.Add([pscustomobject]@{ Target = $t; Task = $task; Ping = $p; IsGw = $false })
        } catch { }
    }
    if ($gw) {
        try {
            $p = New-Object System.Net.NetworkInformation.Ping
            $task = $p.SendPingAsync($gw, [int]$script:Settings.PingTimeoutMs)
            [void]$list.Add([pscustomobject]@{ Target = $gw; Task = $task; Ping = $p; IsGw = $true })
        } catch { }
    }
    $script:PingTasks = $list
    $script:PingStart = Get-Date
}

function Complete-PingProbe {
    $anyOk = $false
    $gwOk = $null
    $lat = @()
    $rows = New-Object System.Collections.ArrayList
    foreach ($e in @($script:PingTasks)) {
        try {
            $ok = $false
            $ms = -1
            if ($e.Task.IsCompleted -and -not $e.Task.IsFaulted) {
                $r = $e.Task.Result
                if ($r -and $r.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
                    $ok = $true
                    $ms = [int]$r.RoundtripTime
                    $lat += ("{0} {1}ms" -f $e.Target, $ms)
                }
            }
            [void]$rows.Add([pscustomobject]@{ t = [string]$e.Target; ms = $ms; ok = $ok; gw = [bool]$e.IsGw })
            if ($e.IsGw) { $gwOk = $ok } else { if ($ok) { $anyOk = $true } }
        } catch { }
    }
    $script:WebPingResults = @($rows)

    if ($anyOk) {
        if ($script:NetDown) {
            Write-Log ("外网已恢复  ({0})" -f ($lat -join ', ')) -Kind 'On'
            Write-CsvEvent -Kind '网络恢复' -Id '' -Name '外网' -Class 'Internet' -Extra ($lat -join ', ')
            Show-Alert -Title '网络已恢复' -Body ("外网连接恢复正常。`n{0}" -f ($lat -join '   ')) -Kind 'On'
            Add-Recent -Kind '恢复' -Label '外网' -Id 'Internet' -Detail ($lat -join ', ')
            $script:EventCount++
        }
        $script:WebPingText = ($lat -join '  ')
        $script:NetDown = $false
        $script:NetFail = 0
    } else {
        $script:NetFail++
        $script:WebPingText = ('连续 {0} 次探测失败' -f $script:NetFail)
        if (-not $script:NetDown -and $script:NetFail -ge [int]$script:Settings.InternetFailThreshold) {
            $script:NetDown = $true
            $detail = if ($gwOk -eq $true) { '网关可达，问题出在运营商/出口线路' }
            elseif ($gwOk -eq $false) { '网关都 ping 不通，本机到路由器这一段断了' }
            else { '未获取到默认网关' }
            Write-Log ("外网断开：{0}" -f $detail) -Kind 'Off'
            Write-CsvEvent -Kind '断网' -Id '' -Name '外网' -Class 'Internet' -Extra $detail
            Show-Alert -Title '⚠ 外网已断开' -Body ("无法连接任何公网地址（连续 {0} 次失败）。`n{1}" -f $script:NetFail, $detail) -Kind 'Off'
            Add-Recent -Kind '断网' -Label '外网' -Id 'Internet' -Detail $detail
            $script:EventCount++
        }
    }
    $script:PingTasks = $null
}

# ============================================================================
#  统计窗口
# ============================================================================
function Show-Stats {
    param(
        # 生成后用记事本打开统计文件（不弹模态对话框 —— 那个每次都要点"确定"，很烦）
        [switch]$Open
    )
    $rows = @()
    foreach ($k in $script:GroupStats.Keys) {
        $s = $script:GroupStats[$k]
        if ($s.Off -eq 0 -and $s.On -eq 0) { continue }
        $rows += [pscustomobject]@{ 设备 = $s.Name; 断联次数 = $s.Off; 接入次数 = $s.On; 实例ID = $k }
    }
    $rows = $rows | Sort-Object 断联次数 -Descending | Select-Object -First 25

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("设备断联统计（本次运行）")
    [void]$sb.AppendLine("启动时间：$($script:StartTime.ToString('yyyy-MM-dd HH:mm:ss'))    现在：$(Get-Date -Format 'HH:mm:ss')")
    [void]$sb.AppendLine("在线设备：$($script:Known.Count)    记录事件：$($script:EventCount) 条")
    [void]$sb.AppendLine(('-' * 78))
    if ($rows.Count -eq 0) {
        [void]$sb.AppendLine('本次运行期间还没有任何设备断开过。')
    } else {
        [void]$sb.AppendLine(("{0,-38} {1,8} {2,8}" -f '设备名称', '断联', '接入'))
        foreach ($r in $rows) {
            $n = $r.设备
            if ($n.Length -gt 36) { $n = $n.Substring(0, 35) + '…' }
            [void]$sb.AppendLine(("{0,-38} {1,8} {2,8}" -f $n, $r.断联次数, $r.接入次数))
        }
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('断联次数最多的设备就是最可疑的。')
    }
    $text = $sb.ToString()
    try { Set-Content -LiteralPath $script:StatsFile -Value $text -Encoding UTF8 } catch { }

    # 结果写进 logs\summary.txt，不再弹模态对话框。
    # 想看就在网页上看「断联排行」卡片，或者用托盘菜单打开这个文件。
    if ($Open) {
        try { Start-Process notepad.exe -ArgumentList "`"$($script:StatsFile)`"" } catch { }
    }
    if (-not $script:Tray) { Write-Host $text }
}

# ============================================================================
#  开机自启 安装 / 卸载
# ============================================================================
function Get-LauncherPath {
    # 单文件启动器。开机自启走它，参数 silent = 启动但不弹浏览器。
    $exe = Join-Path $script:Root '设备连接监控.exe'
    if (Test-Path -LiteralPath $exe) { return $exe }
    return $null
}

function Install-Startup {
    # 用计划任务而不是启动文件夹快捷方式：计划任务可以设置"以最高权限运行"，
    # 这样开机时不会弹 UAC（读内存温度需要管理员），而快捷方式每次都会弹。
    $lnch = Get-LauncherPath
    if (-not $lnch) {
        Write-Host '找不到 设备连接监控.exe，无法安装开机自启。' -ForegroundColor Red
        return $false
    }
    # exe 用 silent 参数：开机只启动监控，不弹浏览器
    $isExe  = $lnch -like '*.exe'
    $launchExe  = if ($isExe) { $lnch } else { "$env:SystemRoot\System32\wscript.exe" }
    $launchArg  = if ($isExe) { 'silent' } else { "`"$lnch`"" }
    $amAdmin = $false
    try { $amAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { }
    try {
        $action = New-ScheduledTaskAction -Execute $launchExe -Argument $launchArg -WorkingDirectory $script:Root
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
        $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
        Register-ScheduledTask -TaskName 'DeviceWatch' -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings -Description '设备连接监控 - 开机自动运行（最高权限，用于读取内存/主板温度）' -Force | Out-Null
        Write-Host '已安装开机自启（计划任务 DeviceWatch，以最高权限运行，开机不弹 UAC）。' -ForegroundColor Green
    } catch {
        Write-Host ('计划任务创建失败：{0}' -f $_.Exception.Message) -ForegroundColor Red
        if (-not $amAdmin) { Write-Host '  提示：创建"最高权限"计划任务本身就需要管理员，请右键本脚本以管理员身份运行。' -ForegroundColor Yellow }
        Write-Host '  退回用启动文件夹快捷方式（开机时会弹一次 UAC）。' -ForegroundColor Yellow
        try {
            $startupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
            if (-not (Test-Path -LiteralPath $startupDir)) { New-Item -ItemType Directory -Path $startupDir -Force | Out-Null }
            $lnk = Join-Path $startupDir '设备连接监控.lnk'
            $ws = New-Object -ComObject WScript.Shell
            $sc = $ws.CreateShortcut($lnk)
            $sc.TargetPath = $launchExe
            $sc.Arguments = $launchArg
            $sc.WorkingDirectory = $script:Root
            $sc.WindowStyle = 7
            $sc.Save()
            Write-Host "  已改用启动文件夹：$lnk" -ForegroundColor Green
            return $true
        } catch { return $false }
    }
    return $true
}

function Uninstall-Startup {
    $ok = $false
    try {
        if (Get-ScheduledTask -TaskName 'DeviceWatch' -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName 'DeviceWatch' -Confirm:$false -ErrorAction Stop
            Write-Host '已删除开机自启计划任务。' -ForegroundColor Green
            $ok = $true
        }
    } catch { Write-Host ('删除计划任务失败：{0}' -f $_.Exception.Message) -ForegroundColor Yellow }
    $startupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
    $lnk = Join-Path $startupDir '设备连接监控.lnk'
    if (Test-Path -LiteralPath $lnk) {
        Remove-Item -LiteralPath $lnk -Force
        Write-Host '已删除启动文件夹快捷方式。' -ForegroundColor Green
        $ok = $true
    }
    if (-not $ok) { Write-Host '未发现开机自启项。' -ForegroundColor Yellow }
    return $true
}

function Invoke-Cleanup {
    # 清理旧日志
    try {
        $keep = [int]$script:Settings.LogRetentionDays
        if ($keep -gt 0) {
            Get-ChildItem -LiteralPath $script:LogDir -Filter 'DeviceWatch-*.log' -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$keep) } |
                Remove-Item -Force -ErrorAction SilentlyContinue
        }
    } catch { }
}

# ============================================================================
#  本地网页面板（http://127.0.0.1:端口）
#  说明：HttpListener 只在 127.0.0.1 / localhost 上监听，外网访问不到。
#        HTTP 请求用异步任务在主循环里轮询处理，不额外开线程，
#        这样可以直接读写监控的同一份内存状态，不需要进程间通信。
# ============================================================================

function Get-WebIndexHtml {
    return @'
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>设备连接监控</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
*{box-sizing:border-box}
body{margin:0;background:#0e1014;color:#e6e9ef;font:14px/1.6 "Segoe UI","Microsoft YaHei",system-ui,sans-serif}
header{display:flex;align-items:center;gap:14px;padding:12px 20px;background:#151922;border-bottom:1px solid #232833;position:sticky;top:0;z-index:10;flex-wrap:wrap}
h1{font-size:16px;margin:0;font-weight:600;letter-spacing:.5px}
h2{font-size:13px;margin:0 0 10px;font-weight:600;color:#a8b1c2;text-transform:none;display:flex;align-items:center;gap:8px}
.pill{padding:4px 12px;border-radius:999px;font-size:13px;font-weight:600;border:1px solid transparent}
.pill.ok{background:#0f2e1d;color:#4ade80;border-color:#1d6b3f}
.pill.warn{background:#332608;color:#fbbf24;border-color:#7a5c14}
.pill.bad{background:#3a1212;color:#f87171;border-color:#7f1d1d}
.pill.off{background:#242934;color:#94a3b8;border-color:#3a4150}
.meta{margin-left:auto;color:#8b93a3;font-size:13px;display:flex;gap:18px;flex-wrap:wrap}
.meta b{color:#e6e9ef;font-weight:600}
main{display:grid;grid-template-columns:repeat(12,1fr);gap:14px;padding:14px;max-width:1560px;margin:0 auto;align-items:start;grid-auto-rows:8px}
.dashbar{grid-column:1/-1;display:flex;align-items:center;gap:10px;font-size:12px;color:#6b7484;padding:0 2px}
.dashbar b{color:#8b93a3}
.dashbar button{margin-left:auto}
.pnl{grid-column:span 6}
.pnl[data-span="12"]{grid-column:span 12}
.pnl[data-span="4"]{grid-column:span 4}
.pnl.dragging{opacity:.4}
.pnl.over{outline:2px dashed #3b82f6;outline-offset:-2px}
.pnlh{display:flex;align-items:center;gap:8px}
.grip{cursor:grab;color:#454c5a;font-size:14px;user-select:none;flex:none}
.grip:active{cursor:grabbing}
.pnlh .spanbtn{margin-left:auto;flex:none;background:#161b26;border:1px solid #2b3346;color:#6b7484;border-radius:5px;font-size:11px;line-height:1;padding:3px 6px;cursor:pointer}
.pnlh .spanbtn:hover{color:#cbd5e1;border-color:#3b82f6}
.pnlh .hidebtn{margin-left:5px;flex:none;background:transparent;border:0;color:#4b5563;font-size:12px;line-height:1;padding:3px 5px;cursor:pointer;border-radius:5px}
.pnlh .hidebtn:hover{color:#f87171;background:#2a1a1c}
.hiddenbar{display:flex;align-items:center;gap:6px;flex-wrap:wrap}
.hb-lbl{font-size:12px;color:#6b7484}
.hb-chip{background:#1b2130;border:1px solid #2b3346;border-radius:6px;padding:2px 8px;font-size:11.5px;color:#8b93a3;cursor:pointer;font-family:inherit}
.hb-chip:hover{color:#4ade80;border-color:#1d6b3f}
.viswrap{position:relative;display:inline-block}
.tool-row{display:flex;flex-direction:column;align-items:flex-start;gap:4px;margin-bottom:11px}
.tool-row button{min-width:118px}
.tool-hint{font-size:11.5px;color:#6b7484;line-height:1.4}
.tool-msg{font-size:12px;margin-top:7px;min-height:17px;line-height:1.5}
.tool-msg.ok{color:#4ade80}
.tool-msg.err{color:#f87171}
.tool-msg.busy{color:#fbbf24}
.inv-out{margin-top:8px;font-size:11.5px;line-height:1.5;color:#8b93a3;max-height:320px;overflow:auto;
  white-space:pre;font-family:Consolas,"Courier New",monospace;background:#0f1319;border:1px solid #1e232e;
  border-radius:7px;padding:8px 10px;display:none}
/* 分开跑时，只显示本模式相关的卡片 */
body[data-mode="Device"] .pnl[data-panel^="hw-"]{display:none!important}
body[data-mode="Hardware"] .pnl[data-panel="problems"],
body[data-mode="Hardware"] .pnl[data-panel="events"],
body[data-mode="Hardware"] .pnl[data-panel="stats"],
body[data-mode="Hardware"] .pnl[data-panel="devices"],
body[data-mode="Hardware"] .pnl[data-panel="net"]{display:none!important}
body[data-mode="Hardware"] .devonly{display:none}
body[data-mode="Device"] .viswrap,body[data-mode="Device"] #hwModeSeg{display:none}
body[data-mode="Hardware"] .hiddenbar{display:none!important}
.modebar{grid-column:1/-1;margin:0 0 12px;padding:9px 13px;border-radius:9px;background:#132135;border:1px solid #1e4266;color:#9dc4ea;font-size:13px;line-height:1.6}
.modebar b{color:#dbeafe}
.modebar a{color:#4ade80;text-decoration:none;border-bottom:1px dashed #4ade80}
.modebar a:hover{color:#86efac}
.vispop{position:absolute;top:27px;left:0;z-index:80;background:#151922;border:1px solid #2b3346;border-radius:10px;padding:8px 12px 10px;box-shadow:0 10px 30px rgba(0,0,0,.6);max-height:66vh;overflow:auto;min-width:250px}
.vispop h4{margin:9px 0 3px;font-size:11.5px;color:#7d8698;font-weight:600;letter-spacing:.5px}
.vispop h4:first-child{margin-top:2px}
.vispop label{display:flex;align-items:center;gap:7px;font-size:12.5px;color:#cbd5e1;padding:2.5px 0;cursor:pointer;white-space:nowrap}
.vispop label:hover{color:#fff}
.vispop input{accent-color:#3b82f6;cursor:pointer}
.vp-foot{display:flex;gap:7px;margin-top:11px;border-top:1px solid #232833;padding-top:9px}
.seg{display:inline-flex;border:1px solid #2b3346;border-radius:6px;overflow:hidden;flex:none}
.seg button{background:#161b26;border:0;color:#8b93a3;font-size:11px;padding:2px 9px;cursor:pointer;line-height:1.6}
.seg button.on{background:#243049;color:#e6e9ef}
.card{background:#151922;border:1px solid #232833;border-radius:10px;padding:11px 13px;min-width:0}
.full{grid-column:1/-1}
.events{max-height:250px;overflow-y:auto;display:flex;flex-direction:column;gap:2px}
.ev{display:flex;gap:8px;align-items:baseline;padding:4px 8px;border-radius:5px;border-left:3px solid #333;background:#12151d;font-size:12.5px;white-space:nowrap;overflow:hidden}
.ev .t{color:#6b7484;font-variant-numeric:tabular-nums;font-size:12px;flex:none}
.ev .k{font-size:12px;font-weight:600;flex:none;min-width:56px}
.ev .n{font-weight:600;overflow:hidden;text-overflow:ellipsis}
.ev .d{color:#7d8698;font-size:11.5px;overflow:hidden;text-overflow:ellipsis}
.ev.off{border-color:#ef4444}.ev.off .k{color:#f87171}
.ev.on{border-color:#22c55e}.ev.on .k{color:#4ade80}
.ev.warn{border-color:#f59e0b}.ev.warn .k{color:#fbbf24}
.ev.net{border-color:#06b6d4}.ev.net .k{color:#22d3ee}
.ev.sys{border-color:#a855f7}.ev.sys .k{color:#c084fc}
.ev.bus{border-color:#ec4899}.ev.bus .k{color:#f472b6}
.ev.new{animation:flash 1.6s ease-out}
@keyframes flash{0%{background:#2a3550}100%{background:#12151d}}
.empty{color:#5b6472;padding:14px;text-align:center}
.row{display:flex;justify-content:space-between;gap:10px;padding:5px 0;border-bottom:1px dashed #232833;font-size:13px}
.row:last-child{border:0}
.row .lbl{color:#a8b1c2;min-width:0;flex:1 1 auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.row .val{flex:none;max-width:60%;font-variant-numeric:tabular-nums;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.up{color:#4ade80}.down{color:#f87171}.dim{color:#6b7484}
.plabel{font-size:11px;color:#5b6472;margin:7px 0 5px;display:flex;justify-content:space-between;gap:8px}
.plabel span{color:#4b5563}
.pings{display:flex;flex-wrap:wrap;gap:5px;margin:0 0 9px}
.chip{background:#1b2130;border:1px solid #2b3346;border-radius:6px;padding:2px 7px;font-size:11.5px;color:#8b93a3;white-space:nowrap;max-width:100%;overflow:hidden;text-overflow:ellipsis}
.chip b{color:#cbd5e1;font-weight:600;margin-left:4px}
.chip.bad{border-color:#7f1d1d}.chip.bad b{color:#f87171}
.chip.gw{background:#182234;border-color:#2b4a6f}
table{width:100%;border-collapse:collapse;font-size:13px}
th,td{text-align:left;padding:5px 6px;border-bottom:1px solid #1e232e}
th{color:#7d8698;font-weight:500;font-size:12px}
td.num{text-align:right;font-variant-numeric:tabular-nums}
.bar{height:5px;background:#232833;border-radius:3px;overflow:hidden;margin-top:3px}
.bar i{display:block;height:100%;background:#ef4444}
input[type=search]{width:100%;padding:7px 10px;margin-bottom:10px;background:#0e1014;border:1px solid #2b3140;border-radius:7px;color:#e6e9ef;font-size:13px}
input[type=search]:focus{outline:none;border-color:#3b82f6}
button{padding:5px 12px;background:#1e2533;border:1px solid #2f3748;border-radius:6px;color:#cbd5e1;cursor:pointer;font-size:12px;font-weight:500}
button:hover{background:#27303f}
.devices{max-height:460px;overflow-y:auto;display:grid;grid-template-columns:repeat(auto-fill,minmax(330px,1fr));gap:4px}
.dev{padding:6px 9px;background:#12151d;border-radius:6px;border-left:3px solid #2f3748;font-size:13px;min-width:0}
.dev .dn{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.dev .dc{color:#6b7484;font-size:11.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.dev.bad{border-color:#f59e0b}
.count{color:#6b7484;font-weight:400;font-size:12px}
/* ===== 硬件监测卡（hw- 前缀，独立于原有样式） ===== */
.hw-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(168px,1fr));gap:8px}
.hw-tile{background:#12151d;border:1px solid #1e232e;border-radius:8px;padding:7px 9px;min-width:0}
.hw-tile.wide{grid-column:span 2}
.hw-dgroup{grid-column:1/-1;display:grid;grid-template-columns:repeat(auto-fill,minmax(168px,1fr));gap:8px}
.hw-tile.full{grid-column:1/-1}
.hw-tile .hw-lbl{font-size:11px;color:#6b7484;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-bottom:1px}
.hw-tile .hw-val{font-size:17px;font-weight:700;line-height:1.25;font-variant-numeric:tabular-nums;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.hw-tile .hw-val.sm{font-size:12.5px;font-weight:600;color:#cbd5e1}
.hw-tile .hw-val .u{font-size:11px;font-weight:500;color:#7d8698;margin-left:2px}
.hw-tile .hw-bar{margin-top:5px}
.hw-tile .hw-sub2{font-size:11px;color:#5b6472;margin-top:3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.hw-box{min-width:0}   /* 不再画内框：卡片本身就是框，避免"框中框" */
.hw-h{display:flex;align-items:baseline;justify-content:space-between;gap:8px;font-size:12px;color:#7d8698;margin-bottom:2px;min-height:15px}
.hw-right{margin-left:auto}
.hw-big{font-size:23px;font-weight:700;line-height:1.3;font-variant-numeric:tabular-nums}
.hw-unit{font-size:12px;font-weight:500;color:#7d8698;margin-left:3px}
.hw-sub{font-size:11.5px;color:#6b7484;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;margin-top:6px}
.hw-empty{color:#6b7484;font-size:12.5px;padding:4px 0}
.hw-bar{height:6px;background:#232833;border-radius:3px;overflow:hidden;margin-top:5px}
.hw-bar i{display:block;height:100%;width:0;background:#4ade80;transition:width .35s ease,background .35s ease}
.hw-bar i.hw-ok{background:#4ade80}
.hw-bar i.hw-warn{background:#fbbf24}
.hw-bar i.hw-bad{background:#f87171}
.hw-ok{color:#4ade80}.hw-warn{color:#fbbf24}.hw-bad{color:#f87171}.hw-dim{color:#6b7484}
.hw-row{display:flex;justify-content:space-between;gap:8px;font-size:12.5px;padding:2px 0;font-variant-numeric:tabular-nums}
.hw-k{color:#6b7484;white-space:nowrap}
.hw-disk{padding:6px 0;border-bottom:1px dashed #232833}
.hw-disk:last-child{border-bottom:0;padding-bottom:0}
.hw-dk{font-weight:600;color:#cbd5e1;display:flex;align-items:baseline;gap:8px;flex-wrap:wrap}
.hw-temp{font-weight:600}
.hw-dk .hw-dim{font-weight:400;font-size:11.5px}
.hw-foot{grid-column:1/-1;display:flex;gap:18px;flex-wrap:wrap;font-size:12px;color:#6b7484;border-top:1px solid #1e232e;padding-top:9px}
.hw-foot b{color:#a8b1c2;font-weight:600}
.hw-metric{margin-bottom:2px}
.hw-two{display:grid;grid-template-columns:1fr 1fr;gap:10px}
.hw-box.hw-wide{grid-column:1/-1}
.hw-vol{display:flex;align-items:center;gap:9px;padding:2px 0;font-size:12.5px}
.hw-drv{width:28px;flex:none;color:#cbd5e1;font-weight:600}
.hw-vol .hw-bar{flex:1;margin-top:0}
.hw-volt{flex:none;color:#6b7484;font-variant-numeric:tabular-nums}
.hw-cols{display:grid;grid-template-columns:1fr;gap:0;margin-top:6px}
.hw-cols .hw-row{padding:1px 0;white-space:nowrap}
.hw-sub.hw-wrap{white-space:normal;word-break:break-word;overflow:visible;text-overflow:clip;line-height:1.35}

/* ===== 设置抽屉（st- 前缀） ===== */
.st-open{margin-left:2px;visibility:hidden}
.st-mask{position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,.5);opacity:0;pointer-events:none;transition:opacity .22s;z-index:40}
.st-mask.on{opacity:1;pointer-events:auto}
.st-drawer{position:fixed;top:0;right:0;bottom:0;width:430px;max-width:94vw;background:#151922;border-left:1px solid #232833;box-shadow:-14px 0 34px rgba(0,0,0,.5);transform:translateX(103%);transition:transform .24s ease;z-index:41;display:flex;flex-direction:column}
.st-drawer.on{transform:none}
.st-head{display:flex;align-items:center;gap:8px;padding:11px 14px;border-bottom:1px solid #232833}
.st-title{flex:1 1 auto;font-weight:600;font-size:14px}
.st-mini{padding:3px 9px}
.st-body{flex:1 1 auto;overflow-y:auto;padding:12px 14px;min-height:0}
.st-foot{display:flex;align-items:center;gap:10px;padding:11px 14px;border-top:1px solid #232833}
.st-footmsg{flex:1 1 auto;font-size:12px;color:#6b7484}
.st-footmsg.st-dirty{color:#fbbf24}
.st-save{padding:7px 20px;font-size:13px;font-weight:600;background:#166534;border-color:#1d6b3f;color:#bbf7d0}
.st-save:hover{background:#15803d}
.st-save[disabled]{opacity:.6;cursor:default}
.st-group{margin-bottom:16px}
.st-gname{font-size:12px;font-weight:600;color:#22d3ee;letter-spacing:.4px;margin-bottom:2px}
.st-item{padding:9px 0;border-bottom:1px dashed #232833}
.st-item:last-child{border-bottom:0;padding-bottom:2px}
.st-lbl{display:flex;align-items:center;gap:6px;flex-wrap:wrap;font-weight:600;font-size:13px}
.st-hint{font-size:11.5px;color:#6b7484;margin-top:3px;line-height:1.55;word-break:break-all}
.st-unit{font-size:11.5px;font-weight:400;color:#7d8698}
.st-restart{font-size:10.5px;font-weight:600;background:#332608;color:#fbbf24;border:1px solid #7a5c14;border-radius:4px;padding:0 5px}
.st-ctl{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-top:7px}
.st-num{width:120px;padding:5px 8px;background:#0e1014;border:1px solid #2b3140;border-radius:6px;color:#e6e9ef;font-size:13px;font-variant-numeric:tabular-nums}
.st-sel{padding:5px 8px;background:#0e1014;border:1px solid #2b3140;border-radius:6px;color:#e6e9ef;font-size:13px}
.st-txt{padding:5px 9px;background:#0e1014;border:1px solid #2b3140;border-radius:6px;color:#e6e9ef;font-size:12.5px;min-width:0}
.st-num:focus,.st-sel:focus,.st-txt:focus{outline:none;border-color:#3b82f6}
.st-sw{display:flex;align-items:center;gap:7px;font-size:12.5px;color:#a8b1c2;cursor:pointer;user-select:none}
.st-sw input{display:none}
.st-sw .st-sl{width:38px;height:20px;border-radius:999px;background:#39404f;position:relative;transition:background .18s;flex:none}
.st-sw .st-sl::after{content:"";position:absolute;top:2px;left:2px;width:16px;height:16px;border-radius:50%;background:#fff;transition:transform .18s}
.st-sw input:checked + .st-sl{background:#16a34a}
.st-sw input:checked + .st-sl::after{transform:translateX(18px)}
.st-swtxt{font-size:12px;color:#8b93a3}
.st-chips{display:flex;flex-wrap:wrap;gap:5px;align-items:center;min-height:24px}
.st-chip{display:inline-flex;align-items:center;gap:2px;background:#1b2130;border:1px solid #2b3346;border-radius:6px;padding:2px 3px 2px 8px;font-size:12px;color:#cbd5e1;max-width:100%;overflow:hidden}
.st-x{background:transparent;border:0;color:#6b7484;font-size:13px;line-height:1;padding:0 4px;cursor:pointer}
.st-x:hover{background:transparent;color:#f87171}
.st-addrow{display:flex;gap:6px;margin-top:7px}
.st-addrow .st-txt{flex:1 1 auto}
.st-dim{color:#6b7484;font-size:12px}
.st-toast{position:fixed;top:0;left:0;right:0;z-index:60;padding:10px 16px;font-size:13px;font-weight:600;text-align:center;transform:translateY(-120%);transition:transform .25s ease;box-shadow:0 6px 20px rgba(0,0,0,.35)}
.st-toast.on{transform:none}
.st-toast.st-ok{background:#0f2e1d;color:#4ade80;border-bottom:1px solid #1d6b3f}
.st-toast.st-bad{background:#3a1212;color:#f87171;border-bottom:1px solid #7f1d1d}
.st-toast.st-warn{background:#332608;color:#fbbf24;border-bottom:1px solid #7a5c14}
.st-terr{font-weight:400;font-size:12px;color:#fca5a5;margin-top:2px}
.st-twarn{font-weight:400;font-size:12px;color:#fde68a;margin-top:2px}

/* ===== 在线设备列表里的操作按钮 ===== */
.dact{margin-left:5px;padding:1px 8px;font-size:11px;line-height:1.5;border-radius:5px;background:#1b2130;border:1px solid #2b3346;color:#8b93a3}
.dact:hover{background:#27303f;color:#cbd5e1}
.dact[disabled]{opacity:.55;cursor:default}
.dact.done{background:#0f2e1d;border-color:#1d6b3f;color:#4ade80}
.sw{display:flex;align-items:center;gap:7px;font-size:13px;color:#a8b1c2;cursor:pointer;user-select:none}
.sw input{display:none}
.sw .sl{width:38px;height:20px;border-radius:999px;background:#39404f;position:relative;transition:background .18s;flex:none}
.sw .sl::after{content:"";position:absolute;top:2px;left:2px;width:16px;height:16px;border-radius:50%;background:#fff;transition:transform .18s}
.sw input:checked + .sl{background:#16a34a}
.sw input:checked + .sl::after{transform:translateX(18px)}
.prob{padding:7px 9px;background:#2a1a0c;border-left:3px solid #f59e0b;border-radius:6px;margin-bottom:5px}
.prob .pn{font-weight:600;color:#fbbf24;word-break:break-all}
.prob .pd{color:#9a8a72;font-size:12px;word-break:break-all}
@media(max-width:1100px){.pnl,.pnl[data-span="12"],.pnl[data-span="4"]{grid-column:span 12}}
</style>
</head>
<body>
<header>
  <h1>🔌 <span id="hTitle">设备连接监控</span></h1>
  <div id="pill" class="pill off">连接中…</div>
  <label class="sw" title="关闭后所有设备告警都不再弹窗（日志和网页照常记录）">
    <input type="checkbox" id="notifySw"><span class="sl"></span>弹窗通知
  </label>
  <div class="meta">
    <span class="devonly">在线设备 <b id="mDev">-</b></span>
    <span class="devonly">已记录事件 <b id="mEvt">-</b></span>
    <span>运行 <b id="mUp">-</b></span>
    <span id="mNow" class="dim"></span>
  </div>
</header>
<main id="dash">
  <div class="modebar" id="modeBar" style="display:none"></div>
  <div class="dashbar">
    <span>拖动卡片标题栏可调整顺序 · 点 <b>⬒</b> 切换半宽 / 全宽 · 布局会记住</span>
    <span class="seg" id="hwSeg"><button type="button" data-m="temp">温度</button><button type="button" data-m="load">占用</button><button type="button" data-m="both">都显示</button></span>
    <span class="count" id="hwWhen"></span>
    <span class="hiddenbar" id="hiddenBar" style="display:none"></span>
    <span class="seg" id="runModeSeg" title="切换监测范围：只盯外设插拔、只看硬件状态、或者两者都看">
      <button type="button" data-rm="Device">外设</button><button type="button" data-rm="Hardware">硬件</button><button type="button" data-rm="All">外设+硬件</button>
    </span>
    <span class="seg" id="hwModeSeg">
      <button type="button" data-hm="simple">简约</button><button type="button" data-hm="full">详细</button>
    </span>
    <span class="viswrap">
      <button id="visBtn" class="st-mini" type="button" title="选择每个卡片里要显示哪些指标">显示指标 ▾</button>
      <div id="visPop" class="vispop" style="display:none"></div>
    </span>
    <button id="layReset" class="st-mini" type="button">恢复默认布局</button>
  </div>

  <section class="card pnl" data-panel="hw-cpu" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>CPU<button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="hwCpu"><div class="empty">采集中…</div></div>
  </section>

  <section class="card pnl" data-panel="hw-mem" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>内存<button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="hwMem"><div class="empty">采集中…</div></div>
  </section>

  <section class="card pnl" data-panel="hw-gpu" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>GPU<button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="hwGpu"><div class="empty">采集中…</div></div>
  </section>

  <section class="card pnl" data-panel="hw-disk" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>硬盘<button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="hwDisk"><div class="empty">采集中…</div></div>
  </section>

  <section class="card pnl" data-panel="hw-io" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>速率<button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="hwIo"><div class="empty">采集中…</div></div>
  </section>

  <section class="card pnl" data-panel="problems" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>设备异常 <span class="count" id="probCount"></span><button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="problems"></div>
  </section>

  <section class="card pnl" data-panel="net" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>网络<button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="net"></div>
  </section>

  <section class="card pnl" data-panel="events" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>实时事件流 <span class="count" id="evCount"></span><button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div class="events" id="events"><div class="empty">等待数据…</div></div>
  </section>

  <section class="card pnl" data-panel="stats" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>断联排行 <span class="count">按物理设备，一次拔插算一次</span><button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="stats"></div>
  </section>

  <section class="card pnl" data-panel="tools" data-span="4">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>工具<button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button></h2>
    <div id="toolsBox"><div class="empty">载入中…</div></div>
  </section>

  <section class="card pnl" data-panel="devices" data-span="12">
    <h2 class="pnlh"><span class="grip" title="按住拖动排序">⠿</span>在线设备 <span class="count" id="devCount"></span>
      <button id="btnLoad" class="st-mini" type="button">载入 / 刷新</button>
      <button class="spanbtn" type="button" title="切换半宽 / 全宽">⬒</button>
    </h2>
    <input type="search" id="filter" placeholder="搜索设备名 / 类别 / 实例ID…（先点「载入 / 刷新」）">
    <div class="devices" id="devices"><div class="empty">点右上角「载入 / 刷新」读取当前在线设备</div></div>
  </section>
</main>
<button id="stOpen" class="st-open" type="button" title="打开设置面板（所有配置都能在这里改，不用手工编辑 settings.json）">⚙ 设置</button>
  <div class="st-mask" id="stMask"></div>
  <aside class="st-drawer" id="stDrawer">
    <div class="st-head">
      <div class="st-title">⚙ 设置</div>
      <button id="stReload" class="st-mini" type="button" title="重新读取当前配置（放弃未保存的修改）">重新读取</button>
      <button id="stClose" class="st-mini" type="button" title="关闭（ESC）">✕</button>
    </div>
    <div class="st-body" id="stBody"><div class="empty">打开设置后自动读取配置…</div></div>
    <div class="st-foot">
      <span class="st-footmsg" id="stFootMsg"></span>
      <button id="stSave" class="st-save" type="button">保存</button>
    </div>
  </aside>
  <div class="st-toast" id="stToast"></div>
<script>
'use strict';
var lastSeq = 0, first = true, allDevices = [], swBusy = false;
/* 渲染差分：每秒一刷时，内容没变的面板不再重写 DOM。
   之前每秒给 5 个面板重设 innerHTML，会触发无谓的重排重绘，是网页端 CPU 的主要来源。 */
var __htmlCache = {};
function setHtml(id, html){
  if(__htmlCache[id] === html) return;
  __htmlCache[id] = html;
  var el = document.getElementById(id);
  if(el) el.innerHTML = html;
}

function esc(s){ return String(s==null?'':s).replace(/[&<>"']/g, function(c){
  return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]; }); }

function kindClass(k){
  if(k==='断联'||k==='断网') return 'off';
  if(k==='接入'||k==='重新连接'||k==='恢复'||k==='网卡连接') return 'on';
  if(k==='故障'||k==='网卡断开') return 'warn';
  if(k==='总线事件') return 'bus';
  if(k==='总线恢复') return 'on';
  if(k==='系统') return 'sys';
  return 'net';
}

function render(s){
  var pl = s.problemList || [], bl = s.benignList || [];
  var pill = document.getElementById('pill');
  if(s.netDown){ pill.className='pill bad'; pill.textContent='外网断开'; pill.title=''; }
  else if(s.problems>0){
    pill.className='pill warn';
    pill.textContent = s.problems===1 ? ('⚠ '+pl[0].name+' 异常') : ('⚠ '+s.problems+' 个设备异常');
    pill.title = pl.map(function(p){ return p.name + '：' + p.text; }).join('\n');
  }
  else { pill.className='pill ok'; pill.textContent='一切正常'; pill.title=''; }

  // 通知开关（用户正在点击时不要覆盖它的状态）
  var sw = document.getElementById('notifySw');
  if(!swBusy && document.activeElement !== sw){ sw.checked = !!s.notify; }

  // 设备异常面板
  var ph = '';
  if(!pl.length){
    ph = '<div class="hw-big hw-ok">正常</div><div class="hw-sub">没有发现异常设备</div>';
  } else {
    ph = '<div class="hw-big hw-warn">' + pl.length + '</div><div class="hw-sub">个异常设备</div>';
    for(var pi=0;pi<pl.length;pi++){
      ph += '<div class="prob"><div class="pn">'+esc(pl[pi].name)+'</div>'
         +  '<div class="pd">'+esc(pl[pi].cls)+' · '+esc(pl[pi].text)+'</div></div>';
    }
  }
  if(bl.length){
    ph += '<div class="plabel">已禁用（正常，不算故障）<span>' + bl.length + ' 个</span></div>';
    for(var bi=0;bi<bl.length;bi++){
      ph += '<div class="hw-row"><span class="hw-k" title="'+esc(bl[bi].name)+'">'+esc(bl[bi].name)+'</span><span class="hw-dim">代码 '+esc(bl[bi].code)+'</span></div>';
    }
  }
  setHtml('problems', ph);
  document.getElementById('probCount').textContent = pl.length ? ('⚠ '+pl.length+' 个') : '';

  document.getElementById('mDev').textContent = s.devices;
  document.getElementById('mEvt').textContent = s.eventCount;
  document.getElementById('mUp').textContent  = s.uptime;
  document.getElementById('mNow').textContent = s.now;

  var ev = document.getElementById('events');
  if(!s.events || !s.events.length){
    setHtml('events', '<div class="empty">还没有任何设备变动。拔一个 U 盘试试。</div>');
  } else {
    var h = '';
    for(var i=0;i<s.events.length;i++){
      var e = s.events[i];
      var isNew = !first && e.seq > lastSeq;
      h += '<div class="ev '+kindClass(e.kind)+(isNew?' new':'')+'">'
         + '<span class="t">'+esc(e.t)+'</span>'
         + '<span class="k">'+esc(e.kind)+'</span>'
         + '<span class="n">'+esc(e.label)+'</span>'
         + '<span class="d">'+esc(e.detail||'')+'</span></div>';
    }
    setHtml('events', h);
  }
  if(s.events && s.events.length) lastSeq = Math.max(lastSeq, s.events[0].seq);
  window.__lastState = s;
  window.__ext = s.ext || null;
  if(window.renderTools) window.renderTools(s);
  window.__mode = s.mode || 'All';
  if(document.body.getAttribute('data-mode') !== window.__mode) document.body.setAttribute('data-mode', window.__mode);
  var _ht = document.getElementById('hTitle');
  if(_ht) _ht.textContent = (window.__mode === 'Device') ? '外设连接监测' : (window.__mode === 'Hardware' ? '硬件状态监测' : '设备连接监控');

  /* 拆分模式下告诉用户另一半在哪 —— 否则会以为"数据采集不出来" */
  /* 运行模式开关：切换后服务端会立刻停掉/开始对应部分的采样 */
  var _rms = document.getElementById('runModeSeg');
  if(_rms){
    var _rbs = _rms.getElementsByTagName('button');
    for(var _ri = 0; _ri < _rbs.length; _ri++){
      _rbs[_ri].className = (_rbs[_ri].getAttribute('data-rm') === window.__mode) ? 'on' : '';
    }
  }
  var _mb = document.getElementById('modeBar');
  if(_mb){
    if(window.__mode === 'Device'){
      _mb.style.display = '';
      _mb.innerHTML = '当前只跑着 <b>外设监测</b> —— 只盯着设备接入/拔出，硬件采样完全停掉（几乎不占 CPU、插拔响应最快）。'
                    + '想看温度/功耗/频率，点上面的 <b>外设+硬件</b> 或 <b>硬件</b>，会立刻开始采集。';
    } else if(window.__mode === 'Hardware'){
      _mb.style.display = '';
      _mb.innerHTML = '当前只跑着 <b>硬件监测</b> —— 只看温度/功耗/频率/硬盘，设备插拔检测已停掉。'
                    + '想记录外设插拔，点上面的 <b>外设+硬件</b> 或 <b>外设</b>。';
    } else {
      _mb.style.display = 'none';
    }
  }
  if(s.hwMetric) window.__hwMetric = s.hwMetric;
  if(window.renderHw) window.renderHw(s.hw);
  document.getElementById('evCount').textContent = '(' + s.eventCount + ' 条，显示最近 ' + (s.events?s.events.length:0) + ' 条)';

  var n = '';
  n += '<div class="hw-big ' + (s.netDown ? 'hw-bad' : 'hw-ok') + '">' + esc(s.internet.state) + '</div>';
  n += '<div class="hw-sub">互联网连通性</div>';
  var tg = s.internet.targets || [];
  if(tg.length){
    n += '<div class="plabel">外网探测目标</div>';
    n += '<div class="pings">';
    for(var ti=0; ti<tg.length; ti++){
      var g = tg[ti];
      var cls = 'chip' + (g.ok ? '' : ' bad') + (g.gw ? ' gw' : '');
      n += '<span class="'+cls+'" title="'+esc(g.t)+(g.gw?'（默认网关）':'')+'">'+esc(g.t)+'<b>'+(g.ok?(g.ms+'ms'):'超时')+'</b></span>';
    }
    n += '</div>';
  }
  for(var j=0;j<s.adapters.length;j++){
    var a = s.adapters[j];
    n += '<div class="hw-row"><span class="hw-k" title="'+esc(a.name)+'">'+esc(a.name)+'</span>'
      +  '<span class="'+(a.state==='Up'?'hw-ok':'hw-dim')+'">'+esc(a.state)+(a.state==='Up'&&a.speed!=='-'?' · '+esc(a.speed):'')+'</span></div>';
  }
  setHtml('net', n);

  if(!s.stats || !s.stats.length){
    setHtml('stats', '<div class="hw-big hw-dim">0</div><div class="hw-sub">还没有设备断联过</div>');
  } else {
    var max = s.stats[0].off || 1, t = '<table><tr><th>设备</th><th class="num">断联</th><th class="num">接入</th></tr>';
    for(var m=0;m<s.stats.length;m++){
      var x = s.stats[m];
      t += '<tr><td><div style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="'+esc(x.name)+'">'+esc(x.name)+'</div>'
        +  '<div class="bar"><i style="width:'+Math.round(100*x.off/max)+'%"></i></div></td>'
        +  '<td class="num">'+x.off+'</td><td class="num dim">'+x.on+'</td></tr>';
    }
    setHtml('stats', t + '</table>');
  }
  first = false;
}

function renderDevices(){
  var q = document.getElementById('filter').value.trim().toLowerCase();
  var list = allDevices;
  if(q){ list = allDevices.filter(function(d){
    return (d.name+' '+d.cls+' '+d.id).toLowerCase().indexOf(q) >= 0; }); }
  if(!list.length){ document.getElementById('devices').innerHTML = '<div class="empty">没有匹配的设备</div>'; return; }
  var h = '';
  for(var i=0;i<list.length;i++){
    var d = list[i];
    h += '<div class="dev'+(d.problem?' bad':'')+'" title="'+esc(d.id)+'">'
      +  '<div class="dn">'+esc(d.name)+'</div>'
      +  '<div class="dc">'+esc(d.cls)+' · '+esc(d.id)+'</div>'
      +  (window.deviceActions ? window.deviceActions(d.id, d.name) : '')
      +  '</div>';
  }
  document.getElementById('devices').innerHTML = h;
  document.getElementById('devCount').textContent = '(' + list.length + ' / ' + allDevices.length + ' 个)';
}

async function tick(){
  try{
    var r = await fetch('/api/state', {cache:'no-store'});
    if(!r.ok) throw 0;
    render(await r.json());
  }catch(e){
    var p = document.getElementById('pill');
    p.className='pill off'; p.textContent='监控未运行 / 已断开';
  }
}

document.getElementById('notifySw').onchange = async function(){
  var want = this.checked;
  swBusy = true;
  try{ await fetch('/api/notify?on=' + (want?1:0), {cache:'no-store'}); }catch(e){}
  swBusy = false;
};

document.getElementById('btnLoad').onclick = async function(){
  this.textContent = '读取中…';
  try{
    var r = await fetch('/api/devices', {cache:'no-store'});
    allDevices = (await r.json()).devices;
    renderDevices();
  }catch(e){ document.getElementById('devices').innerHTML='<div class="empty">读取失败</div>'; }
  this.textContent = '载入 / 刷新';
};
document.getElementById('filter').oninput = renderDevices;

tick();
setInterval(tick, 1000);
/* =========================================================================
   硬件监测卡 + 设置抽屉 + 设备操作按钮
   只定义 window.renderHw / window.initSettings / window.deviceActions
   不覆盖主程序已有的 render / tick / renderDevices 等函数
   ========================================================================= */
(function(){
'use strict';

var uiInit = false;     // initSettings 只绑定一次
var stState = {};       // key -> { def, orig, val }
var stLoaded = false;   // 设置是否已成功读取
var stBusy = false;     // 是否有请求在飞
var stTimer = null;     // 顶部提示条定时器

/* ---------- 小工具（全部对 null / undefined 容错） ---------- */
function esc(s){
  return String(s==null?'':s).replace(/[&<>"']/g, function(c){
    return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];
  });
}
function isArr(v){ return Object.prototype.toString.call(v)==='[object Array]'; }
function num(v){
  if(v===null || v===undefined || v==='') return null;
  if(typeof v==='boolean') return null;
  var n = Number(v);
  return isFinite(n) ? n : null;
}
function txt(v){ return (v===null || v===undefined || v==='') ? '-' : String(v); }
function clampf(v,a,b){ return v<a ? a : (v>b ? b : v); }
function bandCls(p){ if(p>85) return 'hw-bad'; if(p>=60) return 'hw-warn'; return 'hw-ok'; }
function tempCls(t){
  var n = num(t);
  if(n===null) return 'hw-dim';
  if(n>85) return 'hw-bad';
  if(n>=70) return 'hw-warn';
  return 'hw-ok';
}
function bar(p){
  var n = num(p);
  if(n===null) return '<div class="hw-bar"><i style="width:0"></i></div>';
  var w = clampf(n,0,100);
  return '<div class="hw-bar"><i class="'+bandCls(n)+'" style="width:'+w.toFixed(1)+'%"></i></div>';
}
function speed(kb){
  var n = num(kb);
  if(n===null) return '-';
  if(n<0) n = 0;
  if(n>=1024) return (n/1024).toFixed(2)+' MB/s';
  return n.toFixed(1)+' KB/s';
}
function tempTxt(t){
  var n = num(t);
  return n===null ? '-' : n.toFixed(1)+' °C';
}
function coresTxt(c,t){
  var a = num(c), b = num(t);
  if(a===null && b===null) return '-';
  return (a===null?'-':String(Math.round(a)))+' 核 / '+(b===null?'-':String(Math.round(b)))+' 线程';
}
function vramTxt(u,t){
  var a = num(u), b = num(t);
  if(a===null && b===null) return '-';
  if(b!==null && b>=1024){
    return (a===null?'-':(a/1024).toFixed(1))+' / '+(b/1024).toFixed(1)+' GB';
  }
  return (a===null?'-':String(Math.round(a)))+' / '+(b===null?'-':String(Math.round(b)))+' MB';
}

/* =========================================================================
   一、硬件监测卡
   ========================================================================= */
function hwNum(v){ if(v===null||v===undefined||v==='') return null; var n=Number(v); return isNaN(n)?null:n; }
function hwNz(v,suf){ if(v===null||v===undefined) return '-'; return Number(v)+(suf||''); }
function hwAttr(s){ return String(s===null||s===undefined?'':s).replace(/"/g,'&quot;').replace(/</g,'&lt;'); }
function hwRate(kb){ var n=hwNum(kb); if(n===null) return '-'; return n>=1024 ? ((Math.round(n/1024*10)/10)+' MB/s') : (Math.round(n*10)/10+' KB/s'); }
function hwTempCls(t){ t=hwNum(t); if(t===null) return 'hw-dim'; if(t>=85) return 'hw-bad'; if(t>=65) return 'hw-warn'; return 'hw-ok'; }
function hwLoadCls(p){ p=hwNum(p); if(p===null) return 'hw-dim'; if(p>=85) return 'hw-bad'; if(p>=60) return 'hw-warn'; return 'hw-ok'; }
function hwTempPct(t){ t=hwNum(t); if(t===null) return 0; return Math.max(0,Math.min(100,(t-30)/70*100)); }
function hwBlock(big, cls, pct, sub){
  return '<div class="hw-metric"><div class="hw-big '+cls+'">'+big+'</div>'
    + '<div class="hw-bar"><i class="'+cls+'" style="width:'+pct+'%"></i></div>'
    + (sub?'<div class="hw-sub">'+sub+'</div>':'')+'</div>';
}
/* NVIDIA 的性能状态（P-State）用中文说明，别直接甩 P0 / P5 给用户 */
/* 磁盘型号太长，卡片里放不下。去掉品牌前缀和容量后缀，只留型号。
   例："Samsung SSD 990 PRO 2TB" -> "990 PRO" */
function shortDisk(n){
  if(!n) return '';
  var s = String(n).replace(/^(ZHITAI|Samsung|Kingston|Western Digital|WDC|WD|Seagate|Crucial|Intel|KIOXIA|SK ?hynix|Micron|Toshiba|Lexar|Fanxiang|Netac|Colorful|Lenovo|Hikvision)\s+/i, '');
  s = s.replace(/\s+\d+(\.\d+)?\s*(GB|TB)$/i, '');
  return s;
}
function cnPstate(p){
  if(!p) return '';
  var m = /^P(\d+)$/.exec(String(p).trim());
  if(!m) return String(p);
  var n = parseInt(m[1], 10);
  if(n === 0) return '满速';
  if(n <= 4) return '高性能';
  if(n <= 7) return '平衡';
  return '节能';
}
function hwMetric(temp, load, mode){
  var t=hwNum(temp), l=hwNum(load);
  var tb = (t===null) ? '' : hwBlock(t+'<span class="hw-unit">°C</span>', hwTempCls(t), hwTempPct(t), '');
  var lb = (l===null) ? '' : hwBlock(l+'<span class="hw-unit">%</span>', hwLoadCls(l), l, '');
  if(mode==='temp') return tb || lb || '<div class="hw-empty">-</div>';
  if(mode==='load') return lb || tb || '<div class="hw-empty">-</div>';
  return '<div class="hw-two">'+(tb||'<div class="hw-empty">-</div>')+(lb||'<div class="hw-empty">-</div>')+'</div>';
}

/* ==========================================================================
   指标自选：每个卡片里显示哪些行，由这里登记，用户在网页上勾选。
   默认全开；选择存在浏览器本地（和面板布局一个机制）。
   ========================================================================== */
var METRICS = [
  {g:'CPU',  id:'cpu.temp',    n:'温度 / 占用率'},
  {g:'CPU',  id:'cpu.name',    n:'CPU 型号'},
  {g:'CPU',  id:'cpu.freq',    n:'实时频率'},
  {g:'CPU',  id:'cpu.power',   n:'封装功耗'},
  {g:'CPU',  id:'cpu.ccd',     n:'CCD1 / CCD2 温度'},
  {g:'CPU',  id:'cpu.fan',     n:'风扇转速'},
  {g:'CPU',  id:'cpu.volt',    n:'CPU 核心电压'},
  {g:'CPU',  id:'cpu.info',    n:'核心/线程 · 缓存'},
  {g:'内存', id:'mem.pct',     n:'占用率'},
  {g:'内存', id:'mem.used',    n:'已用 / 总量'},
  {g:'内存', id:'mem.free',    n:'可用'},
  {g:'内存', id:'mem.commit',  n:'已提交'},
  {g:'内存', id:'mem.modules', n:'内存条型号 / 频率'},
  {g:'内存', id:'mem.temp',    n:'内存条温度'},
  {g:'GPU',  id:'gpu.temp',    n:'温度 / 占用率'},
  {g:'GPU',  id:'gpu.name',    n:'GPU 型号'},
  {g:'GPU',  id:'gpu.vram',    n:'显存已用 / 总量'},
  {g:'GPU',  id:'gpu.power',   n:'功耗'},
  {g:'GPU',  id:'gpu.smclk',   n:'核心频率'},
  {g:'GPU',  id:'gpu.memclk',  n:'显存频率'},
  {g:'GPU',  id:'gpu.extras',  n:'核心热点 / 显存结温'},
  {g:'GPU',  id:'gpu.volt',    n:'GPU 核心电压'},
  {g:'硬盘', id:'disk.smart',  n:'温度 / 寿命 / 通电时长'},
  {g:'硬盘', id:'disk.nand',   n:'闪存(NAND) 温度'},
  {g:'硬盘', id:'disk.vol',    n:'各分区剩余空间'},
  {g:'速率', id:'io.disk',     n:'磁盘读 / 写（分盘）'},
  {g:'速率', id:'io.net',      n:'网络收 / 发'},
  {g:'速率', id:'io.misc',     n:'开机时长 / 电池 / 采样时间'},
  {g:'速率', id:'io.board',    n:'主板温度'}
];
var VIS_KEY = 'dsh-metrics-v1';
var HM_KEY  = 'dsh-hwmode-v1';

/* 「简约」模式保留的指标 —— 一眼能看出机器状态的这些 */
var SIMPLE_ON = ['cpu.temp','cpu.load','cpu.freq','cpu.power','cpu.ccd',
                 'mem.pct','mem.used','mem.temp',
                 'gpu.temp','gpu.load','gpu.vram','gpu.power','gpu.extras',
                 'disk.smart','disk.vol','io.disk','io.net'];

var hwMode = 'simple';
try { var _hm = localStorage.getItem(HM_KEY); if(_hm === 'full' || _hm === 'simple') hwMode = _hm; } catch(e){}

function visForMode(m){
  var o = {};
  for(var i=0;i<METRICS.length;i++){
    o[METRICS[i].id] = (m === 'full') ? true : (SIMPLE_ON.indexOf(METRICS[i].id) >= 0);
  }
  return o;
}

/* 用户手动勾过就用他的选择，没勾过就按模式套预设 */
var vis = null;
try { var _vr = localStorage.getItem(VIS_KEY); if(_vr) vis = JSON.parse(_vr); } catch(e){ vis = null; }
if(!vis || typeof vis !== 'object') vis = visForMode(hwMode);

function mv(id){ return vis[id] !== false; }        /* 默认显示 */
function saveVis(){ try { localStorage.setItem(VIS_KEY, JSON.stringify(vis)); } catch(e){} }

function applyHwMode(m){
  hwMode = m;
  vis = visForMode(m);
  try { localStorage.setItem(HM_KEY, m); } catch(e){}
  saveVis();
  var seg = document.getElementById('hwModeSeg');
  if(seg){
    var bs = seg.getElementsByTagName('button');
    for(var k=0;k<bs.length;k++){ bs[k].className = (bs[k].getAttribute('data-hm') === m) ? 'on' : ''; }
  }
  buildVisPop();
  if(window.__lastHw) window.renderHw(window.__lastHw);
}

function buildVisPop(){
  var p = document.getElementById('visPop'); if(!p) return;
  var groups = {}, order = [];
  for(var i=0;i<METRICS.length;i++){
    var m = METRICS[i];
    if(!groups[m.g]){ groups[m.g] = []; order.push(m.g); }
    groups[m.g].push(m);
  }
  var h = '';
  for(var g=0; g<order.length; g++){
    h += '<h4>' + esc(order[g]) + '</h4>';
    var arr = groups[order[g]];
    for(var k=0;k<arr.length;k++){
      h += '<label><input type="checkbox" data-mid="' + arr[k].id + '"' + (mv(arr[k].id) ? ' checked' : '') + '>' + esc(arr[k].n) + '</label>';
    }
  }
  h += '<div class="vp-foot"><button type="button" class="st-mini" data-vp="all">全选</button>'
     + '<button type="button" class="st-mini" data-vp="none">全不选</button>'
     + '<button type="button" class="st-mini" data-vp="reset">恢复默认</button></div>';
  p.innerHTML = h;
}

/* 运行模式切换（外设 / 硬件 / 外设+硬件） */
(function initRunMode(){
  function bind(){
    var seg = document.getElementById('runModeSeg');
    if(!seg) return;
    seg.addEventListener('click', function(e){
      var el = e.target;
      var m = el && el.getAttribute ? el.getAttribute('data-rm') : null;
      if(!m || m === window.__mode) return;
      var bs = seg.getElementsByTagName('button');
      for(var i=0;i<bs.length;i++){ bs[i].disabled = true; }
      fetch('/api/mode', {
        method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({mode:m})
      }).then(function(r){ return r.json(); }).then(function(j){
        for(var i=0;i<bs.length;i++){ bs[i].disabled = false; }
        if(j && j.ok){ window.__mode = j.mode; if(window.__lastState) window.render(window.__lastState); }
        else { alert('切换失败：' + ((j && j.err) || '未知错误')); }
      }).catch(function(){
        for(var i=0;i<bs.length;i++){ bs[i].disabled = false; }
        alert('切换失败：连不上监控程序');
      });
    });
  }
  if(document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bind);
  else bind();
})();

(function initVis(){
  function bind(){
    var btn = document.getElementById('visBtn'), pop = document.getElementById('visPop');
    var seg = document.getElementById('hwModeSeg');
    if(seg){
      var bs = seg.getElementsByTagName('button');
      for(var k=0;k<bs.length;k++){ bs[k].className = (bs[k].getAttribute('data-hm') === hwMode) ? 'on' : ''; }
      seg.addEventListener('click', function(e){
        var el = e.target;
        var m = el && el.getAttribute ? el.getAttribute('data-hm') : null;
        if(m) applyHwMode(m);
      });
    }
    if(!btn || !pop) return;
    buildVisPop();
    btn.addEventListener('click', function(e){
      e.stopPropagation();
      var open = pop.style.display !== 'none';
      if(open){ pop.style.display = 'none'; return; }
      buildVisPop();
      pop.style.display = '';
    });
    pop.addEventListener('click', function(e){
      e.stopPropagation();
      var el = e.target;
      if(el && el.tagName === 'INPUT' && el.getAttribute('data-mid')){
        vis[el.getAttribute('data-mid')] = el.checked;
        saveVis();
        if(window.__lastHw) window.renderHw(window.__lastHw);
        return;
      }
      var vb = el && el.getAttribute ? el.getAttribute('data-vp') : null;
      if(vb === 'all'){ vis = {}; for(var i=0;i<METRICS.length;i++) vis[METRICS[i].id] = true; }
      else if(vb === 'none'){ for(var j=0;j<METRICS.length;j++) vis[METRICS[j].id] = false; }
      else if(vb === 'reset'){ vis = visForMode(hwMode); }
      else return;
      saveVis(); buildVisPop();
      if(window.__lastHw) window.renderHw(window.__lastHw);
    });
    document.addEventListener('click', function(){ pop.style.display = 'none'; });
  }
  if(document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bind);
  else bind();
})();

/* ==========================================================================
   工具卡片：设备清单 / 日志文件夹 / 开机自启
   注意：这些内容会被每秒重绘，所以状态存在 window.__toolState 里，
   不能直接往 DOM 里写 —— 否则下一次刷新就没了。
   ========================================================================== */
window.__toolState = { msg: '', cls: '', inv: '', invTitle: '' };

window.renderTools = function(s){
  var box = document.getElementById('toolsBox');
  if(!box) return;
  var st = window.__toolState;
  var h = '';
  h += '<div class="tool-row"><button class="st-mini" type="button" data-tool="inv">生成设备清单</button>'
     + '<span class="tool-hint">读出每个设备的真实型号和厂商，比设备管理器里的｢USB 输入设备｣有用得多</span></div>';
  h += '<div class="tool-row"><button class="st-mini" type="button" data-tool="logs">打开日志文件夹</button>'
     + '<span class="tool-hint">在这台电脑上打开 logs 目录</span></div>';
  h += '<div class="tool-row"><button class="st-mini" type="button" data-tool="auto">'
     + (s.autostart ? '取消开机自启' : '安装开机自启') + '</button>'
     + '<span class="tool-hint">' + (s.autostart ? '当前：已开启，登录时自动启动（不弹提示）' : '当前：未开启') + '</span></div>';
  h += '<div class="tool-msg ' + st.cls + '">' + esc(st.msg) + '</div>';
  if(st.inv){
    h += '<div class="tool-hint" style="margin-top:8px">' + esc(st.invTitle) + '</div>';
    h += '<div class="inv-out" style="display:block">' + esc(st.inv) + '</div>';
  }
  setHtml('toolsBox', h);
};

(function initTools(){
  function setMsg(text, cls){ window.__toolState.msg = text; window.__toolState.cls = cls || ''; window.renderTools(window.__lastState || {}); }
  function bind(){
    document.addEventListener('click', function(e){
      var el = e.target;
      var t = (el && el.getAttribute) ? el.getAttribute('data-tool') : null;
      if(!t) return;
      e.stopPropagation();

      if(t === 'logs'){
        setMsg('正在打开…', 'busy');
        fetch('/api/openlogs', {cache:'no-store'})
          .then(function(r){ return r.json(); })
          .then(function(j){ setMsg(j && j.ok ? '已在这台电脑上打开日志文件夹' : ('打开失败：' + ((j && j.err) || '未知错误')), j && j.ok ? 'ok' : 'err'); })
          .catch(function(){ setMsg('打开失败：连不上监控程序', 'err'); });
        return;
      }

      if(t === 'auto'){
        var on = !(window.__lastState && window.__lastState.autostart);
        setMsg(on ? '正在安装开机自启…' : '正在取消开机自启…', 'busy');
        fetch('/api/autostart', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({on:on})})
          .then(function(r){ return r.json(); })
          .then(function(j){
            if(j && j.ok){ setMsg(j.installed ? '已安装开机自启（最高权限计划任务，开机不弹提示）' : '已取消开机自启', 'ok'); }
            else { setMsg('操作失败：' + ((j && j.err) || '未知错误'), 'err'); }
          })
          .catch(function(){ setMsg('操作失败：连不上监控程序', 'err'); });
        return;
      }

      if(t === 'inv'){
        setMsg('正在读取设备信息，大约需要几秒…', 'busy');
        window.__toolState.inv = '';
        window.renderTools(window.__lastState || {});
        fetch('/api/inventory', {cache:'no-store'})
          .then(function(r){ return r.json(); })
          .then(function(j){
            if(j && j.ok){
              window.__toolState.inv = j.text || '';
              window.__toolState.invTitle = '共 ' + j.count + ' 个在线设备，耗时 ' + j.ms + ' 毫秒，已存到 ' + j.file;
              setMsg('设备清单已生成', 'ok');
            } else { setMsg('生成失败：' + ((j && j.err) || '未知错误'), 'err'); }
          })
          .catch(function(){ setMsg('生成失败：连不上监控程序', 'err'); });
        return;
      }
    });
  }
  if(document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bind);
  else bind();
})();

window.renderHw = function(hw){
  window.__lastHw = hw;
  var mode = window.__hwMetric || 'temp';

  var seg = document.getElementById('hwSeg');
  if(seg){ var bs = seg.getElementsByTagName('button');
    for(var i=0;i<bs.length;i++){ bs[i].className = (bs[i].getAttribute('data-m')===mode) ? 'on' : ''; } }
  var w = document.getElementById('hwWhen');
  if(w) w.textContent = (hw && hw.LastUpdate) ? ('采样 ' + hw.LastUpdate) : '';

  if(!hw){
    var em = '<div class="empty">采集中…</div>';
    setHtml('hwCpu', em); setHtml('hwMem', em); setHtml('hwGpu', em);
    setHtml('hwDisk', em); setHtml('hwIo', em);
    return;
  }

  /* ---------------- CPU ---------------- */
  var cEx = window.__ext || {};
  var cCpuT = cEx.cpuTemps || [], cPwr = cEx.power || [], cFan = cEx.fans || [];
  // 提权后 LHM 的 CPU 核心温度比 WMI 热区准，优先用它
  var cMain = hwNum(hw.CpuTempC);
  for(var cx = 0; cx < cCpuT.length; cx++){ if(/CPU 核心|Tctl/i.test(cCpuT[cx].name)){ cMain = cCpuT[cx].value; break; } }
  var c = '<div class="hw-box">';
  if(mv('cpu.temp') || mv('cpu.load')) c += hwMetric(mv('cpu.temp') ? cMain : null, mv('cpu.load') ? hw.CpuPct : null, mode);
  if(mv('cpu.name')) c += '<div class="hw-sub hw-wrap" title="' + hwAttr(hw.CpuName) + '">' + esc(hw.CpuName || '-') + '</div>';
  if(mv('cpu.freq')) c += '<div class="hw-row"><span class="hw-k">实时频率</span><span class="hw-ok">' + (hw.CpuCurMHz ? (Math.round(hw.CpuCurMHz) + ' MHz') : '-') + '</span></div>';
  if(mv('cpu.power')){
    for(var cp2 = 0; cp2 < cPwr.length; cp2++){
      c += '<div class="hw-row"><span class="hw-k">' + esc(cPwr[cp2].name) + ' 功耗</span><span class="hw-warn">' + cPwr[cp2].value + ' W</span></div>';
    }
  }
  if(mv('cpu.ccd')){
    // CCD 是 CPU 里的核心复合体（一个 CCD 装 8 个核）。分开看能判断是哪个 CCD 在发热。
    for(var ct2 = 0; ct2 < cCpuT.length; ct2++){
      if(/^CCD\d+$/.test(cCpuT[ct2].name)){
        c += '<div class="hw-row"><span class="hw-k">' + esc(cCpuT[ct2].name) + '</span><span class="' + hwTempCls(cCpuT[ct2].value) + '">' + cCpuT[ct2].value + ' °C</span></div>';
      }
    }
  }
  if(mv('cpu.fan')){
    for(var cf2 = 0; cf2 < cFan.length; cf2++){
      c += '<div class="hw-row"><span class="hw-k">' + esc(cFan[cf2].name) + '</span><span>' + Math.round(cFan[cf2].value) + ' rpm</span></div>';
    }
  }
  if(mv('cpu.volt')){
    var cVolt = cEx.volts || [];
    for(var cv2 = 0; cv2 < cVolt.length; cv2++){
      if(/CPU/.test(cVolt[cv2].name)) c += '<div class="hw-row"><span class="hw-k">' + esc(cVolt[cv2].name) + '</span><span>' + cVolt[cv2].value + ' V</span></div>';
    }
  }
  if(mv('cpu.info')){
    var cInfo = (hw.CpuCores ? (hw.CpuCores + ' 核 / ' + hw.CpuThreads + ' 线程') : '');
    if(hw.CpuL3MB) cInfo += ' · L2 ' + hw.CpuL2MB + 'M · L3 ' + hw.CpuL3MB + 'M';
    if(cInfo) c += '<div class="hw-sub">' + esc(cInfo) + '</div>';
  }
  c += '</div>';
  setHtml('hwCpu', c);

  /* ---------------- 内存 ---------------- */
  var mp = hwNum(hw.MemPct);
  var m = '<div class="hw-box">';
  if(mv('mem.pct')) m += hwBlock((mp === null ? '-' : mp) + '<span class="hw-unit">%</span>', hwLoadCls(mp), (mp === null ? 0 : mp), '');
  if(mv('mem.used')) m += '<div class="hw-row"><span class="hw-k">已用 / 总量</span><span>' + hwNz(hw.MemUsedGB) + ' / ' + hwNz(hw.MemTotalGB) + ' GB</span></div>';
  if(mv('mem.free')) m += '<div class="hw-row"><span class="hw-k">可用</span><span>' + hwNz(hw.MemFreeGB) + ' GB</span></div>';
  if(mv('mem.commit') && hw.MemCommitMaxGB) m += '<div class="hw-row"><span class="hw-k" title="程序申请的内存总量，含页面文件；接近上限会开始大量读写页面文件">已提交</span><span>' + hwNz(hw.MemCommitGB) + ' / ' + hwNz(hw.MemCommitMaxGB) + ' GB</span></div>';
  var mm = hw.MemModules || [];
  if(mv('mem.modules') && mm.length){
    var mtot = 0;
    for(var mi = 0; mi < mm.length; mi++){ mtot += (hwNum(mm[mi].sizeGB) || 0); }
    m += '<div class="hw-sub" style="margin-top:6px">' + mm.length + ' 条 · 共 ' + mtot + ' GB';
    if(mm[0].rated && mm[0].rated !== mm[0].speed) m += ' · ' + mm[0].rated + ' MT/s（运行 ' + mm[0].speed + '）';
    else if(mm[0].speed) m += ' · ' + mm[0].speed + ' MT/s';
    if(mm.length > 1) m += ' · 双通道';
    m += '</div>';
    for(var mj = 0; mj < mm.length; mj++){
      m += '<div class="hw-row"><span class="hw-k" title="' + hwAttr(mm[mj].slot) + '">' + esc(mm[mj].part || mm[mj].mfg || '内存条') + '</span><span class="hw-dim">' + hwNz(mm[mj].sizeGB) + ' GB</span></div>';
    }
  }
  if(mv('mem.temp')){
    var exm = (window.__ext && window.__ext.memoryTemps) ? window.__ext.memoryTemps : [];
    if(exm.length){
      for(var mk = 0; mk < exm.length; mk++){
        m += '<div class="hw-row"><span class="hw-k">' + esc(exm[mk].name) + '</span><span class="' + hwTempCls(exm[mk].value) + '">' + exm[mk].value + ' °C</span></div>';
      }
      m += '<div class="hw-sub">内存温度来自内存条 SPD 传感器</div>';
    } else if(mm.length){
      m += '<div class="hw-sub">内存温度属于低层传感器，需要管理员权限</div>';
    }
  }
  m += '</div>';
  setHtml('hwMem', m);

  /* ---------------- GPU ---------------- */
  if(hw.GpuName){
    var g = '<div class="hw-box">';
    if(mv('gpu.temp') || mv('gpu.load')) g += hwMetric(mv('gpu.temp') ? hw.GpuTempC : null, mv('gpu.load') ? hw.GpuUtilPct : null, mode);
    if(mv('gpu.name')) g += '<div class="hw-sub hw-wrap" title="' + hwAttr(hw.GpuName) + '">' + esc(hw.GpuName) + (hw.GpuPstate ? '　<span class="hw-dim">' + esc(cnPstate(hw.GpuPstate)) + '</span>' : '') + '</div>';
    var gUsed = hwNum(hw.GpuMemUsedMB), gTot = hwNum(hw.GpuMemTotalMB);
    var gFill = (gUsed !== null && gTot) ? Math.round(gUsed / gTot * 100) : null;
    if(mv('gpu.vram') && gTot !== null) g += '<div class="hw-row"><span class="hw-k">显存</span><span>' + hwNz(gUsed) + '/' + hwNz(gTot) + ' MB' + (gFill !== null ? ' (' + gFill + '%)' : '') + '</span></div>';
    if(mv('gpu.power') && hwNum(hw.GpuPowerW) !== null) g += '<div class="hw-row"><span class="hw-k">功耗</span><span>' + hwNz(hw.GpuPowerW) + (hwNum(hw.GpuPowerMaxW) !== null ? ' / ' + hwNz(hw.GpuPowerMaxW) : '') + ' W</span></div>';
    if(mv('gpu.smclk') && hwNum(hw.GpuClockMHz) !== null) g += '<div class="hw-row"><span class="hw-k">核心频率</span><span>' + hwNz(hw.GpuClockMHz) + ' MHz</span></div>';
    if(mv('gpu.memclk') && hwNum(hw.GpuMemClockMHz) !== null) g += '<div class="hw-row"><span class="hw-k">显存频率</span><span>' + hwNz(hw.GpuMemClockMHz) + ' MHz</span></div>';
    if(mv('gpu.volt')){
      var gVolt = (window.__ext && window.__ext.volts) ? window.__ext.volts : [];
      for(var gv2 = 0; gv2 < gVolt.length; gv2++){
        if(/GPU/.test(gVolt[gv2].name)) g += '<div class="hw-row"><span class="hw-k">' + esc(gVolt[gv2].name) + '</span><span>' + gVolt[gv2].value + ' V</span></div>';
      }
    }
    if(mv('gpu.extras')){
      var gGpuT = (window.__ext && window.__ext.gpuTemps) ? window.__ext.gpuTemps : [];
      for(var gt = 0; gt < gGpuT.length; gt++){
        g += '<div class="hw-row"><span class="hw-k">' + esc(gGpuT[gt].name) + '</span><span class="' + hwTempCls(gGpuT[gt].value) + '">' + gGpuT[gt].value + ' °C</span></div>';
      }
    }
    g += '</div>';
    setHtml('hwGpu', g);
  } else {
    setHtml('hwGpu', '<div class="hw-box"><div class="hw-empty">未检测到独立显卡</div></div>');
  }

  /* ---------------- 硬盘 ---------------- */
  var smMap = {};
  for(var si = 0; si < (hw.DriveSmart || []).length; si++){ var sx = hw.DriveSmart[si]; smMap[String(sx.pnum)] = sx; }
  var ord = [], by = {}, disks = hw.Disks || [];
  for(var di = 0; di < disks.length; di++){
    var dd = disks[di];
    var k = (dd.pnum === null || dd.pnum === undefined || dd.pnum < 0) ? 'x' : String(dd.pnum);
    if(!by[k]){ by[k] = {model:'', items:[]}; ord.push(k); }
    by[k].items.push(dd);
    if(dd.phys) by[k].model = dd.phys;
  }
  var d = '<div class="hw-box">';
  if(!ord.length) d += '<div class="hw-empty">-</div>';
  for(var oi = 0; oi < ord.length; oi++){
    var gg = by[ord[oi]], smv = smMap[ord[oi]];
    var showHead = (gg.model || smv) && mv('disk.smart');
    var showVol = mv('disk.vol');
    if(!showHead && !showVol) continue;
    d += '<div class="hw-disk">';
    if(showHead){
      var extra = '';
      if(smv){
        extra += ' <span class="hw-temp ' + hwTempCls(smv.tempC) + '">' + smv.tempC + '°C</span>';
        if(mv('disk.nand') && smv.temp2) extra += ' <span class="hw-dim">闪存 ' + smv.temp2 + '°C</span>';
        if(smv.used !== null && smv.used !== undefined) extra += ' <span class="hw-dim">已用寿命 ' + smv.used + '%</span>';
        if(smv.hours) extra += ' <span class="hw-dim">通电 ' + Math.round(smv.hours) + ' 小时</span>';
      }
      d += '<div class="hw-dk">' + esc(gg.model || ('磁盘 #' + (oi + 1))) + extra + '</div>';
    }
    if(showVol){
      for(var vi = 0; vi < gg.items.length; vi++){
        var v = gg.items[vi], vp = hwNum(v.pct);
        d += '<div class="hw-vol"><span class="hw-drv">' + esc(v.m) + '</span>'
           + '<div class="hw-bar"><i class="' + hwLoadCls(vp) + '" style="width:' + (vp === null ? 0 : vp) + '%"></i></div>'
           + '<span class="hw-volt">' + hwNz(v.freeGB) + ' / ' + hwNz(v.totalGB) + ' GB 剩</span></div>';
      }
    }
    d += '</div>';
  }
  d += '</div>';
  setHtml('hwDisk', d);

  /* ---------------- 速率 / 其他 ---------------- */
  var ex = window.__ext || {};
  var o = '<div class="hw-box">';
  if(mv('io.disk')){
    var dio = hw.DiskIo || [];
    if(dio.length > 0){
      // 每块物理磁盘分开显示 —— 一眼看出是哪块盘在读写
      var pname = {};
      var dlist = hw.Disks || [];
      for(var dp = 0; dp < dlist.length; dp++){
        if(dlist[dp].pnum !== null && dlist[dp].pnum !== undefined && dlist[dp].pnum >= 0 && dlist[dp].phys){
          pname[String(dlist[dp].pnum)] = dlist[dp].phys;
        }
      }
      for(var dq = 0; dq < dio.length; dq++){
        var full = pname[String(dio[dq].pnum)] || ('磁盘 #' + (dio[dq].pnum + 1));
        o += '<div class="hw-row"><span class="hw-k" title="' + hwAttr(full) + '">' + esc(shortDisk(full)) + '</span>'
           + '<span>读 ' + hwRate(dio[dq].readKB) + ' · 写 ' + hwRate(dio[dq].writeKB) + '</span></div>';
      }
      o += '<div class="hw-row"><span class="hw-k">合计</span><span class="hw-dim">读 ' + hwRate(hw.DiskReadKB) + ' · 写 ' + hwRate(hw.DiskWriteKB) + '</span></div>';
    } else {
      o += '<div class="hw-row"><span class="hw-k">磁盘读</span><span>' + hwRate(hw.DiskReadKB) + '</span></div>';
      o += '<div class="hw-row"><span class="hw-k">磁盘写</span><span>' + hwRate(hw.DiskWriteKB) + '</span></div>';
    }
  }
  if(mv('io.net')){
    o += '<div class="hw-row"><span class="hw-k">网络收</span><span>' + hwRate(hw.NetRxKB) + '</span></div>';
    o += '<div class="hw-row"><span class="hw-k">网络发</span><span>' + hwRate(hw.NetTxKB) + '</span></div>';
  }
  if(mv('io.board')){
    var bds = ex.boardTemps || [];
    for(var bi2 = 0; bi2 < bds.length; bi2++){
      o += '<div class="hw-row"><span class="hw-k">' + esc(bds[bi2].name) + '</span><span class="' + hwTempCls(bds[bi2].value) + '">' + bds[bi2].value + ' °C</span></div>';
    }
  }
  if(mv('io.misc')){
    o += '<div class="hw-row"><span class="hw-k">开机时长</span><span>' + esc(hw.Uptime || '-') + '</span></div>';
    if(hw.BatPct != null) o += '<div class="hw-row"><span class="hw-k">电池</span><span>' + hw.BatPct + ' % ' + (hw.OnAC ? '接电源' : '用电池') + '</span></div>';
    o += '<div class="hw-row"><span class="hw-k">采样时间</span><span class="hw-dim">' + esc(hw.LastUpdate || '-') + '</span></div>';
  }
  o += '</div>';
  setHtml('hwIo', o);
};

/* =========================================================================
   二、设置抽屉
   ========================================================================= */
function stKey(k){ return String(k==null?'':k).replace(/[^0-9A-Za-z_]/g,'_'); }
function stListId(k){ return 'stList_'+stKey(k); }
function stSwId(k){ return 'stSw_'+stKey(k); }
function stNumId(k){ return 'stNum_'+stKey(k); }
function stSelId(k){ return 'stSel_'+stKey(k); }
function stTxtId(k){ return 'stTxt_'+stKey(k); }

function stClone(v){
  if(v===null || v===undefined) return v;
  if(isArr(v)){
    var a = [];
    for(var i=0;i<v.length;i++) a.push(v[i]);
    return a;
  }
  if(typeof v==='object'){
    var o = {};
    for(var k in v){ if(Object.prototype.hasOwnProperty.call(v,k)) o[k] = v[k]; }
    return o;
  }
  return v;
}
function stArr(v){
  var out = [];
  if(isArr(v)){
    for(var i=0;i<v.length;i++) out.push(String(v[i]));
  } else if(v!==null && v!==undefined && v!==''){ out.push(String(v)); }
  return out;
}
function stEq(a,b){
  var aa = isArr(a), bb = isArr(b);
  if(aa || bb){
    if(!aa || !bb) return false;
    if(a.length!==b.length) return false;
    for(var i=0;i<a.length;i++){ if(String(a[i])!==String(b[i])) return false; }
    return true;
  }
  if(typeof a==='boolean' || typeof b==='boolean') return !!a===!!b;
  var na = num(a), nb = num(b);
  if(na!==null && nb!==null) return na===nb;
  return String(a===null||a===undefined?'':a)===String(b===null||b===undefined?'':b);
}
function stClosest(node, root, cls){
  while(node && node!==root){
    if(node.nodeType===1 && typeof node.className==='string' && (' '+node.className+' ').indexOf(' '+cls+' ')>=0) return node;
    node = node.parentNode;
  }
  return null;
}
function stToast(kind, lines, ms){
  var t = document.getElementById('stToast');
  if(!t) return;
  t.className = 'st-toast on st-'+kind;
  t.innerHTML = lines.join('');
  if(stTimer){ clearTimeout(stTimer); stTimer = null; }
  if(ms && ms>0){
    stTimer = setTimeout(function(){ t.className = 'st-toast st-'+kind; }, ms);
  }
}

/* ---- 控件 HTML ---- */
function stChipsHtml(k, arr){
  if(!arr.length) return '<span class="st-dim">（空）</span>';
  var h = '';
  for(var i=0;i<arr.length;i++){
    h += '<span class="st-chip">'+esc(arr[i])
       + '<button type="button" class="st-x" data-stdel="'+esc(k)+'" data-sti="'+i+'" title="删除这一项">×</button></span>';
  }
  return h;
}
function stListHtml(k, arr){
  return '<div class="st-chips" id="'+stListId(k)+'">'+stChipsHtml(k,arr)+'</div>'
    + '<div class="st-addrow"><input type="text" class="st-txt" data-staddin="'+esc(k)+'" placeholder="新增一项…">'
    + '<button type="button" class="st-addbtn" data-stadd="'+esc(k)+'">添加</button></div>';
}
function stCtrlHtml(it,k,v){
  var t = String((it && it.t) || 'bool').toLowerCase();
  if(t==='bool'){
    var on = !!v;
    return '<label class="st-sw"><input type="checkbox" id="'+stSwId(k)+'" data-stk="'+esc(k)+'"'+(on?' checked':'')+'>'
      + '<span class="st-sl"></span><span class="st-swtxt">'+(on?'已开启':'已关闭')+'</span></label>';
  }
  if(t==='int' || t==='double'){
    var step = (t==='double') ? '0.1' : '1';
    var s = '<input type="number" class="st-num" id="'+stNumId(k)+'" data-stk="'+esc(k)+'" step="'+step+'"';
    var mn = num(it.min), mx = num(it.max);
    if(mn!==null) s += ' min="'+mn+'"';
    if(mx!==null) s += ' max="'+mx+'"';
    var nv = num(v);
    s += ' value="'+(nv===null?'':nv)+'">';
    if(it.unit) s += '<span class="st-unit">'+esc(it.unit)+'</span>';
    return s;
  }
  if(t==='enum'){
    var opts = isArr(it.opts) ? it.opts : [];
    var h = '<select class="st-sel" id="'+stSelId(k)+'" data-stk="'+esc(k)+'">';
    var found = false;
    for(var i=0;i<opts.length;i++){
      var o = String(opts[i]);
      var sel = (v!==null && v!==undefined && String(v)===o);
      if(sel) found = true;
      h += '<option value="'+esc(o)+'"'+(sel?' selected':'')+'>'+esc(o)+'</option>';
    }
    if(!found && v!==null && v!==undefined && v!==''){
      h += '<option value="'+esc(v)+'" selected>'+esc(v)+'</option>';
    }
    return h + '</select>';
  }
  if(t==='list') return stListHtml(k, stArr(v));
  return '<input type="text" class="st-txt" id="'+stTxtId(k)+'" data-stk="'+esc(k)+'" value="'+esc(v===null||v===undefined?'':v)+'">';
}
function stItemHtml(it){
  if(!it || it.k===null || it.k===undefined) return '';
  var k = String(it.k);
  stState[k] = { def: it, orig: stClone(it.v), val: stClone(it.v) };
  var h = '<div class="st-item">';
  h += '<div class="st-lbl">'+esc(it.label || k);
  if(it.unit) h += '<span class="st-unit">'+esc(it.unit)+'</span>';
  if(it.restart===true) h += '<span class="st-restart">重启后生效</span>';
  h += '</div>';
  if(it.hint) h += '<div class="st-hint">'+esc(it.hint)+'</div>';
  h += '<div class="st-ctl">'+stCtrlHtml(it,k,it.v)+'</div>';
  h += '</div>';
  return h;
}
function stRenderBody(groups){
  stState = {};
  var h = '';
  for(var gi=0; gi<groups.length; gi++){
    var g = groups[gi] || {};
    var items = isArr(g.items) ? g.items : [];
    h += '<div class="st-group"><div class="st-gname">'+esc(g.name || ('分组 '+(gi+1)))+'</div>';
    for(var ii=0; ii<items.length; ii++) h += stItemHtml(items[ii]);
    h += '</div>';
  }
  if(!h) h = '<div class="empty">没有可配置项</div>';
  return h;
}

/* ---- 读值 / 脏检查 ---- */
function stRead(k){
  var st = stState[k];
  if(!st) return undefined;
  var t = String((st.def && st.def.t) || 'bool').toLowerCase();
  if(t==='list') return stClone(st.val);
  if(t==='bool'){
    var e = document.getElementById(stSwId(k));
    return e ? !!e.checked : undefined;
  }
  if(t==='int' || t==='double'){
    var n = document.getElementById(stNumId(k));
    if(!n) return undefined;
    var s = String(n.value).replace(/\s+/g,'');
    if(s==='') return undefined;
    var f = parseFloat(s);
    if(!isFinite(f)) return undefined;
    var mn = num(st.def.min), mx = num(st.def.max);
    if(mn!==null && f<mn) f = mn;
    if(mx!==null && f>mx) f = mx;
    return (t==='int') ? Math.round(f) : f;
  }
  if(t==='enum'){
    var se = document.getElementById(stSelId(k));
    return se ? String(se.value) : undefined;
  }
  var tx = document.getElementById(stTxtId(k));
  return tx ? String(tx.value) : undefined;
}
function stDirtyList(){
  var out = [];
  for(var k in stState){
    if(!Object.prototype.hasOwnProperty.call(stState,k)) continue;
    var cur = stRead(k);
    if(cur===undefined) continue;
    if(!stEq(cur, stState[k].orig)) out.push(k);
  }
  return out;
}
function stMarkDirty(){
  var el = document.getElementById('stFootMsg');
  if(!el) return;
  if(!stLoaded){ el.className='st-footmsg'; el.textContent=''; return; }
  var n = stDirtyList().length;
  el.className = 'st-footmsg' + (n ? ' st-dirty' : '');
  el.textContent = n ? ('有 '+n+' 项修改未保存') : '没有未保存的修改';
}

/* ---- 读取 ---- */
async function stLoad(force){
  var body = document.getElementById('stBody');
  if(stBusy) return;
  if(stLoaded && !force) return;
  stBusy = true;
  if(body) body.innerHTML = '<div class="empty">读取配置中…</div>';
  try{
    var r = await fetch('/api/settings', {cache:'no-store'});
    if(!r.ok) throw new Error('HTTP '+r.status);
    var res = await r.json();
    var groups = null;
    if(isArr(res)) groups = res;
    else if(res && isArr(res.groups)) groups = res.groups;
    if(!groups){
      stLoaded = false;
      if(body) body.innerHTML = '<div class="empty">配置读取失败：返回格式不对</div>';
    } else {
      if(body) body.innerHTML = stRenderBody(groups);
      stLoaded = true;
    }
  }catch(e){
    stLoaded = false;
    var msg = (e && e.message) ? e.message : '未知错误';
    if(body) body.innerHTML = '<div class="empty">配置读取失败：'+esc(msg)+'<br>（主程序需要先提供 /api/settings）</div>';
  }
  stBusy = false;
  stMarkDirty();
}

/* ---- 保存 ---- */
async function stSave(){
  var btn = document.getElementById('stSave');
  if(stBusy) return;
  if(!stLoaded){ stToast('warn',['<div>配置还没读取成功</div>'],2500); return; }
  var keys = stDirtyList();
  if(!keys.length){ stToast('warn',['<div>没有需要保存的修改</div>'],2000); return; }
  var body = {};
  for(var i=0;i<keys.length;i++) body[keys[i]] = stRead(keys[i]);

  stBusy = true;
  var oldTxt = btn ? btn.textContent : '保存';
  if(btn){ btn.disabled = true; btn.textContent = '保存中…'; }
  try{
    var r = await fetch('/api/settings', {
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body: JSON.stringify(body)
    });
    var text = await r.text();
    var res = null;
    try{ res = JSON.parse(text); }catch(e1){ res = null; }
    if(!r.ok || !res || res.ok!==true){
      var em = (res && res.error) ? String(res.error) : ('HTTP '+r.status);
      stToast('bad',['<div>保存失败：'+esc(em)+'</div>'],8000);
    } else {
      var applied = isArr(res.applied) ? res.applied : [];
      var errs = isArr(res.errors) ? res.errors : [];
      var nr = isArr(res.needRestart) ? res.needRestart : [];
      for(var a=0;a<applied.length;a++){
        var ak = String(applied[a]);
        if(Object.prototype.hasOwnProperty.call(stState,ak)){
          var cv = stRead(ak);
          if(cv!==undefined) stState[ak].orig = stClone(cv);
        }
      }
      var lines = [];
      if(errs.length){
        lines.push('<div>保存完成，但有 '+errs.length+' 项失败</div>');
        for(var ei=0;ei<errs.length;ei++) lines.push('<div class="st-terr">'+esc(errs[ei])+'</div>');
      } else {
        lines.push('<div>✓ 已保存：'+esc(keys.join('、'))+'</div>');
      }
      if(nr.length){
        var ns = [];
        for(var ni=0;ni<nr.length;ni++) ns.push(String(nr[ni]));
        lines.push('<div class="st-twarn">⚠ 这些项需要重启监控才生效：'+esc(ns.join('、'))+'</div>');
      }
      var kind = errs.length ? 'bad' : 'ok';
      stToast(kind, lines, errs.length ? 8000 : (nr.length ? 5000 : 2000));
      stMarkDirty();
    }
  }catch(e){
    var m2 = (e && e.message) ? e.message : '网络错误';
    stToast('bad',['<div>保存失败：'+esc(m2)+'</div>'],8000);
  }
  stBusy = false;
  if(btn){ btn.disabled = false; btn.textContent = oldTxt || '保存'; }
}

/* ---- 列表增删 ---- */
function stRefreshList(k){
  var c = document.getElementById(stListId(k));
  if(!c || !stState[k]) return;
  c.innerHTML = stChipsHtml(k, stArr(stState[k].val));
}
function stDelItem(k, i){
  if(!k || !Object.prototype.hasOwnProperty.call(stState,k)) return;
  var arr = stArr(stState[k].val);
  var idx = parseInt(i,10);
  if(isNaN(idx) || idx<0 || idx>=arr.length) return;
  arr.splice(idx,1);
  stState[k].val = arr;
  stRefreshList(k);
  stMarkDirty();
}
function stAddItem(k, inputEl){
  if(!k || !Object.prototype.hasOwnProperty.call(stState,k)) return;
  var el = inputEl;
  if(!el){
    var c = document.getElementById(stListId(k));
    if(c && c.parentNode) el = c.parentNode.querySelector('.st-txt');
  }
  var v = el ? String(el.value||'').replace(/^\s+|\s+$/g,'') : '';
  if(!v){ if(el) el.focus(); return; }
  var arr = stArr(stState[k].val);
  for(var i=0;i<arr.length;i++){
    if(arr[i]===v){ if(el) el.value=''; return; }
  }
  arr.push(v);
  stState[k].val = arr;
  if(el) el.value = '';
  stRefreshList(k);
  stMarkDirty();
}

/* ---- 打开 / 关闭 / 事件绑定 ---- */
function stOpenDrawer(){
  var d = document.getElementById('stDrawer');
  var m = document.getElementById('stMask');
  if(d) d.className = 'st-drawer on';
  if(m) m.className = 'st-mask on';
  if(!stLoaded && !stBusy) stLoad(false);
  stMarkDirty();
}
function stCloseDrawer(){
  var d = document.getElementById('stDrawer');
  var m = document.getElementById('stMask');
  if(d) d.className = 'st-drawer';
  if(m) m.className = 'st-mask';
}
function stDelegClick(e, root){
  var del = stClosest(e.target, root, 'st-x');
  if(del){
    e.preventDefault();
    stDelItem(del.getAttribute('data-stdel'), del.getAttribute('data-sti'));
    return;
  }
  var add = stClosest(e.target, root, 'st-addbtn');
  if(add){
    e.preventDefault();
    var box = add.parentNode ? add.parentNode.querySelector('.st-txt') : null;
    stAddItem(add.getAttribute('data-stadd'), box);
  }
}
function stDelegChange(e){
  var el = e.target;
  if(!el || !el.getAttribute) return;
  var k = el.getAttribute('data-stk');
  if(k===null || k===undefined) return;
  if(el.type==='checkbox'){
    var sp = el.parentNode ? el.parentNode.querySelector('.st-swtxt') : null;
    if(sp) sp.textContent = el.checked ? '已开启' : '已关闭';
  }
  stMarkDirty();
}
function stDelegKey(e){
  var kc = e.keyCode || 0;
  if(kc!==13) return;
  var el = e.target;
  if(!el || !el.getAttribute) return;
  var av = el.getAttribute('data-staddin');
  if(av===null || av===undefined) return;
  e.preventDefault();
  stAddItem(av, el);
}

/* =========================================================================
   三、在线设备列表的操作按钮
   ========================================================================= */
window.deviceActions = function(id, name){
  var pat = '*' + String(id===null||id===undefined?'':id) + '*';
  var a = esc(pat);
  var tip = esc(name===null||name===undefined?'':name);
  return '<button type="button" class="dact" data-act="focus" data-pat="'+a+'" title="把『'+tip+'』加入重点关注">关注</button>'
    + '<button type="button" class="dact" data-act="ignore" data-pat="'+a+'" title="把『'+tip+'』加入忽略名单（不再告警）">忽略</button>';
};
async function devDelegClick(e, root){
  var btn = stClosest(e.target, root, 'dact');
  if(!btn) return;
  var act = btn.getAttribute('data-act');
  var pat = btn.getAttribute('data-pat');
  if(!act || !pat || btn.disabled) return;
  var old = btn.textContent;
  btn.disabled = true;
  btn.textContent = '处理中…';
  try{
    var r = await fetch('/api/rule?kind='+encodeURIComponent(act)+'&pattern='+encodeURIComponent(pat), {cache:'no-store'});
    if(!r.ok) throw new Error('HTTP '+r.status);
    btn.textContent = (act==='ignore') ? '已忽略' : '已关注';
    btn.className = 'dact done';
  }catch(err){
    btn.disabled = false;
    btn.textContent = old;
  }
}

/* =========================================================================
   四、初始化（只绑定一次）
   ========================================================================= */
window.initSettings = function(){
  if(uiInit) return;
  uiInit = true;

  /* 「⚙ 设置」按钮放进 header；HTML 块可能被插在 main 里，这里自动搬一次 */
  var openBtn = document.getElementById('stOpen');
  var hd = document.querySelector('header');
  if(openBtn && hd && openBtn.parentNode!==hd){
    var meta = hd.querySelector('.meta');
    if(meta) hd.insertBefore(openBtn, meta); else hd.appendChild(openBtn);
  }
  if(openBtn){
    openBtn.style.visibility = 'visible';
    openBtn.onclick = function(){ stOpenDrawer(); };
  }
  var cl = document.getElementById('stClose');
  if(cl) cl.onclick = function(){ stCloseDrawer(); };
  var mk = document.getElementById('stMask');
  if(mk) mk.onclick = function(){ stCloseDrawer(); };
  var sv = document.getElementById('stSave');
  if(sv) sv.onclick = function(){ stSave(); };
  var rl = document.getElementById('stReload');
  if(rl) rl.onclick = function(){
    stLoaded = false;
    stLoad(true);
  };

  document.addEventListener('keydown', function(e){
    var kc = e.keyCode || 0;
    if(e.key==='Escape' || kc===27) stCloseDrawer();
  });

  var dr = document.getElementById('stDrawer');
  if(dr){
    dr.addEventListener('click', function(e){ stDelegClick(e, dr); });
    dr.addEventListener('change', function(e){ stDelegChange(e); });
    dr.addEventListener('input', function(e){ stMarkDirty(); });
    dr.addEventListener('keydown', function(e){ stDelegKey(e); });
  }

  /* 设备列表会被反复重绘，所以用事件委托绑在容器上，只绑一次 */
  var dvs = document.getElementById('devices');
  if(dvs) dvs.addEventListener('click', function(e){ devDelegClick(e, dvs); });

  stLoad(false);   /* 预加载，打开抽屉时就不用等 */
};

/* 主程序可能在 JS 块之前就调用了 initSettings()，这里兜底补一次；重复调用会被 uiInit 拦住 */
if(document.readyState==='loading'){
  document.addEventListener('DOMContentLoaded', function(){ window.initSettings(); });
} else {
  window.initSettings();
}
})();
/* ==== 硬件监测 / 设置抽屉 / 设备按钮 结束 ==== */
/* ---------- 面板布局：拖动排序 + 半宽/全宽 + 记住选择 ---------- */
(function(){
  var KEY='dsh-layout-v4';   /* 卡片宽度改成统一 4 列，旧布局作废 */   /* 布局结构改过，旧键作废，避免残留的旧顺序把页面撑空 */
  var DEF_ORDER=['hw-cpu','hw-mem','hw-gpu','hw-disk','hw-io','net','problems','events','stats','tools','devices'];
  var DEF_SPAN={'hw-cpu':4,'hw-mem':4,'hw-gpu':4,'hw-disk':4,'hw-io':4,net:4,problems:4,events:4,stats:4,tools:4,devices:12};   /* 统一 4 列：一行 3 张，9 张正好铺满 3 行 */
  var lay={order:DEF_ORDER.slice(),span:{}};
  try{ var raw=localStorage.getItem(KEY); if(raw){ var o=JSON.parse(raw); if(o&&o.order&&o.order.length) lay=o; } }catch(e){}
  if(!lay.span) lay.span={};
  if(!lay.hidden) lay.hidden={};

  function dashEl(){ return document.getElementById('dash'); }
  function cards(){ var d=dashEl(); return d?Array.prototype.slice.call(d.querySelectorAll('.pnl')):[]; }
  function persist(){ try{ localStorage.setItem(KEY, JSON.stringify(lay)); }catch(e){} }
  function clearOver(){ for(var i=0;i<cards().length;i++) cards()[i].classList.remove('over'); }
  function applyOrder(){
    var d=dashEl(); if(!d) return;
    var ord=lay.order.slice();
    for(var i=0;i<DEF_ORDER.length;i++){ if(ord.indexOf(DEF_ORDER[i])<0) ord.push(DEF_ORDER[i]); }
    for(var j=0;j<ord.length;j++){
      var el=d.querySelector('.pnl[data-panel="'+ord[j]+'"]');
      if(el) d.appendChild(el);
    }
  }
  function applySpan(){
    var cs=cards();
    for(var i=0;i<cs.length;i++){
      var id=cs[i].getAttribute('data-panel');
      var sp=parseInt(lay.span[id],10); if(!sp) sp=DEF_SPAN[id]||6;
      cs[i].setAttribute('data-span', sp);
    }
  }
  /* ---------- 自适应布局（瀑布流）：按每张卡的实际高度算它占几行，
       这样矮卡片下面的空隙会被后面的卡片补上，不会留大片空白 ---------- */
  var MROW = 8, MGAP = 14;   /* 必须和 CSS 里的 grid-auto-rows / gap 一致 */
  var __span = {};
  function layoutMasonry(){
    var d = dashEl(); if(!d) return;
    var items = Array.prototype.slice.call(d.querySelectorAll('.modebar, .dashbar, .pnl'));
    for(var i=0;i<items.length;i++){
      var el = items[i];
      if(el.style.display === 'none') continue;
      var h = el.offsetHeight;                 /* align-self:start，量到的就是自然高度 */
      if(!h) continue;
      var n = Math.ceil((h + MGAP) / (MROW + MGAP));
      if(n < 1) n = 1;
      var key = el.getAttribute('data-panel') || (el.className.indexOf('modebar') >= 0 ? '__mode__' : '__bar__');
      if(__span[key] !== n){ __span[key] = n; el.style.gridRowEnd = 'span ' + n; }
    }
  }
  window.addEventListener('resize', function(){ __span = {}; layoutMasonry(); });

  function applyAll(){ applyOrder(); applySpan(); applyHidden(); layoutMasonry(); }
  applyAll();
  setInterval(layoutMasonry, 1200);   /* 卡片内容每秒会变，定期重算高度 */

  var cs=cards();
  for(var ci=0; ci<cs.length; ci++){
    (function(c){
      var hd=c.querySelector('h2'); if(!hd) return;
      hd.addEventListener('mousedown', function(e){
        var tg=e.target;
        if(tg && tg.tagName==='BUTTON') return;
        if(tg && (' '+String(tg.className)+' ').indexOf(' spanbtn ')>-1) return;
        c.setAttribute('draggable','true');
      });
      hd.addEventListener('mouseup', function(){ c.removeAttribute('draggable'); });
      c.addEventListener('dragstart', function(e){
        c.classList.add('dragging');
        try{ e.dataTransfer.setData('text/plain', c.getAttribute('data-panel')); e.dataTransfer.effectAllowed='move'; }catch(x){}
      });
      c.addEventListener('dragend', function(){ c.classList.remove('dragging'); c.removeAttribute('draggable'); clearOver(); });
      c.addEventListener('dragover', function(e){ e.preventDefault(); clearOver(); c.classList.add('over'); });
      c.addEventListener('dragleave', function(){ c.classList.remove('over'); });
      c.addEventListener('drop', function(e){
        e.preventDefault(); clearOver();
        var from=null;
        try{ from=e.dataTransfer.getData('text/plain'); }catch(x){}
        if(!from){ var dg=document.querySelector('.pnl.dragging'); if(dg) from=dg.getAttribute('data-panel'); }
        var to=c.getAttribute('data-panel');
        if(!from||from===to) return;
        var cur=[]; var cc=cards();
        for(var i=0;i<cc.length;i++) cur.push(cc[i].getAttribute('data-panel'));
        var fi=cur.indexOf(from), ti=cur.indexOf(to);
        if(fi<0||ti<0) return;
        cur.splice(fi,1); cur.splice(ti,0,from);
        lay.order=cur; persist(); applyOrder();
      });
      var hb=document.createElement('button');
      hb.type='button'; hb.className='hidebtn'; hb.textContent='✕'; hb.title='隐藏这张卡片（顶部会出现恢复按钮）';
      hd.appendChild(hb);
      hb.addEventListener('click', function(ev){
        ev.stopPropagation();
        hidePanel(c.getAttribute('data-panel'), true);
      });

      var sb=c.querySelector('.spanbtn');
      if(sb) sb.addEventListener('click', function(ev){
        ev.stopPropagation();
        var id=c.getAttribute('data-panel');
        var cur=parseInt(c.getAttribute('data-span'),10)||6;
        lay.span[id] = (cur===12)?6:12;
        persist(); applySpan();
      });
    })(cs[ci]);
  }

  /* ---------- 隐藏 / 恢复卡片 ---------- */
  var PANEL_NAME = {'hw-cpu':'CPU','hw-mem':'内存','hw-gpu':'GPU','hw-disk':'硬盘','hw-io':'速率',net:'网络',problems:'设备异常',events:'实时事件流',stats:'断联排行',tools:'工具',devices:'在线设备'};
  function renderHiddenBar(){
    var bar = document.getElementById('hiddenBar');
    if(!bar) return;
    var ids = [];
    for(var k in lay.hidden){ if(lay.hidden[k]) ids.push(k); }
    if(!ids.length){ bar.style.display = 'none'; bar.innerHTML = ''; return; }
    bar.style.display = '';
    var s = '<span class="hb-lbl">已隐藏 ' + ids.length + ' 张：</span>';
    for(var i=0;i<ids.length;i++){
      s += '<button class="hb-chip" type="button" data-id="' + ids[i] + '" title="点一下恢复显示">+ ' + esc(PANEL_NAME[ids[i]] || ids[i]) + '</button>';
    }
    s += '<button class="hb-chip" type="button" data-id="__all__" title="全部恢复">全部显示</button>';
    bar.innerHTML = s;
  }
  function applyHidden(){
    var cs = cards();
    for(var i=0;i<cs.length;i++){
      var id = cs[i].getAttribute('data-panel');
      cs[i].style.display = lay.hidden[id] ? 'none' : '';
    }
    renderHiddenBar();
  }
  function hidePanel(id, on){
    if(on){ lay.hidden[id] = true; } else { delete lay.hidden[id]; }
    persist(); applyHidden(); layoutMasonry();
  }

  var hbar = document.getElementById('hiddenBar');
  if(hbar) hbar.addEventListener('click', function(e){
    var b = e.target;
    while(b && b !== hbar && (!b.className || (' ' + b.className + ' ').indexOf(' hb-chip ') < 0)) b = b.parentNode;
    if(!b || b === hbar) return;
    var id = b.getAttribute('data-id');
    if(id === '__all__'){
      lay.hidden = {}; persist(); applyHidden(); layoutMasonry();
    } else {
      hidePanel(id, false);
    }
  });

  var rb=document.getElementById('layReset');
  if(rb) rb.addEventListener('click', function(){
    lay={order:DEF_ORDER.slice(), span:{}, hidden:{}}; persist(); applyAll();
  });

  var seg=document.getElementById('hwSeg');
  if(seg) seg.addEventListener('click', function(e){
    var b=e.target; if(!b||b.tagName!=='BUTTON') return;
    var mm=b.getAttribute('data-m'); if(!mm) return;
    window.__hwMetric=mm;
    try{ fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({HwMetric:mm})}); }catch(x){}
    if(window.__lastHw) window.renderHw(window.__lastHw);
  });
})();
/* 放在所有 UI 脚本之后调用，确保函数已经定义 */
if(window.initSettings) window.initSettings();
</script>
</body>
</html>
'@
}

function Get-WebStateJson {
    $now = Get-Date
    $up = $now - $script:StartTime
    $uptime = "{0} 天 {1:00}:{2:00}:{3:00}" -f [int]$up.Days, $up.Hours, $up.Minutes, $up.Seconds

    # 事件流：最新的在最前面
    $events = New-Object System.Collections.ArrayList
    for ($i = $script:Recent.Count - 1; $i -ge 0; $i--) {
        $r = $script:Recent[$i]
        [void]$events.Add([pscustomobject]@{
            seq    = $r.Seq
            t      = $r.Time.ToString('HH:mm:ss')
            kind   = $r.Kind
            label  = $r.Label
            detail = $r.Detail
            id     = $r.Id
        })
    }

    # 断联排行（按物理设备分组，一次拔插算一次）
    $grpStats = New-Object System.Collections.ArrayList
    foreach ($k in $script:GroupStats.Keys) {
        $g = $script:GroupStats[$k]
        if ($g.Off -eq 0 -and $g.On -eq 0) { continue }
        [void]$grpStats.Add([pscustomobject]@{ name = $g.Name; off = [int]$g.Off; on = [int]$g.On; id = $k })
    }
    $topStats = @($grpStats | Sort-Object { $_.off } -Descending | Select-Object -First 15)

    # 网卡
    $adapters = New-Object System.Collections.ArrayList
    if ($script:WebNetSnap) {
        foreach ($k in $script:WebNetSnap.Keys) {
            $a = $script:WebNetSnap[$k]
            [void]$adapters.Add([pscustomobject]@{
                name = $a.Name; state = $a.State; speed = (Format-Speed $a.Speed)
            })
        }
    }

    $obj = [pscustomobject]@{
        ok         = $true
        now        = $now.ToString('HH:mm:ss')
        start      = $script:StartTime.ToString('HH:mm:ss')
        uptime     = $uptime
        devices    = $script:Known.Count
        problems   = $script:ProblemCount
        problemList = @($script:ProblemList | ForEach-Object { [pscustomobject]@{ name = $_.name; cls = $_.cls; code = $_.code; text = $_.text } })
        benignList  = @($script:BenignList | ForEach-Object { [pscustomobject]@{ name = $_.name; cls = $_.cls; code = $_.code; text = $_.text } })
        eventCount = $script:EventCount
        netDown    = [bool]$script:NetDown
        notify     = [bool]($script:UseNotify -and $script:Settings.Notify)
        learning   = ((Get-Date) -lt $script:LearnUntil)
        internet   = [pscustomobject]@{
            state   = if ($script:NetDown) { '已断开' } else { '正常' }
            detail  = [string]$script:WebPingText
            targets = @($script:WebPingResults)
        }
        adapters   = @($adapters)
        hw         = $script:Hw
        mode       = [string]$script:Mode
        autostart  = [bool]$script:AutoStartOn
        hwMetric   = [string]$script:Settings.HwMetric
        ext        = $script:SensorsExt
        uiLagMs    = [int]$script:MaxBlockMs
        events     = @($events)
        stats      = @($topStats)
    }
    return ($obj | ConvertTo-Json -Depth 5 -Compress)
}

function Get-WebDevicesJson {
    $list = New-Object System.Collections.ArrayList
    foreach ($k in $script:Known.Keys) {
        $d = $script:Known[$k]
        [void]$list.Add([pscustomobject]@{
            name    = $d.Name
            cls     = $d.Class
            id      = $k
            problem = [int]$d.Problem
        })
    }
    $sorted = @($list | Sort-Object cls, name)
    return ([pscustomobject]@{ count = $sorted.Count; devices = $sorted } | ConvertTo-Json -Depth 4 -Compress)
}

function Invoke-WebNotifyToggle {
    param($Query)
    $on = $Query['on']
    if ($null -ne $on) {
        $script:UseNotify = ($on -eq '1' -or $on -eq 'true')
        # 和设置里的 Notify 保持一致，并落盘，否则刷新页面后状态对不上
        $script:Settings.Notify = $script:UseNotify
        try {
            $json = [pscustomobject]$script:Settings | ConvertTo-Json -Depth 6
            [System.IO.File]::WriteAllText($script:ConfigPath, $json, (New-Object System.Text.UTF8Encoding($true)))
        } catch { }
        if ($script:MenuPause) {
            try {
                $script:MenuPause.Checked = -not $script:UseNotify
                $script:MenuPause.Text = if ($script:UseNotify) { '暂停弹窗通知' } else { '恢复弹窗通知' }
            } catch { }
        }
        Write-Log ("网页端{0}了弹窗通知" -f $(if ($script:UseNotify) { '打开' } else { '关闭' }))
        try { Update-Tray } catch { }
    }
    return ([pscustomobject]@{ notify = [bool]$script:UseNotify } | ConvertTo-Json -Compress)
}

function Send-WebText {
    param($Context, [string]$Body, [string]$ContentType = 'application/json; charset=utf-8', [int]$Code = 200)
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
        $Context.Response.StatusCode = $Code
        $Context.Response.ContentType = $ContentType
        $Context.Response.ContentEncoding = [System.Text.Encoding]::UTF8
        $Context.Response.Headers.Add('Cache-Control', 'no-store')
        $Context.Response.ContentLength64 = $bytes.Length
        $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } catch { }
}

function Initialize-WebServer {
    $port = [int]$script:Settings.WebPort
    try {
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$port/")
        $listener.Prefixes.Add("http://localhost:$port/")
        $listener.Start()
        $script:WebListener = $listener
        $script:WebUrl = "http://127.0.0.1:$port/"
        $script:WebContextTask = $listener.GetContextAsync()
        Write-Log ("网页面板已启动：{0}" -f $script:WebUrl)
        return $true
    } catch {
        $script:WebListener = $null
        $script:WebContextTask = $null
        Write-Log ("网页面板启动失败（端口 {0} 可能被占用，或权限不足）：{1}" -f $port, $_.Exception.Message) -Kind 'Warn'
        return $false
    }
}

function Read-WebBody {
    param($Context)
    try {
        if (-not $Context.Request.HasEntityBody) { return '' }
        $reader = New-Object System.IO.StreamReader($Context.Request.InputStream, [System.Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        $reader.Close()
        return $body
    } catch { return '' }
}

function Invoke-WebInventoryRequest {
    # 网页上点「生成设备清单」：跑一次完整枚举，把结果整份回给前端。
    try {
        $r = Invoke-DeviceInventory -Quiet
        return (@{
            ok    = $true
            file  = [string]$r.file
            count = [int]$r.count
            ms    = [int]$r.ms
            text  = [string]$r.text
        } | ConvertTo-Json -Compress -Depth 4)
    } catch {
        return (@{ ok = $false; err = $_.Exception.Message } | ConvertTo-Json -Compress)
    }
}

function Invoke-WebOpenLogsRequest {
    # 在电脑上打开日志文件夹。监控跑在本机，直接调 explorer 即可。
    try {
        if (-not (Test-Path -LiteralPath $script:LogDir)) {
            New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
        }
        Start-Process -FilePath 'explorer.exe' -ArgumentList $script:LogDir
        return '{"ok":true}'
    } catch {
        return (@{ ok = $false; err = $_.Exception.Message } | ConvertTo-Json -Compress)
    }
}

function Get-AutostartState {
    try { return [bool](Get-ScheduledTask -TaskName 'DeviceWatch' -ErrorAction SilentlyContinue) } catch { return $false }
}

function Invoke-WebAutostartRequest {
    # GET  = 查当前状态；POST = 装 / 卸（body: {"on":true|false}）
    param($Context)
    try {
        if ($Context.Request.HttpMethod -eq 'POST') {
            $o = (Read-WebBody -Context $Context) | ConvertFrom-Json
            if ([bool]$o.on) {
                if (-not $script:AutoStartOn) { Install-Startup | Out-Null }
            } else {
                if ($script:AutoStartOn) { Uninstall-Startup | Out-Null }
            }
            # 改完立刻刷新缓存，前端下一次轮询就能看到新状态
            $script:AutoStartOn = Get-AutostartState
            $script:AutoStartChecked = Get-Date
        }
        return (@{ ok = $true; installed = [bool]$script:AutoStartOn } | ConvertTo-Json -Compress)
    } catch {
        return (@{ ok = $false; err = $_.Exception.Message } | ConvertTo-Json -Compress)
    }
}

function Invoke-WebModeRequest {
    # 网页上切换运行模式：All / Device / Hardware
    param($Context)
    try {
        $o = (Read-WebBody -Context $Context) | ConvertFrom-Json
        $m = [string]$o.mode
        if (@('All','Device','Hardware') -notcontains $m) {
            return (@{ ok = $false; err = '模式只能是 All / Device / Hardware' } | ConvertTo-Json -Compress)
        }
        $script:Mode = $m
        $script:Settings.RunMode = $m      # 存起来，下次启动沿用
        Save-CfgSettingsFile -Path ([string]$script:ConfigPath)
        Write-Log ("运行模式已切换为：{0}" -f $m)
        return (@{ ok = $true; mode = $m } | ConvertTo-Json -Compress)
    } catch {
        return (@{ ok = $false; err = $_.Exception.Message } | ConvertTo-Json -Compress)
    }
}

function Invoke-WebSettingsRequest {
    param($Context)
    if ($Context.Request.HttpMethod -eq 'POST') {
        $oldPort = [int]$script:Settings.WebPort
        $oldOn = [bool]$script:Settings.WebEnabled
        $res = Apply-WebSettings -Body (Read-WebBody -Context $Context)
        # 端口变了或开关状态变了，需要重建监听器（在响应发完之后做）
        if ([int]$script:Settings.WebPort -ne $oldPort -or [bool]$script:Settings.WebEnabled -ne $oldOn) {
            $script:WebRestartPending = $true
        }
        return $res
    }
    return (Get-WebSettingsJson)
}

function Invoke-WebRuleRequest {
    param($Query)
    $kind = [string]$Query['kind']
    $pat = [string]$Query['pattern']
    $rm = ($Query['remove'] -eq '1')
    return (Add-DeviceRule -Kind $kind -Pattern $pat -Remove:$rm)
}

# ---------------------------------------------------------------------------
#  低层传感器：内存温度 / 主板温度 / 风扇转速
#  库是随工具分发的 LibreHardwareMonitorLib（MPL-2.0），放在 lib\ 下。
#  非管理员时驱动加载不了，这里会静默跳过，不影响其它功能。
# ---------------------------------------------------------------------------
$script:LhmTried = $false
$script:LhmOk   = $false
$script:LhmPc   = $null
$script:PidFile = Join-Path $script:LogDir 'monitor.pid'

function Initialize-LhmSensors {
    $libDir = Join-Path $script:Root 'lib'
    $dll = Join-Path $libDir 'LibreHardwareMonitorLib.dll'
    if (-not (Test-Path -LiteralPath $dll)) { return }
    if (-not $script:IsAdmin) { return }
    try {
        if (-not ('SensorAsmResolver' -as [type])) {
            Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public class SensorAsmResolver {
    public static string Dir;
    static readonly HashSet<string> Busy = new HashSet<string>();
    public static void Attach() {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e) {
            try {
                var n = new AssemblyName(e.Name).Name;
                lock (Busy) { if (Busy.Contains(n)) return null; Busy.Add(n); }
                try { var p = Path.Combine(Dir, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
                finally { lock (Busy) { Busy.Remove(n); } }
                return null;
            } catch { return null; }
        };
    }
}
"@
        }
        [SensorAsmResolver]::Dir = $libDir
        [SensorAsmResolver]::Attach()
        $null = [System.Reflection.Assembly]::LoadFrom($dll)
        $c = New-Object LibreHardwareMonitor.Hardware.Computer
        $c.IsCpuEnabled         = $true
        $c.IsMemoryEnabled      = $true
        $c.IsMotherboardEnabled = $true
        $c.IsControllerEnabled  = $true
        $c.IsGpuEnabled         = $true    # GPU 热点温度 / 显存结温只有它能读
        $c.Open()
        $script:LhmPc = $c
        $script:LhmOk = $true
        Write-Log '低层传感器已启用（内存温度 / 主板温度 / 风扇转速）'
    } catch {
        $script:LhmOk = $false
        Write-Log ("低层传感器初始化失败：{0}" -f $_.Exception.Message) -Kind 'Warn'
    }
}

function Get-CnSensorName([string]$Name) {
    # LibreHardwareMonitor 报出来的传感器名全是英文，统一翻成中文再展示。
    # 先查表，再按模式匹配，最后原样返回 —— 宁可显示英文，也不显示猜错的翻译。
    $map = @{
        'Core (Tctl/Tdie)'    = 'CPU 核心'
        'CCDs Max (Tdie)'     = 'CCD 最高'
        'CCDs Average (Tdie)' = 'CCD 平均'
        'CCD1 (Tdie)'         = 'CCD1'
        'CCD2 (Tdie)'         = 'CCD2'
        'CCD3 (Tdie)'         = 'CCD3'
        'Package'             = 'CPU 封装'
        'CPU Package'         = 'CPU 封装'
        'GPU Hot Spot'        = '核心热点'
        'GPU Memory Junction' = '显存结温'
        'GPU Core'            = 'GPU 核心'
        'GPU Memory'          = '显存'
        'GPU Fan'             = 'GPU 风扇'
        'CPU Fan'             = 'CPU 风扇'
        'System Fan'          = '机箱风扇'
        'GPU VRM'             = '显卡供电'
        'VRM'                 = '供电模块'
        'VRM MOS'             = '供电模块'
        'Chipset'             = '芯片组'
        'Motherboard'         = '主板'
        'System'              = '主板'
        'Ambient'             = '机箱环境'
        'Memory'              = '内存'
        'Temperature'         = '温度'
        'CPU Cores'           = 'CPU 核心'
        'Bus Speed'           = '总线频率'
        'CPU IOD Hotspot'     = 'IOD 热点'
        'CPU SoC'             = 'SoC'
        'CPU VDDCR_SOC'       = 'SoC 电压'
    }
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    if ($map.ContainsKey($Name)) { return $map[$Name] }
    if ($Name -match '^DIMM\s*#?\s*(\d+)$')  { return '内存条 ' + ([int]$Matches[1] + 1) }
    if ($Name -match '^CPU Core\s*#(\d+)$')  { return '核心 ' + $Matches[1] }
    if ($Name -match '^Core\s*#(\d+)$')      { return '核心 ' + $Matches[1] }
    if ($Name -match '^Fan\s*#?\s*(\d+)$')   { return '风扇 ' + $Matches[1] }
    if ($Name -match '^PCH')                 { return '芯片组' }
    if ($Name -match '^GPU')                 { return ($Name -replace '^GPU\s*','显卡 ') }
    if ($Name -match '^CPU')                 { return ($Name -replace '^CPU\s*','CPU ') }
    return $Name
}

function Read-LhmSensors {
    if (-not $script:LhmOk -or $null -eq $script:LhmPc) { return $null }
    $mem  = New-Object System.Collections.ArrayList
    $cpuT = New-Object System.Collections.ArrayList
    $fans = New-Object System.Collections.ArrayList
    $pwr  = New-Object System.Collections.ArrayList
    $gpuT = New-Object System.Collections.ArrayList
    $volt = New-Object System.Collections.ArrayList
    # 名字里带这些词的是"上限 / 临界值 / 分辨率"，不是实时温度，要排掉
    $junk = 'Resolution|Limit|Critical|Warning'

    $targets = New-Object System.Collections.ArrayList
    foreach ($dev in $script:LhmPc.Hardware) {
        [void]$targets.Add($dev)
        foreach ($sub in $dev.SubHardware) { [void]$targets.Add($sub) }
    }
    foreach ($dev in $targets) {
        try { $dev.Update() } catch { }
        $ht = [string]$dev.HardwareType
        foreach ($s in $dev.Sensors) {
            try {
                if ($null -eq $s.Value) { continue }
                $st = [string]$s.SensorType
                $nm = [string]$s.Name
                $v  = [math]::Round([double]$s.Value, 1)
                if ($st -eq 'Temperature') {
                    if ($nm -match $junk -or $v -le 0) { continue }
                    if ($ht -eq 'Memory') { [void]$mem.Add([pscustomobject]@{ name = (Get-CnSensorName $nm); value = $v }) }
                    elseif ($ht -eq 'Cpu' -and $nm -match 'Tctl|Tdie') { [void]$cpuT.Add([pscustomobject]@{ name = (Get-CnSensorName $nm); value = $v }) }
                    elseif ($ht -match '^Gpu') {
                        # 只要 nvidia-smi 给不了的那两个：核心热点、显存结温
                        if ($nm -match 'Hot Spot') { [void]$gpuT.Add([pscustomobject]@{ name = '核心热点'; value = $v }) }
                        elseif ($nm -match 'Memory Junction') { [void]$gpuT.Add([pscustomobject]@{ name = '显存结温'; value = $v }) }
                    }
                } elseif ($st -eq 'Fan') {
                    if ($v -gt 0) { [void]$fans.Add([pscustomobject]@{ name = (Get-CnSensorName $nm); value = $v }) }
                } elseif ($st -eq 'Voltage') {
                    # 只要两个有意义的：CPU 核心电压、GPU 核心电压（每个核都有一份 VID，取第一个就够）
                    if ($ht -eq 'Cpu' -and $nm -match 'VID' -and $volt.Count -eq 0) {
                        [void]$volt.Add([pscustomobject]@{ name = 'CPU 核心电压'; value = $v })
                    } elseif ($ht -match '^Gpu' -and $nm -match 'Core Voltage') {
                        [void]$volt.Add([pscustomobject]@{ name = 'GPU 核心电压'; value = $v })
                    }
                } elseif ($st -eq 'Power') {
                    # 只要 CPU 的整包功耗。注意必须限定 $ht -eq 'Cpu' ——
                    # GPU 也有个叫 "GPU Package" 的功耗项，不限定就会被当成 CPU 功耗混进来。
                    if ($ht -eq 'Cpu' -and $nm -match 'Package') { [void]$pwr.Add([pscustomobject]@{ name = 'CPU 封装'; value = $v }) }
                }
            } catch { }
        }
    }
    return [pscustomobject]@{
        at = (Get-Date).ToString('HH:mm:ss'); source = 'admin'
        memoryTemps = @($mem); cpuTemps = @($cpuT)
        fans = @($fans); power = @($pwr); gpuTemps = @($gpuT); volts = @($volt)
    }
}


function Invoke-UiPump {
    # 托盘菜单和网页都靠这里泵消息。顺便记录"两次泵之间隔了多久"——
    # 间隔过大就说明某段代码把主循环卡住了，日志里能直接看到卡了多久。
    $now = Get-Date
    if ($script:LastPumpAt) {
        $gap = ($now - $script:LastPumpAt).TotalMilliseconds
        if ($gap -gt $script:MaxBlockMs) {
            $script:MaxBlockMs = $gap
            # 记下卡顿时正在跑哪一段，日志里就能直接看出元凶
            $script:MaxBlockWhere = $script:Phase
        }
    }
    $script:LastPumpAt = $now
    if ($script:Tray) { try { [System.Windows.Forms.Application]::DoEvents() } catch { } }
    if ($script:WebListener) { Invoke-WebPump }
}

function Invoke-WebPump {
    if (-not $script:WebListener) { return }
    try {
        if ($script:WebContextTask -and $script:WebContextTask.IsCompleted) {
            $ctx = $null
            try { $ctx = $script:WebContextTask.Result } catch { $ctx = $null }
            $script:WebContextTask = $null
            if ($ctx) {
                try {
                    $path = $ctx.Request.Url.AbsolutePath
                    switch -Regex ($path) {
                        '^/(index\.html)?$' { Send-WebText -Context $ctx -Body (Get-WebIndexHtml) -ContentType 'text/html; charset=utf-8' }
                        '^/api/state$'     { Send-WebText -Context $ctx -Body (Get-WebStateJson) }
                        '^/api/devices$'   { Send-WebText -Context $ctx -Body (Get-WebDevicesJson) }
                        '^/api/notify'     { Send-WebText -Context $ctx -Body (Invoke-WebNotifyToggle -Query $ctx.Request.QueryString) }
                        '^/api/settings$'  { Send-WebText -Context $ctx -Body (Invoke-WebSettingsRequest -Context $ctx) }
                        '^/api/rule'       { Send-WebText -Context $ctx -Body (Invoke-WebRuleRequest -Query $ctx.Request.QueryString) }
                        '^/api/mode$'      { Send-WebText -Context $ctx -Body (Invoke-WebModeRequest -Context $ctx) }
                        '^/api/inventory$' { Send-WebText -Context $ctx -Body (Invoke-WebInventoryRequest) }
                        '^/api/openlogs$'  { Send-WebText -Context $ctx -Body (Invoke-WebOpenLogsRequest) }
                        '^/api/autostart$' { Send-WebText -Context $ctx -Body (Invoke-WebAutostartRequest -Context $ctx) }
                        '^/favicon\.ico$'  { Send-WebText -Context $ctx -Body '' -ContentType 'image/x-icon' -Code 204 }
                        default            { Send-WebText -Context $ctx -Body '{"error":"not found"}' -Code 404 }
                    }
                } catch { }
                try { $ctx.Response.Close() } catch { }
                # 端口/开关变更：响应发完之后再重建监听器
                if ($script:WebRestartPending) {
                    $script:WebRestartPending = $false
$script:ChangeAt = $null      # 本轮变化第一次被发现的时刻（算端到端延迟用）
$script:Phase = '启动'          # 看门狗用：当前正在跑哪一段
$script:MaxBlockWhere = ''
$script:AutoStartOn = $false     # 开机自启状态缓存（Get-ScheduledTask 一次要 500ms，绝不能放进 /api/state）
$script:AutoStartChecked = [datetime]::MinValue
$script:SensorsExt  = $null      # 低层传感器（内存温度 / CPU 功耗 / GPU 热点等）
$script:NextExtAt   = [datetime]::MinValue
$script:LastPumpAt   = $null
$script:MaxBlockMs   = 0.0
                    try { $script:WebListener.Stop(); $script:WebListener.Close() } catch { }
                    $script:WebListener = $null
                    $script:WebContextTask = $null
                    if ($script:Settings.WebEnabled) {
                        try { Initialize-WebServer | Out-Null } catch { }
                    } else {
                        Write-Log '网页面板已按设置关闭'
                    }
                }
            }
        }
        if (-not $script:WebContextTask -and $script:WebListener.IsListening) {
            $script:WebContextTask = $script:WebListener.GetContextAsync()
        }
    } catch {
        # 监听器出问题就重置，下一轮重试
        $script:WebContextTask = $null
    }
}

# ============================================================================
#  配置模块（已内联，原 _build\cfg.ps1）
# ============================================================================
# ============================================================================
#  cfg.ps1 —— 独立配置管理 + Web API 模块（DeviceWatch 的网页面板后端）
#
#  作用：
#    1) 用带元数据的 schema 描述 $script:Settings 里的每一项配置
#    2) Get-WebSettingsJson  把配置连同元数据、当前值打包成 JSON 给网页
#    3) Apply-WebSettings    校验网页提交的 JSON，合法项直接写回 $script:Settings
#                             并整体保存到 $script:ConfigPath
#    4) Add-DeviceRule       增删 IgnorePatterns / FocusPatterns
#
#  约定：
#    - 本文件由 DeviceWatch.ps1 点源（. .\_build\cfg.ps1）加载，
#      因此 $script: 作用域就是主程序的脚本作用域，直接读写 $script:Settings。
#    - 不定义 $script:Settings，只描述它（主程序已经有初值）。
#    - 兼容 Windows PowerShell 5.1：不使用 ?? / ?. / 三目运算符。
#    - 本文件所有函数只返回字符串，不做任何控制台输出。
# ============================================================================

# ---------------------------------------------------------------------------
#  配置项 schema（每项一个哈希表）
#    k       配置键名，必须和 $script:Settings 的键完全一致
#    t       类型 bool / int / double / enum / list
#    label   中文短标签
#    hint    中文说明
#    unit    单位，没有就空字符串
#    min/max int、double 的取值范围（其他类型为 $null）
#    opts    enum 的可选值（其他类型为 $null）
#    restart $true 表示改完要重启监控才生效
#    group   所属分组（仅本文件内部使用，不输出给网页）
# ---------------------------------------------------------------------------

$script:SettingsGroupOrder = @('通知', '采集', '监控范围', '外网探测', '网页面板', '日志')

$script:SettingsSchema = @(

    # ---------------- 通知 ----------------
    @{ k = 'Notify';             t = 'bool';   label = '弹窗总开关';   hint = '关闭后设备变化、断网、硬件错误都不再弹窗提醒。';                 unit = '';     min = $null; max = $null;   opts = $null;                restart = $false; group = '通知' }
    @{ k = 'Sound';              t = 'bool';   label = '提示音';       hint = '弹窗时同时播放系统提示音，安静场合可以关掉。';                   unit = '';     min = $null; max = $null;   opts = $null;                restart = $false; group = '通知' }
    @{ k = 'NotifyEventLog';     t = 'bool';   label = '日志错误弹窗'; hint = '系统日志里出现硬件错误时也弹窗提醒。';                           unit = '';     min = $null; max = $null;   opts = $null;                restart = $false; group = '通知' }
    @{ k = 'ToastDuration';      t = 'enum';   label = '弹窗时长';     hint = '系统通知在屏幕上停留的时间档位，short 短、long 长。';             unit = '';     min = $null; max = $null;   opts = @('short', 'long');   restart = $false; group = '通知' }
    @{ k = 'NotifyCooldownSec';  t = 'int';    label = '同设备冷却';   hint = '同一台设备在这个秒数内只弹一次窗，防止刷屏。';                   unit = '秒';   min = 1;     max = 600;     opts = $null;                restart = $false; group = '通知' }
    @{ k = 'CoalesceSec';        t = 'double'; label = '变化合并窗口'; hint = '这段时间内的设备变化合并成一次提醒，设 0 表示立刻弹。';           unit = '秒';   min = 0;     max = 10;      opts = $null;                restart = $false; group = '通知' }
    @{ k = 'BusEventThreshold';  t = 'int';    label = '总线事件阈值'; hint = '同一个窗口内这么多台设备一起变化，就合并成一条「总线事件」提醒。'; unit = '台';   min = 2;     max = 20;      opts = $null;                restart = $false; group = '通知' }
    @{ k = 'FocusPatterns';      t = 'list';   label = '重点关注设备'; hint = '匹配这些通配符的设备会加 ⭐ 并在提醒里优先显示。';               unit = '';     min = $null; max = $null;   opts = $null;                restart = $false; group = '通知' }

    # ---------------- 采集 ----------------
    @{ k = 'PollMs';             t = 'int';    label = '设备轮询间隔'; hint = '每隔多少毫秒重新枚举一次在线设备，越小越灵敏但越费 CPU。';       unit = '毫秒'; min = 100;   max = 5000;    opts = $null;                restart = $false; group = '采集' }
    @{ k = 'DebounceMs';         t = 'int';    label = '插拔去抖';     hint = '设备出现或消失后等这么久再确认，避免 USB 抖动造成误报。';         unit = '毫秒'; min = 0;     max = 3000;    opts = $null;                restart = $false; group = '采集' }
    @{ k = 'NetCheckMs';         t = 'int';    label = '网卡检查间隔'; hint = '每隔多少毫秒检查一次网卡的 Up / Down 状态。';                   unit = '毫秒'; min = 500;   max = 60000;   opts = $null;                restart = $false; group = '采集' }
    @{ k = 'FullScanSec';        t = 'int';    label = '完整扫描间隔'; hint = '每隔多少秒做一次全量设备详情扫描，用来发现「设备还在但出故障」。'; unit = '秒';   min = 5;     max = 3600;    opts = $null;                restart = $false; group = '采集' }
    @{ k = 'EventLogCheckSec';   t = 'int';    label = '日志检查间隔'; hint = '每隔多少秒读一次系统日志里的硬件错误。';                         unit = '秒';   min = 2;     max = 600;     opts = $null;                restart = $false; group = '采集' }
    @{ k = 'HwSampleMs';         t = 'int';    label = '硬件采样间隔'; hint = '每隔多少毫秒采样一次温度、占用率等硬件数据。';                   unit = '毫秒'; min = 500;   max = 60000;   opts = $null;                restart = $false; group = '采集' }
    @{ k = 'LhmSampleSec'; t = 'int'; label = '低层传感器间隔'; hint = '内存温度 / CPU 功耗 / GPU 热点这类走驱动读的传感器，每隔多少秒读一次。变化慢，调大能省 CPU。'; unit = '秒'; min = 2; max = 120; opts = $null; restart = $false; group = '采集' },
    @{ k = 'HwMetric';           t = 'enum';   label = '硬件主指标';   hint = '硬件面板上的大字显示温度还是占用率。';                           unit = '';     min = $null; max = $null;   opts = @('temp', 'load', 'both'); restart = $false; group = '采集' }

    # ---------------- 监控范围 ----------------
    @{ k = 'WatchNetwork';          t = 'bool'; label = '监控网卡';     hint = '网卡 Up / Down 时提醒。';                                            unit = ''; min = $null; max = $null; opts = $null; restart = $false; group = '监控范围' }
    @{ k = 'WatchInternet';         t = 'bool'; label = '监控外网';     hint = '外网从通变断或从断变通时提醒。';                                     unit = ''; min = $null; max = $null; opts = $null; restart = $false; group = '监控范围' }
    @{ k = 'WatchEventLog';         t = 'bool'; label = '监控日志错误'; hint = '读取系统日志里的硬件错误并提醒，关闭后不再读日志。';                 unit = ''; min = $null; max = $null; opts = $null; restart = $false; group = '监控范围' }
    @{ k = 'IgnoreSoftwareDevices'; t = 'bool'; label = '忽略虚拟设备'; hint = '忽略 SW\ 、SWD\ 、ROOT\ 开头的软件虚拟设备，减少无意义的提醒。';      unit = ''; min = $null; max = $null; opts = $null; restart = $false; group = '监控范围' }
    @{ k = 'IgnorePatterns';        t = 'list'; label = '忽略规则';     hint = '匹配这些通配符的设备完全不再监控，例如 *WPDBUSENUM*。';              unit = ''; min = $null; max = $null; opts = $null; restart = $false; group = '监控范围' }

    # ---------------- 外网探测 ----------------
    @{ k = 'PingTargets';           t = 'list'; label = '探测目标';     hint = '用来判断外网是否连通的 IP 列表，网关会自动加入，不用手写。';         unit = '';   min = $null; max = $null; opts = $null; restart = $false; group = '外网探测' }
    @{ k = 'PingTimeoutMs';         t = 'int';  label = '单目标超时';   hint = 'Ping 一个目标最多等多少毫秒，超时就记为失败。';                     unit = '毫秒'; min = 200; max = 10000; opts = $null; restart = $false; group = '外网探测' }
    @{ k = 'InternetFailThreshold'; t = 'int';  label = '断网判定次数'; hint = '连续失败这么多次才判定为断网，调大可以避免误报。';                   unit = '次';   min = 1;   max = 20;    opts = $null; restart = $false; group = '外网探测' }

    # ---------------- 网页面板 ----------------
    @{ k = 'WebEnabled'; t = 'bool'; label = '网页面板'; hint = '开启后可以用浏览器打开本机网页查看和控制监控，只监听 127.0.0.1。'; unit = '';   min = $null; max = $null; opts = $null; restart = $true; group = '网页面板' }
    @{ k = 'WebPort';    t = 'int';  label = '网页端口'; hint = '网页面板监听的端口号，被占用时可以换一个。';                       unit = '端口'; min = 1024;  max = 65535; opts = $null; restart = $true; group = '网页面板' }

    # ---------------- 日志 ----------------
    @{ k = 'LogRetentionDays'; t = 'int';  label = '日志保留天数'; hint = '日志保留多少天，超期的自动清理，设 0 表示永久保留。';         unit = '天'; min = 0; max = 3650; opts = $null; restart = $false; group = '日志' }
    @{ k = 'ShowTray';         t = 'bool'; label = '托盘图标';     hint = '是否在任务栏通知区域显示托盘图标，改动后需要重启监控。';       unit = '';   min = $null; max = $null; opts = $null; restart = $true; group = '日志' }
    @{ k = 'MaxLogFileMB';     t = 'int';  label = '单文件体积上限'; hint = '单个日志文件最大多少 MB，超过就自动切分新文件。';           unit = 'MB'; min = 1; max = 1024; opts = $null; restart = $false; group = '日志' }
)

# ============================================================================
#  内部工具函数
# ============================================================================

# 按 k 找 schema 项，找不到返回 $null
function Get-CfgSchemaItem {
    param([string]$Key)
    foreach ($it in @($script:SettingsSchema)) {
        if ([string]$it['k'] -eq $Key) { return $it }
    }
    return $null
}

# 判断是否是数字（排除 bool / string / 数组）
function Test-CfgNumber {
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return $false }
    if ($Value -is [string]) { return $false }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [int16] -or $Value -is [byte] -or
        $Value -is [uint32] -or $Value -is [uint64] -or $Value -is [uint16] -or
        $Value -is [single] -or $Value -is [double] -or $Value -is [decimal]) { return $true }
    return $false
}

# 取配置当前值，按 schema 类型归一化
#   list   -> 字符串数组（一定是数组，哪怕只有 1 个元素或 0 个元素）
#   bool   -> [bool]
#   int    -> [int]
#   double -> [double]
#   其他   -> [string]
function Get-CfgCurrentValue {
    param($Item)
    $key  = [string]$Item['k']
    $type = [string]$Item['t']

    $raw = $null
    if ($null -ne $script:Settings -and $script:Settings.Contains($key)) { $raw = $script:Settings[$key] }

    if ($type -eq 'list') {
        $out = New-Object System.Collections.ArrayList
        if ($null -ne $raw) {
            foreach ($x in @($raw)) {
                if ($null -eq $x) { continue }
                if ($x -is [bool] -or $x -is [System.Array]) { continue }
                $s = [string]$x
                if ($s.Trim().Length -gt 0) { [void]$out.Add($s) }
            }
        }
        # 前面加逗号：避免 PowerShell 把数组拆成多个返回值
        return , @($out.ToArray())
    }

    if ($type -eq 'bool') {
        if ($null -eq $raw) { return $false }
        if ($raw -is [bool]) { return [bool]$raw }
        $s = ([string]$raw).Trim()
        if ($s -eq '1' -or $s.ToLower() -eq 'true') { return $true }
        return $false
    }

    if ($type -eq 'int') {
        if ($null -eq $raw) { return 0 }
        try { return [int]$raw } catch { return 0 }
    }

    if ($type -eq 'double') {
        if ($null -eq $raw) { return [double]0 }
        try { return [double]$raw } catch { return [double]0 }
    }

    if ($null -eq $raw) { return '' }
    return [string]$raw
}

# 给值算一个可比较的“指纹”，用于判断值有没有真的变化
function Get-CfgValueSignature {
    param($Value)
    if ($null -eq $Value) { return '<null>' }
    if ($Value -is [bool]) { if ($Value) { return 'True' } else { return 'False' } }
    if ($Value -is [System.Array]) {
        $parts = New-Object System.Collections.ArrayList
        foreach ($x in $Value) { [void]$parts.Add([string]$x) }
        return '[' + ($parts.ToArray() -join '|') + ']'
    }
    return [string]$Value
}

# 校验 + 转换网页提交的单个值
# 返回 @{ ok = $true/$false; value = 归一化后的值; error = '失败原因' }
function Convert-CfgIncomingValue {
    param($Item, $Value)

    $type = [string]$Item['t']
    $bad  = @{ ok = $false; value = $null; error = '类型不对' }

    # ---- bool ----
    if ($type -eq 'bool') {
        if ($Value -is [bool]) { return @{ ok = $true; value = [bool]$Value; error = '' } }
        return @{ ok = $false; value = $null; error = '类型不对，应为 true 或 false' }
    }

    # ---- enum ----
    if ($type -eq 'enum') {
        if ($Value -isnot [string]) {
            return @{ ok = $false; value = $null; error = ('类型不对，应为字符串，可选值：' + (@($Item['opts']) -join ' / ')) }
        }
        $s = [string]$Value
        foreach ($o in @($Item['opts'])) {
            if ([string]$o -eq $s) { return @{ ok = $true; value = $s; error = '' } }
        }
        return @{ ok = $false; value = $null; error = ('取值不在允许范围内，可选值：' + (@($Item['opts']) -join ' / ')) }
    }

    # ---- list ----
    if ($type -eq 'list') {
        $arr = $null
        if ($Value -is [string]) { $arr = @([string]$Value) }
        elseif ($Value -is [System.Array]) { $arr = @($Value) }
        else { return @{ ok = $false; value = $null; error = '类型不对，应为字符串数组' } }

        $out = New-Object System.Collections.ArrayList
        foreach ($x in $arr) {
            if ($null -eq $x) { continue }
            if ($x -is [bool] -or $x -is [System.Array]) {
                return @{ ok = $false; value = $null; error = '数组元素必须是字符串' }
            }
            $s = ([string]$x).Trim()
            if ($s.Length -eq 0) { continue }
            [void]$out.Add($s)
        }
        return @{ ok = $true; value = @($out.ToArray()); error = '' }
    }

    # ---- int / double ----
    if ($type -eq 'int' -or $type -eq 'double') {
        if (-not (Test-CfgNumber $Value)) {
            if ($type -eq 'int') { return @{ ok = $false; value = $null; error = '类型不对，应为整数' } }
            return @{ ok = $false; value = $null; error = '类型不对，应为数字' }
        }
        $d   = [double]$Value
        $min = $Item['min']
        $max = $Item['max']
        $rangeText = ''
        if ($null -ne $min -and $null -ne $max) { $rangeText = ("应在 {0} ~ {1} 之间" -f $min, $max) }
        elseif ($null -ne $min) { $rangeText = ("不能小于 {0}" -f $min) }
        elseif ($null -ne $max) { $rangeText = ("不能大于 {0}" -f $max) }

        if ($type -eq 'int') {
            if ($d -ne [math]::Floor($d)) {
                return @{ ok = $false; value = $null; error = "类型不对，应为整数（$rangeText）" }
            }
            if ($null -ne $min -and $d -lt [double]$min) { return @{ ok = $false; value = $null; error = "数值超出范围，$rangeText" } }
            if ($null -ne $max -and $d -gt [double]$max) { return @{ ok = $false; value = $null; error = "数值超出范围，$rangeText" } }
            if ($d -gt 2147483647 -or $d -lt -2147483648) { return @{ ok = $false; value = $null; error = '数值超出范围' } }
            return @{ ok = $true; value = [int]$d; error = '' }
        }

        if ($null -ne $min -and $d -lt [double]$min) { return @{ ok = $false; value = $null; error = "数值超出范围，$rangeText" } }
        if ($null -ne $max -and $d -gt [double]$max) { return @{ ok = $false; value = $null; error = "数值超出范围，$rangeText" } }
        return @{ ok = $true; value = [double]$d; error = '' }
    }

    return $bad
}

# 统一拼返回 JSON（保证数组不会退化成标量）
function New-CfgResultJson {
    param([bool]$Ok, $Applied, $Errors, $NeedRestart)
    try {
        $obj = [ordered]@{
            ok          = $Ok
            applied     = @($Applied)
            errors      = @($Errors)
            needRestart = @($NeedRestart)
        }
        return (ConvertTo-Json -InputObject $obj -Depth 6 -Compress)
    } catch {
        return '{"ok":false,"applied":[],"errors":["返回结果序列化失败"],"needRestart":[]}'
    }
}

# 把 $script:Settings 整体写回配置文件（UTF-8 带 BOM）
# 只序列化 $script:Settings 本身，所以不在 schema 里的键（例如 MaxLogFileMB）也会原样保留
function Save-CfgSettingsFile {
    param([string]$Path)

    $target = $Path
    if ($null -eq $target -or ([string]$target).Trim().Length -eq 0) { $target = [string]$script:ConfigPath }
    if ($null -eq $target -or $target.Trim().Length -eq 0) { throw '没有可用的配置文件路径' }

    $dir = [System.IO.Path]::GetDirectoryName($target)
    if (-not [string]::IsNullOrEmpty($dir) -and -not [System.IO.Directory]::Exists($dir)) {
        [void][System.IO.Directory]::CreateDirectory($dir)
    }

    $json = ConvertTo-Json -InputObject $script:Settings -Depth 6
    $enc  = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($target, $json, $enc)
}

# ============================================================================
#  对外接口 1：配置 JSON（带元数据）
# ============================================================================
function Get-WebSettingsJson {
    try {
        $groupNames = New-Object System.Collections.ArrayList
        $groupItems = @{}

        foreach ($it in @($script:SettingsSchema)) {
            $g = [string]$it['group']
            if ($g.Trim().Length -eq 0) { $g = '其他' }
            if (-not $groupItems.ContainsKey($g)) {
                [void]$groupNames.Add($g)
                $groupItems[$g] = New-Object System.Collections.ArrayList
            }

            $type = [string]$it['t']
            $min  = $null
            $max  = $null
            $opts = $null
            if ($type -eq 'int' -or $type -eq 'double') {
                $min = $it['min']
                $max = $it['max']
            } elseif ($type -eq 'enum') {
                $opts = @($it['opts'])
            }

            $entry = [ordered]@{
                k       = [string]$it['k']
                t       = $type
                label   = [string]$it['label']
                hint    = [string]$it['hint']
                unit    = [string]$it['unit']
                v       = (Get-CfgCurrentValue -Item $it)
                min     = $min
                max     = $max
                opts    = $opts
                restart = [bool]$it['restart']
            }
            [void]$groupItems[$g].Add($entry)
        }

        # 按固定顺序输出分组，schema 里多出来的分组排在后面
        $ordered = New-Object System.Collections.ArrayList
        foreach ($g in @($script:SettingsGroupOrder)) {
            if ($groupItems.ContainsKey($g)) { [void]$ordered.Add($g) }
        }
        foreach ($g in @($groupNames.ToArray())) {
            if (-not $ordered.Contains($g)) { [void]$ordered.Add($g) }
        }

        $groups = New-Object System.Collections.ArrayList
        foreach ($g in @($ordered.ToArray())) {
            $go = [ordered]@{
                name  = $g
                items = @($groupItems[$g].ToArray())
            }
            [void]$groups.Add($go)
        }

        $outRoot = [ordered]@{
            ok     = $true
            groups = @($groups.ToArray())
        }
        return (ConvertTo-Json -InputObject $outRoot -Depth 6 -Compress)
    } catch {
        return (ConvertTo-Json -InputObject ([ordered]@{ ok = $false; groups = @(); error = [string]$_.Exception.Message }) -Depth 6 -Compress)
    }
}

# ============================================================================
#  对外接口 2：应用网页提交的修改
# ============================================================================
function Apply-WebSettings {
    param([string]$Body)

    $applied     = New-Object System.Collections.ArrayList
    $errors      = New-Object System.Collections.ArrayList
    $needRestart = New-Object System.Collections.ArrayList

    try {
        if ($null -eq $Body -or ([string]$Body).Trim().Length -eq 0) {
            [void]$errors.Add('请求内容为空')
            return (New-CfgResultJson -Ok $false -Applied $applied -Errors $errors -NeedRestart $needRestart)
        }

        $parsed = $null
        try {
            $parsed = ConvertFrom-Json -InputObject $Body
        } catch {
            [void]$errors.Add('请求不是合法的 JSON：' + [string]$_.Exception.Message)
            return (New-CfgResultJson -Ok $false -Applied $applied -Errors $errors -NeedRestart $needRestart)
        }

        if ($null -eq $parsed -or $parsed -is [string] -or $parsed -is [System.Array] -or
            $parsed -is [int] -or $parsed -is [long] -or $parsed -is [double] -or $parsed -is [bool]) {
            [void]$errors.Add('请求格式不对：需要一个 JSON 对象，例如 {"PollMs":500}')
            return (New-CfgResultJson -Ok $false -Applied $applied -Errors $errors -NeedRestart $needRestart)
        }

        $props = @($parsed.PSObject.Properties)
        if ($props.Count -eq 0) {
            [void]$errors.Add('请求里没有任何配置项')
            return (New-CfgResultJson -Ok $false -Applied $applied -Errors $errors -NeedRestart $needRestart)
        }

        foreach ($p in $props) {
            $key = [string]$p.Name
            $val = $p.Value

            # 键必须存在于 $script:Settings
            $exists = $false
            try {
                if ($null -ne $script:Settings) { $exists = [bool]$script:Settings.Contains($key) }
            } catch { $exists = $false }
            if (-not $exists) {
                [void]$errors.Add(("{0}: 未知配置项" -f $key))
                continue
            }

            # 必须能在 schema 里找到才能校验类型
            $item = Get-CfgSchemaItem -Key $key
            if ($null -eq $item) {
                [void]$errors.Add(("{0}: 该配置项不支持在线修改" -f $key))
                continue
            }

            $res = Convert-CfgIncomingValue -Item $item -Value $val
            if (-not $res.ok) {
                [void]$errors.Add(("{0}: {1}" -f $key, [string]$res.error))
                continue
            }

            # 校验通过就写入（写同值也算成功，这样网页整表提交时不会误报失败）
            $oldSig  = Get-CfgValueSignature -Value (Get-CfgCurrentValue -Item $item)
            $newSig  = Get-CfgValueSignature -Value $res.value
            $changed = ($oldSig -ne $newSig)

            $script:Settings[$key] = $res.value
            [void]$applied.Add($key)
            # 只有真的变了、而且这一项要求重启，才提示需要重启
            if ($changed -and [bool]$item['restart']) { [void]$needRestart.Add($key) }
        }

        # 有成功项才写盘；一个都没成功就不动文件
        if ($applied.Count -gt 0) {
            try {
                Save-CfgSettingsFile -Path ([string]$script:ConfigPath)
            } catch {
                [void]$errors.Add('配置已生效，但写入配置文件失败：' + [string]$_.Exception.Message)
            }
        }

        $ok = ($applied.Count -gt 0)
        return (New-CfgResultJson -Ok $ok -Applied $applied -Errors $errors -NeedRestart $needRestart)
    } catch {
        [void]$errors.Add('处理失败：' + [string]$_.Exception.Message)
        return (New-CfgResultJson -Ok $false -Applied $applied -Errors $errors -NeedRestart $needRestart)
    }
}

# ============================================================================
#  对外接口 3：设备忽略 / 重点关注规则
# ============================================================================
function Add-DeviceRule {
    param([string]$Kind, [string]$Pattern, [switch]$Remove)

    try {
        $key = ''
        $kindText = ''
        if ($null -ne $Kind) {
            $k = ([string]$Kind).Trim().ToLower()
            if ($k -eq 'ignore') { $key = 'IgnorePatterns'; $kindText = '忽略' }
            elseif ($k -eq 'focus') { $key = 'FocusPatterns'; $kindText = '重点关注' }
        }
        if ($key.Length -eq 0) {
            return (ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = 'Kind 只能是 ignore 或 focus' }) -Depth 6 -Compress)
        }

        if ($null -eq $Pattern -or ([string]$Pattern).Trim().Length -eq 0) {
            return (ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = '规则内容不能为空' }) -Depth 6 -Compress)
        }
        $pat = ([string]$Pattern).Trim()

        if ($null -eq $script:Settings -or -not $script:Settings.Contains($key)) {
            return (ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = ('配置里没有 {0}' -f $key) }) -Depth 6 -Compress)
        }

        # 读出现有规则（统一成字符串数组）
        $list = New-Object System.Collections.ArrayList
        foreach ($x in @($script:Settings[$key])) {
            if ($null -eq $x) { continue }
            if ($x -is [bool] -or $x -is [System.Array]) { continue }
            $s = ([string]$x).Trim()
            if ($s.Length -eq 0) { continue }
            [void]$list.Add($s)
        }

        if ($Remove) {
            $kept = New-Object System.Collections.ArrayList
            $hit  = $false
            foreach ($x in @($list.ToArray())) {
                if ([string]$x -eq $pat) { $hit = $true; continue }
                [void]$kept.Add([string]$x)
            }
            if (-not $hit) {
                return (ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = ('{0} 规则里没有「{1}」' -f $kindText, $pat) }) -Depth 6 -Compress)
            }
            $script:Settings[$key] = @($kept.ToArray())
        } else {
            $dup = $false
            foreach ($x in @($list.ToArray())) {
                if ([string]$x -eq $pat) { $dup = $true; break }
            }
            if (-not $dup) { [void]$list.Add($pat) }
            $script:Settings[$key] = @($list.ToArray())
        }

        try {
            Save-CfgSettingsFile -Path ([string]$script:ConfigPath)
        } catch {
            return (ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = ('写入配置文件失败：{0}' -f [string]$_.Exception.Message) }) -Depth 6 -Compress)
        }

        $out = [ordered]@{
            ok       = $true
            patterns = @($script:Settings[$key])
        }
        return (ConvertTo-Json -InputObject $out -Depth 6 -Compress)
    } catch {
        return (ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = [string]$_.Exception.Message }) -Depth 6 -Compress)
    }
}

# ============================================================================
#  硬件采集模块（已内联，原 _build\hw.ps1）
# ============================================================================
# =====================================================================
#  hw.ps1 —— 独立硬件采集模块
#  目标环境：Windows 11 + Windows PowerShell 5.1（Desktop 版），无管理员权限
#
#  对外接口（主程序只调用这两个，签名固定）：
#     Initialize-Hardware   只调用一次：缓存静态信息 + 建立常驻计数器/基准点
#     Update-Hardware       主循环里按间隔反复调用：刷新 $script:Hw
#
#  结果对象：$script:Hw，字段名固定（见 Initialize-Hardware 内的骨架定义）
#  主程序负责调用节奏（$script:Settings.HwSampleMs），本模块自己不管间隔。
#
#  设计要点：
#   * 每个采集项各自 try/catch，任何一项失败都不影响其它项，函数永不抛异常
#   * 全程无任何控制台输出（不 Write-Host / 不 Write-Output）
#   * 只用 P/Invoke + CIM + PerformanceCounter(PhysicalDisk) + nvidia-smi
#     不使用 Processor 类别的 PerformanceCounter（首次初始化要 4.7 秒）
#   * PerformanceCounter 对象常驻复用（只在 Initialize 时 new 一次）
# =====================================================================

# ---------------------------------------------------------------------
# 0. 内部状态容器
# ---------------------------------------------------------------------
$script:Hw       = $null    # 对外结果对象（初始化前为 $null）
$script:HwState  = @{}      # 内部：静态缓存 / CPU 基准 / 网卡基准 / 计时器
$script:HwPerf   = $null    # 内部：常驻 PerformanceCounter 对象

# ---------------------------------------------------------------------
# 1. P/Invoke 定义（重复点源加载时不会重复定义）
#    - GetSystemTimes        CPU 累计时间（100ns 单位）
#    - GlobalMemoryStatusEx  物理内存
#    - GetSystemPowerStatus  电源/电池
# ---------------------------------------------------------------------
try {
    if (-not ('HwNativeApi' -as [type])) {
        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public class HwNativeApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
'@
    }
} catch {
    # P/Invoke 不可用：依赖它的项目（CPU/内存/电池）会被静默跳过
}

# ---------------------------------------------------------------------
# 2. 内部小工具函数
# ---------------------------------------------------------------------

# FILETIME(低/高 32 位) -> 64 位累计计数
function Get-HwFileTimeValue($ft) {
    try {
        return (([uint64]$ft.dwHighDateTime * 4294967296) + [uint64]$ft.dwLowDateTime)
    } catch {
        return [uint64]0
    }
}

# nvidia-smi 的 csv 字段 -> double；"N/A"、"[N/A]"、空 一律返回 $null
function ConvertTo-HwDouble($text) {
    if ($null -eq $text) { return $null }
    try {
        $s = ([string]$text).Trim()
        if ($s -eq '') { return $null }
        if ($s -match 'N/A') { return $null }
        $d = [double]0
        if ([double]::TryParse($s, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) {
            return $d
        }
    } catch { }
    return $null
}

# 调用 nvidia-smi 取一行 CSV；失败返回 $null
# 关键：ProcessStartInfo + CreateNoWindow + RedirectStandardOutput + UseShellExecute=$false
#       否则会闪黑框
# ---------------------------------------------------------------------------
#  NVMe 硬盘温度 / 寿命
#  致态这类盘不通过标准 Windows API 暴露 SMART（Get-StorageReliabilityCounter
#  返回空对象，WMI 的 MSStorageDriver_* 直接"不支持"）。但可以直接读物理磁盘的
#  NVMe SMART 日志：用 access=0 打开 \\.\PhysicalDriveN（注意 GENERIC_READ 才
#  需要管理员权限，access=0 普通用户就能打开），再发 IOCTL_STORAGE_QUERY_PROPERTY
#  取 Log Page 02h（SMART/Health Information）。
# ---------------------------------------------------------------------------
if (-not ('HwNvmeSmart' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public class HwNvmeSmart {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr templ);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint ret, IntPtr overlapped);

    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    const int SPEC_LEN = 40;
    const int DATA_LEN = 512;

    // 返回 "温度C|已用寿命%|可用备用%|通电小时"；失败返回空串
    public static string Read(int idx) {
        SafeFileHandle h = CreateFileW(@"\\.\PhysicalDrive" + idx, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return ""; }
        try {
            int total = 8 + SPEC_LEN + DATA_LEN;
            byte[] buf = new byte[total];
            BitConverter.GetBytes((uint)49).CopyTo(buf, 0);
            BitConverter.GetBytes((uint)0).CopyTo(buf, 4);
            int o = 8;
            BitConverter.GetBytes((uint)3).CopyTo(buf, o + 0);
            BitConverter.GetBytes((uint)2).CopyTo(buf, o + 4);
            BitConverter.GetBytes((uint)2).CopyTo(buf, o + 8);
            BitConverter.GetBytes((uint)0).CopyTo(buf, o + 12);
            BitConverter.GetBytes((uint)SPEC_LEN).CopyTo(buf, o + 16);
            BitConverter.GetBytes((uint)DATA_LEN).CopyTo(buf, o + 20);

            uint ret = 0;
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)total, buf, (uint)total, out ret, IntPtr.Zero)) return "";
            int d = 8 + (int)BitConverter.ToUInt32(buf, o + 16);
            if (d + 144 > total) return "";
            // NVMe SMART/Health Log 布局：
            //   0=关键警告  1-2=温度(K)  3=可用备用%  4=备用阈值%  5=已用寿命%  128-143=通电小时
            int tempK = BitConverter.ToUInt16(buf, d + 1);
            if (tempK < 200 || tempK > 400) return "";
            double c = tempK - 273.15;
            int spare = buf[d + 3];
            int used  = buf[d + 5];
            ulong hours = 0;
            for (int k = 7; k >= 0; k--) { hours = (hours << 8) | buf[d + 128 + k]; }
            // 偏移 200 起是 Temperature Sensor 1..8。S1 和复合温度基本一样，
            // S2 是闪存(NAND)温度 —— 就是 AIDA64 每块盘显示的第二个值。
            int s2 = 0;
            int o2 = d + 202;
            if (o2 + 1 < total) { int kk = BitConverter.ToUInt16(buf, o2); if (kk > 200 && kk < 400) s2 = (int)Math.Round(kk - 273.15); }
            return c.ToString("F1") + "|" + used + "|" + spare + "|" + hours + "|" + s2;
        } catch { return ""; } finally { h.Dispose(); }
    }
}
"@
}

function Get-HwNvmeSmart([int]$Idx) {
    try {
        if (-not ('HwNvmeSmart' -as [type])) { return $null }
        $raw = [HwNvmeSmart]::Read($Idx)
        if (-not $raw) { return $null }
        $p = $raw -split '\|'
        if ($p.Count -lt 4) { return $null }
        return @{ tempC = [double]$p[0]; used = [int]$p[1]; spare = [int]$p[2]; hours = [long]$p[3]; temp2 = [int]$p[4] }
    } catch { return $null }
}
function Start-HwGpuProc {
    # 真正启动 nvidia-smi（不等待）
    $st = $script:HwState
    if (-not $st.Nvidia) { return $false }
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName               = $st.Nvidia
        $psi.Arguments              = '--query-gpu=name,temperature.gpu,utilization.gpu,utilization.memory,memory.used,memory.total,power.draw,power.limit,clocks.sm,clocks.mem,fan.speed,pstate --format=csv,noheader,nounits'
        $psi.UseShellExecute        = $false
        $psi.CreateNoWindow         = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError  = $true
        $st.GpuProc  = [System.Diagnostics.Process]::Start($psi)
        $st.GpuStart = Get-Date
        return $true
    } catch {
        $st.GpuProc = $null
        return $false
    }
}

function Invoke-HwNvidiaSmi {
    # 异步取 GPU 数据。nvidia-smi 每次要 60~90ms，同步等会卡住主循环，
    # 而主循环同时负责泵托盘菜单消息和网页 HTTP 请求 —— 那正是托盘卡顿的根源。
    # 做法：上一轮启动的进程若已结束就收结果，随即再起一个新的，本次直接返回上一次的结果。
    param([switch]$Block)   # -Block 只在初始化时用一次，保证首帧就有 GPU 型号

    $st = $script:HwState
    if (-not $st.Nvidia) { return $null }

    if ($Block) {
        try {
            if ($null -eq $st.GpuProc) { [void](Start-HwGpuProc) }
            $out = ''
            if ($st.GpuProc) {
                [void]$st.GpuProc.WaitForExit(3000)
                try { $out = $st.GpuProc.StandardOutput.ReadToEnd() } catch { }
                try { [void]$st.GpuProc.StandardError.ReadToEnd() } catch { }
                try { $st.GpuProc.Dispose() } catch { }
                $st.GpuProc = $null
            }
            $sv = ([string]$out).Trim()
            if ($sv -ne '') { $st.GpuLast = $sv }
        } catch { }
        return $st.GpuLast
    }

    try {
        if ($null -ne $st.GpuProc) {
            $done = $true
            try { $done = $st.GpuProc.HasExited } catch { $done = $true }
            if ($done) {
                $out = ''
                try { $out = $st.GpuProc.StandardOutput.ReadToEnd() } catch { }
                try { [void]$st.GpuProc.StandardError.ReadToEnd() } catch { }
                try { $st.GpuProc.Dispose() } catch { }
                $st.GpuProc = $null
                $sv = ([string]$out).Trim()
                if ($sv -ne '') { $st.GpuLast = $sv }
            } elseif ($st.GpuStart -and ((Get-Date) - $st.GpuStart).TotalSeconds -gt 6) {
                try { $st.GpuProc.Kill(); $st.GpuProc.Dispose() } catch { }
                $st.GpuProc = $null
            }
        }
        if ($null -eq $st.GpuProc) { [void](Start-HwGpuProc) }
    } catch {
        try { if ($st.GpuProc) { $st.GpuProc.Dispose() } } catch { }
        $st.GpuProc = $null
    }
    return $st.GpuLast
}

# 重建"Up 且非 Loopback/Tunnel"的网卡列表，并记录当前累计字节作为基准
function Update-HwNicList {
    try {
        $list = @()
        $prev = @{}
        foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            try {
                if ($ni.OperationalStatus -ne [System.Net.NetworkInformation.OperationalStatus]::Up) { continue }
                $t = $ni.NetworkInterfaceType
                if ($t -eq [System.Net.NetworkInformation.NetworkInterfaceType]::Loopback) { continue }
                if ($t -eq [System.Net.NetworkInformation.NetworkInterfaceType]::Tunnel) { continue }
                $st = $ni.GetIPv4Statistics()
                if ($null -eq $st) { continue }
                $list += $ni
                $prev[[string]$ni.Id] = @{ Rx = [double]$st.BytesReceived; Tx = [double]$st.BytesSent }
            } catch { }
        }
        $script:HwState.Nics = $list
        $script:HwState.NicPrev = $prev
    } catch { }
}

# =====================================================================
#  Initialize-Hardware —— 只调用一次
# =====================================================================
function Initialize-Hardware {

    # ---- 结果对象骨架：字段名一个字都不能差 ----
    $script:Hw = [pscustomobject]@{
        CpuPct        = 0.0
        CpuUserPct    = 0.0
        CpuKernelPct  = 0.0
        CpuName       = ''
        CpuCores      = 0
        CpuThreads    = 0
        CpuTempC      = $null
        CpuMaxMHz     = 0
        CpuCurMHz     = 0
        CpuL2MB       = 0
        CpuL3MB       = 0
        MemTotalGB    = 0.0
        MemUsedGB     = 0.0
        MemPct        = 0.0
        MemFreeGB     = 0.0
        MemCommitGB   = 0.0
        MemCommitMaxGB= 0.0
        GpuName       = $null
        GpuTempC      = $null
        GpuUtilPct    = $null
        GpuMemPct     = $null
        GpuMemUsedMB  = $null
        GpuMemTotalMB = $null
        GpuPowerW     = $null
        GpuPowerMaxW  = $null
        GpuClockMHz   = $null
        GpuMemClockMHz= $null
        GpuFanPct     = $null
        GpuPstate     = $null
        DiskReadKB    = 0.0
        DiskWriteKB   = 0.0
        NetRxKB       = 0.0
        NetTxKB       = 0.0
        Disks         = @()
        DriveSmart    = @()   # 每块物理盘的 NVMe SMART：温度 / 寿命 / 通电小时
        DiskIo        = @()   # 每块物理盘各自的读写速率
        MemModules    = @()   # 内存条：型号 / 容量 / 频率 / 插槽
        BatPct        = $null
        OnAC          = $null
        Uptime        = ''
        LastUpdate    = ''
    }

    # ---- 内部状态 ----
    $script:HwState = @{
        Boot     = $null      # 开机时间（DateTime）
        CpuIdle  = $null      # CPU 空闲累计（基准）
        CpuTotal = $null      # CPU 总累计（kernel+user，基准）
        CpuUser  = $null      # CPU 用户态累计（基准）
        Nvidia   = $null      # nvidia-smi 全路径
        GpuOk    = $false     # 是否有可用的 N 卡 + nvidia-smi
        GpuProc  = $null      # 正在跑的 nvidia-smi 进程（异步，避免阻塞主循环）
        GpuStart = $null      # 上面这个进程的启动时刻
        GpuLast  = $null      # 最近一次成功取到的 nvidia-smi 输出
        Drives   = @()        # 固定磁盘盘符，如 @('C:','D:','E:')
        DiskMap  = @{}        # 盘符 -> @{pnum=物理磁盘号; phys=物理磁盘型号}
        SmartAt  = $null      # 上次读 NVMe SMART 的时刻（温度变化慢，不必每次采样都读）
        TzSearcher    = $null   # CPU 温度用的常驻 WMI 查询器
        UseWmiCpuFreq = $false   # 性能计数器不可用（非英文系统）时改走 WMI
        UseWmiDiskIo  = $false   # 同上，磁盘读写速率
        Nics     = @()        # 缓存的 Up 网卡对象
        NicPrev  = @{}        # 网卡 Id -> @{Rx=;Tx=} 上次累计字节
        NetStamp = $null      # 上次网络采样时刻（秒）
        Sw       = $null      # 计时用 Stopwatch
    }
    # ---- 盘符 -> 物理磁盘 映射 ----
    # 这台机器是 2 块 NVMe 分了 3 个区，必须标清每个区属于哪块盘，
    # 否则会误以为"有 3 块硬盘"。走 CIM（Get-Partition|Get-Disk 要 2 秒）。
    $script:HwState.DiskMap = @{}
    try {
        $diskByDev = @{}
        foreach ($dd in (Get-CimInstance Win32_DiskDrive -Property Index, Model, DeviceID -ErrorAction Stop)) {
            $diskByDev[[string]$dd.DeviceID] = $dd
        }
        $partToDisk = @{}
        foreach ($r in (Get-CimInstance Win32_DiskDriveToDiskPartition -ErrorAction Stop)) {
            $did  = [regex]::Match([string]$r.Antecedent, 'DeviceID\s*=\s*"([^"]+)"').Groups[1].Value
            $ptid = [regex]::Match([string]$r.Dependent,  'DeviceID\s*=\s*"([^"]+)"').Groups[1].Value
            if ($ptid -and $diskByDev.ContainsKey($did)) { $partToDisk[$ptid] = $diskByDev[$did] }
        }
        foreach ($r in (Get-CimInstance Win32_LogicalDiskToPartition -ErrorAction Stop)) {
            $lid  = [regex]::Match([string]$r.Dependent,  'DeviceID\s*=\s*"([^"]+)"').Groups[1].Value
            $ptid = [regex]::Match([string]$r.Antecedent, 'DeviceID\s*=\s*"([^"]+)"').Groups[1].Value
            if ($lid -and $partToDisk.ContainsKey($ptid)) {
                $dd = $partToDisk[$ptid]
                $script:HwState.DiskMap[$lid] = @{ pnum = [int]$dd.Index; phys = [string]$dd.Model }
            }
        }
    } catch { }

    # 重复 Initialize 时先释放上一轮的常驻计数器，避免句柄堆积
    try {
        if ($null -ne $script:HwPerf) {
            foreach ($cntKey in @('Read', 'Write', 'CpuPerf')) {
                try { if ($script:HwPerf[$cntKey]) { $script:HwPerf[$cntKey].Dispose() } } catch { }
            }
        }
    } catch { }
    try {
        if ($null -ne $script:HwPerf -and $script:HwPerf.DiskIo) {
            foreach ($k2 in @($script:HwPerf.DiskIo.Keys)) {
                try { $script:HwPerf.DiskIo[$k2].Read.Dispose() }  catch { }
                try { $script:HwPerf.DiskIo[$k2].Write.Dispose() } catch { }
            }
        }
    } catch { }
    $script:HwPerf = @{ Read = $null; Write = $null; CpuPerf = $null; DiskIo = @{} }

    try { $script:HwState.Sw = [System.Diagnostics.Stopwatch]::StartNew() } catch { }

    $hasNative = $false
    try { if ('HwNativeApi' -as [type]) { $hasNative = $true } } catch { $hasNative = $false }

    # ---- 2.1 内存总量（顺带先填一次内存） ----
    try {
        if ($hasNative) {
            $m = New-Object 'HwNativeApi+MEMORYSTATUSEX'
            $m.dwLength = 64    # sizeof(MEMORYSTATUSEX)，避免依赖 Marshal.SizeOf
            if ([HwNativeApi]::GlobalMemoryStatusEx([ref]$m)) {
                $totGB = [double]$m.ullTotalPhys / 1073741824
                if ($totGB -gt 0) {
                    $script:Hw.MemTotalGB = [math]::Round($totGB, 1)
                    $availGB = [double]$m.ullAvailPhys / 1073741824
                    $usedGB = $totGB - $availGB
                    $script:Hw.MemUsedGB = [math]::Round($usedGB, 1)
                    $script:Hw.MemFreeGB  = [math]::Round($availGB, 1)
                    $script:Hw.MemPct = [math]::Round(($usedGB / $totGB) * 100, 1)
                    $tp = [double]$m.ullTotalPageFile / 1073741824
                    $ap = [double]$m.ullAvailPageFile / 1073741824
                    if ($tp -gt 0) {
                        $script:Hw.MemCommitMaxGB = [math]::Round($tp, 1)
                        $script:Hw.MemCommitGB    = [math]::Round($tp - $ap, 1)
                    }
                }
            }
        }
    } catch { }

    # ---- 2.2 CPU 时间基准点 ----
    try {
        if ($hasNative) {
            $i = New-Object 'HwNativeApi+FILETIME'
            $k = New-Object 'HwNativeApi+FILETIME'
            $u = New-Object 'HwNativeApi+FILETIME'
            if ([HwNativeApi]::GetSystemTimes([ref]$i, [ref]$k, [ref]$u)) {
                $script:HwState.CpuIdle  = Get-HwFileTimeValue $i
                $script:HwState.CpuTotal = (Get-HwFileTimeValue $k) + (Get-HwFileTimeValue $u)
            }
        }
    } catch { }

    # ---- 2.3 CPU 型号 / 核心数 / 线程数 ----
    try {
        # 必须用 -Property 只投影这 3 个属性：整类查询会触发 WMI 计算 LoadPercentage，
        # 本机实测每次固定 ~1130ms；投影后 ~10ms，取到的值完全一致
        $cpu = Get-CimInstance -ClassName Win32_Processor -Property Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize -ErrorAction Stop | Select-Object -First 1
        if ($null -ne $cpu) {
            if ($cpu.Name) { $script:Hw.CpuName = ([string]$cpu.Name).Trim() }
            if ($cpu.NumberOfCores) { $script:Hw.CpuCores = [int]$cpu.NumberOfCores }
            if ($cpu.NumberOfLogicalProcessors) { $script:Hw.CpuThreads = [int]$cpu.NumberOfLogicalProcessors }
            if ($cpu.MaxClockSpeed)     { $script:Hw.CpuMaxMHz = [int]$cpu.MaxClockSpeed }
            if ($cpu.CurrentClockSpeed) { $script:Hw.CpuCurMHz = [int]$cpu.CurrentClockSpeed }
            if ($cpu.L2CacheSize)       { $script:Hw.CpuL2MB   = [int]([math]::Round($cpu.L2CacheSize / 1024)) }   # WMI 单位是 KB
            if ($cpu.L3CacheSize)       { $script:Hw.CpuL3MB   = [int]([math]::Round($cpu.L3CacheSize / 1024)) }   # WMI 单位是 KB
            
            # ---- 内存条明细（型号/频率/通道；温度拿不到，见 README 说明）----
            try {
                $mods = @()
                foreach ($mm in (Get-CimInstance Win32_PhysicalMemory -Property PartNumber, Manufacturer, Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, BankLabel, SMBIOSMemoryType -ErrorAction Stop)) {
                    $mods += [pscustomobject]@{
                        part  = ([string]$mm.PartNumber).Trim()
                        mfg   = ([string]$mm.Manufacturer).Trim()
                        sizeGB= [math]::Round([double]$mm.Capacity / 1073741824, 0)
                        speed = [int]$mm.ConfiguredClockSpeed     # 实际运行频率
            rated = [int]$mm.Speed                    # 标称频率
            ddr   = [int]$mm.SMBIOSMemoryType
                        slot  = ([string]$mm.DeviceLocator).Trim()
                    }
                }
                if ($mods.Count -gt 0) { $script:Hw.MemModules = @($mods) }
            } catch { }
        }
    } catch { }

    # ---- 2.4 nvidia-smi 路径（两个位置都找）+ GPU 型号 ----
    try {
        $cands = @()
        if ($env:SystemRoot) { $cands += (Join-Path $env:SystemRoot 'System32\nvidia-smi.exe') }
        if ($env:ProgramFiles) { $cands += (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe') }
        if (${env:ProgramFiles(x86)}) { $cands += (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVSMI\nvidia-smi.exe') }
        foreach ($c in $cands) {
            try {
                if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) {
                    $script:HwState.Nvidia = $c
                    break
                }
            } catch { }
        }
        if ($script:HwState.Nvidia) {
            $line = Invoke-HwNvidiaSmi -Block
            if ($line) {
                $fields = @($line -split "`r?`n" | Where-Object { $_ -and $_.Trim() -ne '' })
                if ($fields.Count -ge 1) {
                    $parts = @($fields[0] -split ',')
                    if ($parts.Count -ge 1) {
                        $nm = ([string]$parts[0]).Trim()
                        if ($nm -ne '') {
                            $script:Hw.GpuName = $nm
                            $script:HwState.GpuOk = $true
                        }
                    }
                }
            }
        }
    } catch { }

    # ---- 2.5 磁盘盘符列表（只取一次；DriveType=3 是本地固定磁盘） ----
    try {
        $dl = @()
        $lds = Get-CimInstance -ClassName Win32_LogicalDisk -Property DeviceID -Filter 'DriveType=3' -ErrorAction Stop
        foreach ($d in $lds) {
            try { if ($d.DeviceID) { $dl += [string]$d.DeviceID } } catch { }
        }
        if ($dl.Count -gt 0) { $script:HwState.Drives = $dl }
    } catch { }
    if ($script:HwState.Drives.Count -eq 0) {
        # CIM 失败时的退化方案：至少保住 C:
        try {
            foreach ($di in [System.IO.DriveInfo]::GetDrives()) {
                try {
                    if ($di.DriveType -eq [System.IO.DriveType]::Fixed -and $di.IsReady) { $dl += [string]$di.Name }
                } catch { }
            }
            if ($dl.Count -gt 0) { $script:HwState.Drives = $dl }
        } catch { }
    }

    # ---- 2.6 开机时间 ----
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -Property LastBootUpTime -ErrorAction Stop
        if ($null -ne $os -and $os.LastBootUpTime) { $script:HwState.Boot = [datetime]$os.LastBootUpTime }
    } catch { }

    # ---- 2.7 常驻性能计数器 ----
    # 注意：PerformanceCounter 用的是**英文类别名**，而类别名在非英文 Windows 上是本地化的
    # （德语 Prozessorinformationen、日语 プロセッサ情報），英文名会直接抛异常。
    # 所以每一项单独判断，建不起来就退回 WMI（WMI 的性能类名永远是英文）。
    try {
        $rd = New-Object System.Diagnostics.PerformanceCounter -ArgumentList 'PhysicalDisk', 'Disk Read Bytes/sec', '_Total'
        $null = $rd.NextValue()   # 第一次必为 0，作为基准
        $script:HwPerf.Read = $rd
    } catch { $script:HwPerf.Read = $null }

    # CPU 实时频率：用「% Processor Performance」× 基频算出来。
    # 免管理员；Win32_Processor.CurrentClockSpeed 在 AMD 上永远返回基频，没用。
    try {
        $cp = New-Object System.Diagnostics.PerformanceCounter('Processor Information', '% Processor Performance', '_Total', $true)
        [void]$cp.NextValue()
        $script:HwPerf.CpuPerf = $cp
    } catch { $script:HwPerf.CpuPerf = $null }
    try {
        $wr = New-Object System.Diagnostics.PerformanceCounter -ArgumentList 'PhysicalDisk', 'Disk Write Bytes/sec', '_Total'
        $null = $wr.NextValue()
        $script:HwPerf.Write = $wr
    } catch { $script:HwPerf.Write = $null }

    # 每块物理磁盘各自的读写速率。Windows 的 PhysicalDisk 计数器实例名形如
    # "0 C: D:" / "1 E:" —— 开头就是物理磁盘号，正好对得上 NVMe SMART 的 pnum。
    try {
        $cat = New-Object System.Diagnostics.PerformanceCounterCategory('PhysicalDisk')
        foreach ($ins in $cat.GetInstanceNames()) {
            if ($ins -eq '_Total') { continue }
            $mm = [regex]::Match($ins, '^(\d+)')
            if (-not $mm.Success) { continue }
            $pn = [int]$mm.Groups[1].Value
            $drd = New-Object System.Diagnostics.PerformanceCounter('PhysicalDisk', 'Disk Read Bytes/sec',  $ins, $true)
            $dwr = New-Object System.Diagnostics.PerformanceCounter('PhysicalDisk', 'Disk Write Bytes/sec', $ins, $true)
            [void]$drd.NextValue(); [void]$dwr.NextValue()
            $script:HwPerf.DiskIo[$pn] = @{ Read = $drd; Write = $dwr }
        }
    } catch { $script:HwPerf.DiskIo = @{} }

    if ($null -eq $script:HwPerf.CpuPerf) {
        $script:HwState.UseWmiCpuFreq = $true
        Write-Log '性能计数器「% Processor Performance」不可用（多半是非英文系统），CPU 实时频率改用 WMI 读取'
    }
    if ($null -eq $script:HwPerf.Read -or $null -eq $script:HwPerf.Write -or $script:HwPerf.DiskIo.Count -eq 0) {
        $script:HwState.UseWmiDiskIo = $true
        Write-Log '性能计数器「PhysicalDisk」不可用（多半是非英文系统），磁盘读写速率改用 WMI 读取'
    }

    # ---- 2.7b CPU 温度用的 WMI Searcher（建一次反复用，比 Get-CimInstance 快 28 倍） ----
    # 有的机器没有这个类，建不起来就退回 Get-CimInstance。
    try {
        $se = New-Object System.Management.ManagementObjectSearcher(
            'root\CIMV2',
            'SELECT HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation')
        [void]$se.Get()
        $script:HwState.TzSearcher = $se
    } catch { $script:HwState.TzSearcher = $null }

    # ---- 2.8 网卡列表 + 累计字节基准 ----
    Update-HwNicList
    try {
        if ($script:HwState.Sw) { $script:HwState.NetStamp = $script:HwState.Sw.Elapsed.TotalSeconds }
    } catch { }

    # ---- 2.9 先跑一次采样，让 $script:Hw 立刻可用 ----
    #      （此后 CpuPct 的基准已是这里的时间点，CPU% 从"上一次采样"开始算）
    try { Update-Hardware } catch { }
}

# =====================================================================
#  Update-Hardware —— 主循环里反复调用
# =====================================================================
function Update-HwCpuFreqFromWmi {
    # 非英文 Windows 上 PerformanceCounter 的类别名是本地化的，英文名会抛异常，
    # 这时改用 WMI 后备 —— WMI 的性能类名永远是英文，不受系统语言影响。
    try {
        $r = @(Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ProcessorInformation -ErrorAction Stop |
               Where-Object { $_.Name -eq '_Total' })[0]
        if ($null -ne $r -and $script:Hw.CpuMaxMHz -gt 0) {
            $pp = [double]$r.PercentProcessorPerformance
            if ($pp -gt 0) { $script:Hw.CpuCurMHz = [int][math]::Round($script:Hw.CpuMaxMHz * $pp / 100) }
        }
    } catch { }
}

function Update-HwDiskIoFromWmi {
    # 同上。这个类一次给出整机和每块物理磁盘的速率，两个需求一起解决。
    try {
        $rows = @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfDisk_PhysicalDisk -ErrorAction Stop)
        $dio = @()
        $tr = -1.0; $tw = -1.0
        foreach ($r in $rows) {
            $rk = [double]$r.DiskReadBytesPersec  / 1KB
            $wk = [double]$r.DiskWriteBytesPersec / 1KB
            if ($rk -lt 0) { $rk = 0 }
            if ($wk -lt 0) { $wk = 0 }
            $nm = [string]$r.Name
            if ($nm -eq '_Total') { $tr = $rk; $tw = $wk; continue }
            $mm = [regex]::Match($nm, '^(\d+)')
            if (-not $mm.Success) { continue }
            $dio += [pscustomobject]@{ pnum = [int]$mm.Groups[1].Value; readKB = [math]::Round($rk, 1); writeKB = [math]::Round($wk, 1) }
        }
        if ($tr -ge 0) { $script:Hw.DiskReadKB  = [math]::Round($tr, 1) }
        if ($tw -ge 0) { $script:Hw.DiskWriteKB = [math]::Round($tw, 1) }
        if ($dio.Count -gt 0) { $script:Hw.DiskIo = @($dio) }
    } catch { }
}

function Update-Hardware {

    if ($null -eq $script:Hw) { return }   # 未初始化：静默返回，不抛异常
    $script:Phase = '采样/CPU占用'
# ---------------- CPU 占用（GetSystemTimes 两次差值） ----------------
    try {
        if ('HwNativeApi' -as [type]) {
            $i = New-Object 'HwNativeApi+FILETIME'
            $k = New-Object 'HwNativeApi+FILETIME'
            $u = New-Object 'HwNativeApi+FILETIME'
            if ([HwNativeApi]::GetSystemTimes([ref]$i, [ref]$k, [ref]$u)) {
                $idle  = Get-HwFileTimeValue $i
                $kernelRaw = Get-HwFileTimeValue $k
                $userRaw   = Get-HwFileTimeValue $u
                $total     = $kernelRaw + $userRaw
                if ($null -ne $script:HwState.CpuIdle -and $null -ne $script:HwState.CpuTotal -and $null -ne $script:HwState.CpuUser) {
                    $didle  = [double]$idle - [double]$script:HwState.CpuIdle
                    $dtotal = [double]$total - [double]$script:HwState.CpuTotal
                    if ($dtotal -gt 0) {
                        # kernel 时间里含 idle，所以 busy = total - idle
                        $pct = (($dtotal - $didle) / $dtotal) * 100
                        if ($pct -lt 0) { $pct = 0 }
                        if ($pct -gt 100) { $pct = 100 }
                        $script:Hw.CpuPct = [math]::Round($pct, 1)
                        # 用户态 / 内核态拆分：kernel 时间里含 idle，要减掉
                        $duser = $userRaw - [double]$script:HwState.CpuUser
                        $dkern = $dtotal - $didle - $duser
                        $up = 0.0; $kp = 0.0
                        if ($dtotal -gt 0) { $up = ($duser / $dtotal) * 100; $kp = ($dkern / $dtotal) * 100 }
                        if ($up -lt 0) { $up = 0 }; if ($up -gt 100) { $up = 100 }
                        if ($kp -lt 0) { $kp = 0 }; if ($kp -gt 100) { $kp = 100 }
                        $script:Hw.CpuUserPct   = [math]::Round($up, 1)
                        $script:Hw.CpuKernelPct = [math]::Round($kp, 1)
                    }
                }
                $script:HwState.CpuIdle  = $idle
                $script:HwState.CpuTotal = $total
                $script:HwState.CpuUser  = $userRaw
            }
        }
    } catch { }
    $script:Phase = '采样/内存'
# ---------------- 内存（GlobalMemoryStatusEx） ----------------
    try {
        if ('HwNativeApi' -as [type]) {
            $m = New-Object 'HwNativeApi+MEMORYSTATUSEX'
            $m.dwLength = 64
            if ([HwNativeApi]::GlobalMemoryStatusEx([ref]$m)) {
                $totGB = [double]$m.ullTotalPhys / 1073741824
                if ($totGB -gt 0) {
                    $availGB = [double]$m.ullAvailPhys / 1073741824
                    $usedGB = $totGB - $availGB
                    $script:Hw.MemTotalGB = [math]::Round($totGB, 1)
                    $script:Hw.MemUsedGB  = [math]::Round($usedGB, 1)
                    $script:Hw.MemPct     = [math]::Round(($usedGB / $totGB) * 100, 1)
                }
            }
        }
    } catch { }
    $script:Phase = '采样/CPU温度'
# ---------------- CPU 温度（ThermalZoneInformation，取所有热区最大值） ----------------
    # 注意：无 -Namespace，就是默认 root/cimv2；某些机器没有这个类，必须容错
    try {
        $tempx = $null
        # 复用预建的 Searcher（0.7ms）而不是每次 Get-CimInstance（20ms）——
        # 这段每 2 秒跑一次，差值就是每秒约 10ms 的 CPU。
        $zones = $null
        if ($script:HwState.TzSearcher) {
            try { $zones = $script:HwState.TzSearcher.Get() } catch { $script:HwState.TzSearcher = $null }
        }
        if ($null -eq $zones) {
            $zones = Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction Stop
        }
        foreach ($z in $zones) {
            try {
                $raw = $z.HighPrecisionTemperature
                if ($null -eq $raw) { continue }
                $v = [double]$raw
                if ($v -le 0) { continue }
                if ($null -eq $tempx -or $v -gt $tempx) { $tempx = $v }
            } catch { }
        }
        if ($null -ne $tempx) {
            $c = ($tempx / 10) - 273.15
            if ($c -gt -30 -and $c -lt 150) {
                $script:Hw.CpuTempC = [math]::Round($c, 1)
            } else {
                $script:Hw.CpuTempC = $null
            }
        } else {
            $script:Hw.CpuTempC = $null
        }
    } catch {
        # 类不存在 / 无权限 / 被禁用：保持 $null
        $script:Hw.CpuTempC = $null
    }
    $script:Phase = '采样/GPU'
# ---------------- GPU（nvidia-smi csv，字段可能为 N/A） ----------------
    try {
        if ($script:HwState.GpuOk -and $script:HwState.Nvidia) {
            $line = Invoke-HwNvidiaSmi -Block
            if ($line) {
                $fields = @($line -split "`r?`n" | Where-Object { $_ -and $_.Trim() -ne '' })
                if ($fields.Count -ge 1) {
                    $parts = @($fields[0] -split ',')
                    if ($parts.Count -ge 12) {
                        $nm = ([string]$parts[0]).Trim()
                        if ($nm -ne '') { $script:Hw.GpuName = $nm }
                        $script:Hw.GpuTempC      = ConvertTo-HwDouble $parts[1]
                        $script:Hw.GpuUtilPct    = ConvertTo-HwDouble $parts[2]
                        $script:Hw.GpuMemPct     = ConvertTo-HwDouble $parts[3]
                        $script:Hw.GpuMemUsedMB  = ConvertTo-HwDouble $parts[4]
                        $script:Hw.GpuMemTotalMB = ConvertTo-HwDouble $parts[5]
                        $script:Hw.GpuPowerW     = ConvertTo-HwDouble $parts[6]
                        $script:Hw.GpuPowerMaxW  = ConvertTo-HwDouble $parts[7]
                        $script:Hw.GpuClockMHz   = ConvertTo-HwDouble $parts[8]
                        $script:Hw.GpuMemClockMHz= ConvertTo-HwDouble $parts[9]
                        $script:Hw.GpuFanPct     = ConvertTo-HwDouble $parts[10]
                        $pv = ([string]$parts[11]).Trim()
                        if ($pv -eq '' -or $pv -eq '[N/A]' -or $pv -eq 'N/A') { $script:Hw.GpuPstate = $null } else { $script:Hw.GpuPstate = $pv }
                    }
                }
            }
        }
    } catch { }
    $script:Phase = '采样/CPU频率'
# ---------------- CPU 实时频率 ----------------
    if ($script:HwState.UseWmiCpuFreq) {
        Update-HwCpuFreqFromWmi
    } else {
        try {
            if ($null -ne $script:HwPerf -and $null -ne $script:HwPerf.CpuPerf -and $script:Hw.CpuMaxMHz -gt 0) {
                $pp = [double]$script:HwPerf.CpuPerf.NextValue()
                if ($pp -gt 0) { $script:Hw.CpuCurMHz = [int][math]::Round($script:Hw.CpuMaxMHz * $pp / 100) }
            }
        } catch { }
    }
    $script:Phase = '采样/分盘IO'
# ---------------- 每块物理磁盘的读写速率 ----------------
    if ($script:HwState.UseWmiDiskIo) {
        Update-HwDiskIoFromWmi
    } else {
        try {
            if ($null -ne $script:HwPerf -and $script:HwPerf.DiskIo -and $script:HwPerf.DiskIo.Count -gt 0) {
                $dio = @()
                foreach ($pn in @($script:HwPerf.DiskIo.Keys | Sort-Object)) {
                    $cc = $script:HwPerf.DiskIo[$pn]
                    $rv = 0.0; $wv = 0.0
                    try { $rv = [double]$cc.Read.NextValue()  / 1KB } catch { }
                    try { $wv = [double]$cc.Write.NextValue() / 1KB } catch { }
                    if ($rv -lt 0) { $rv = 0 }
                    if ($wv -lt 0) { $wv = 0 }
                    $dio += [pscustomobject]@{ pnum = $pn; readKB = [math]::Round($rv, 1); writeKB = [math]::Round($wv, 1) }
                }
                if ($dio.Count -gt 0) { $script:Hw.DiskIo = @($dio) }
            }
        } catch { }
    }
    $script:Phase = '采样/IO合计'
# ---------------- 磁盘读写速率合计（常驻计数器，单次 0.3~1ms） ----------------
    try {
        if ($null -ne $script:HwPerf -and $null -ne $script:HwPerf.Read) {
            $v = [double]$script:HwPerf.Read.NextValue()
            if ($v -lt 0) { $v = 0 }
            $script:Hw.DiskReadKB = [math]::Round($v / 1024, 1)
        }
    } catch { }
    try {
        if ($null -ne $script:HwPerf -and $null -ne $script:HwPerf.Write) {
            $v = [double]$script:HwPerf.Write.NextValue()
            if ($v -lt 0) { $v = 0 }
            $script:Hw.DiskWriteKB = [math]::Round($v / 1024, 1)
        }
    } catch { }
    $script:Phase = '采样/网络'
# ---------------- 网络速率（累计字节差 / 间隔秒数） ----------------
    try {
        if ($script:HwState.Nics.Count -eq 0) { Update-HwNicList }   # 初始化时没有 Up 网卡，重试

        $now = $null
        if ($script:HwState.Sw) { $now = [double]$script:HwState.Sw.Elapsed.TotalSeconds }
        $dt = 0.0
        if ($null -ne $now -and $null -ne $script:HwState.NetStamp) {
            $dt = $now - [double]$script:HwState.NetStamp
        }

        $rxSum = 0.0
        $txSum = 0.0
        $valid = $false
        foreach ($ni in $script:HwState.Nics) {
            try {
                $st = $ni.GetIPv4Statistics()
                if ($null -eq $st) { continue }
                $rx = [double]$st.BytesReceived
                $tx = [double]$st.BytesSent
                $prev = $script:HwState.NicPrev[[string]$ni.Id]
                if ($null -ne $prev -and $dt -gt 0.05) {
                    $drx = $rx - [double]$prev.Rx
                    $dtx = $tx - [double]$prev.Tx
                    if ($drx -lt 0) { $drx = 0 }   # 网卡重启会归零，按 0 处理
                    if ($dtx -lt 0) { $dtx = 0 }
                    $rxSum += $drx / $dt
                    $txSum += $dtx / $dt
                    $valid = $true
                }
                $script:HwState.NicPrev[[string]$ni.Id] = @{ Rx = $rx; Tx = $tx }
            } catch { }
        }
        if ($valid) {
            $script:Hw.NetRxKB = [math]::Round($rxSum / 1024, 1)
            $script:Hw.NetTxKB = [math]::Round($txSum / 1024, 1)
        }
        if ($null -ne $now) { $script:HwState.NetStamp = $now }
    } catch { }
    $script:Phase = '采样/磁盘空间'
# ---------------- 磁盘空间（盘符列表已在初始化缓存） ----------------
    try {
        $arr = @()
        foreach ($d in $script:HwState.Drives) {
            try {
                $di = New-Object -TypeName 'System.IO.DriveInfo' -ArgumentList $d
                $tot  = [double]$di.TotalSize
                $free = [double]$di.AvailableFreeSpace
                if ($tot -le 0) { continue }
                    $dinfo = $script:HwState.DiskMap[[string]$d]
                $arr += [pscustomobject]@{
                    m       = [string]$d
                    totalGB = [math]::Round($tot / 1073741824, 1)
                    freeGB  = [math]::Round($free / 1073741824, 1)
                    pct     = [math]::Round((($tot - $free) / $tot) * 100, 1)
                    pnum    = $(if ($dinfo) { [int]$dinfo.pnum } else { -1 })
                    phys    = $(if ($dinfo) { [string]$dinfo.phys } else { '' })
                }
            } catch { }
        }
        $script:Hw.Disks = $arr
        
        # NVMe SMART：温度 / 寿命。致态这类盘的标准 API 全是空的，只能直读 SMART 日志。
        # 温度变化慢，每 10 秒读一次就够（读一次要走一次 DeviceIoControl）。
        $needSmart = $true
        if ($script:HwState.SmartAt) {
            if (((Get-Date) - $script:HwState.SmartAt).TotalSeconds -lt 10) { $needSmart = $false }
        }
        if ($needSmart) {
            $script:HwState.SmartAt = Get-Date
            Invoke-UiPump    # NVMe SMART 要发 DeviceIoControl，前后各泵一次让托盘不冻
            $sm = @()
            foreach ($pn in @($arr | ForEach-Object { $_.pnum } | Sort-Object -Unique)) {
                if ($null -eq $pn -or $pn -lt 0) { continue }
                $info = Get-HwNvmeSmart -Idx ([int]$pn)
                if ($info) {
                    $sm += [pscustomobject]@{ pnum = [int]$pn; tempC = $info.tempC; used = $info.used; spare = $info.spare; hours = $info.hours; temp2 = $info.temp2 }
                }
            }
            if ($sm.Count -gt 0) { $script:Hw.DriveSmart = @($sm) }
            Invoke-UiPump
        }
    } catch { }
    $script:Phase = '采样/电池'
# ---------------- 电池 / 电源 ----------------
    # AC: 1=接电源 0=用电池 255=未知；BatteryLifePercent: 255=未知
    # BatteryFlag: 128=无系统电池 255=未知 -> 视为"无电池"，两个字段都为 $null
    try {
        if ('HwNativeApi' -as [type]) {
            $pw = New-Object 'HwNativeApi+SYSTEM_POWER_STATUS'
            if ([HwNativeApi]::GetSystemPowerStatus([ref]$pw)) {
                $ac   = [int]$pw.ACLineStatus
                $flag = [int]$pw.BatteryFlag
                $pct  = [int]$pw.BatteryLifePercent
                $noBattery = ($flag -eq 128 -or $flag -eq 255 -or ($ac -eq 255 -and $pct -eq 255))
                if ($noBattery) {
                    $script:Hw.BatPct = $null
                    $script:Hw.OnAC   = $null
                } else {
                    if ($pct -eq 255) { $script:Hw.BatPct = $null } else { $script:Hw.BatPct = $pct }
                    if ($ac -eq 1) { $script:Hw.OnAC = $true }
                    elseif ($ac -eq 0) { $script:Hw.OnAC = $false }
                    else { $script:Hw.OnAC = $null }
                }
            }
        }
    } catch { }
    $script:Phase = '采样/开机时长'
# ---------------- 开机时长 ----------------
    try {
        if ($null -ne $script:HwState.Boot) {
            $ts = (Get-Date) - $script:HwState.Boot
            if ($ts.TotalSeconds -lt 0) { $ts = [timespan]::Zero }
            $days = [int][math]::Floor($ts.TotalDays)
            $script:Hw.Uptime = ('{0} 天 {1:00}:{2:00}' -f $days, $ts.Hours, $ts.Minutes)
        }
    } catch { }

    # ---------------- 采样时间戳 ----------------
    try { $script:Hw.LastUpdate = (Get-Date).ToString('HH:mm:ss') } catch { }
}


# ============================================================================
#  设备清单
#  原来是一个独立的 设备清单.ps1，现已并入主程序 —— 命令行用 -Inventory，
#  网页上点「工具」卡片里的按钮。
# ============================================================================
function Invoke-DeviceInventory {
    param(
        [switch]$Open,    # 生成后用记事本打开
        [switch]$Quiet    # 不往控制台刷（网页调用时用）
    )
    # 网页调用时把 Write-Host 静音（函数里定义同名函数就能遮蔽 cmdlet）
    if ($Quiet) { function Write-Host { param([Parameter(ValueFromRemainingArguments=$true)]$Rest) } }
    $OutFile = Join-Path $script:Root '设备清单.txt'
# ---------------------------------------------------------------- 原生接口
if (-not ('DevInventory' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class DevInventory {
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(IntPtr c, string e, IntPtr h, uint f);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInfo(IntPtr s, uint i, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiOpenDeviceInfo(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstanceIdW(IntPtr s, ref SP_DEVINFO_DATA d, StringBuilder i, uint n, out uint r);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr s, ref SP_DEVINFO_DATA d, uint p, out uint t, byte[] b, uint n, out uint r);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDevicePropertyW")]
    static extern bool GetPropW(IntPtr s, ref SP_DEVINFO_DATA d, ref DEVPROPKEY k, out uint t, byte[] b, uint n, out uint r, uint f);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint st, out uint pr, uint d, uint f);

    static Guid F = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");   // DEVPKEY_Device_*
    static Guid B = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2");   // DEVPKEY_Device_BusReportedDeviceDesc

    static string Reg(IntPtr s, ref SP_DEVINFO_DATA d, uint p) {
        uint t, n; byte[] b = new byte[2048];
        if (SetupDiGetDeviceRegistryPropertyW(s, ref d, p, out t, b, (uint)b.Length, out n) && n > 2)
            return Encoding.Unicode.GetString(b, 0, (int)n - 2);
        return "";
    }
    static string Prop(IntPtr s, ref SP_DEVINFO_DATA d, Guid g, uint id) {
        DEVPROPKEY k = new DEVPROPKEY(); k.fmtid = g; k.pid = id;
        uint t, n; byte[] b = new byte[4096];
        try {
            if (GetPropW(s, ref d, ref k, out t, b, (uint)b.Length, out n, 0) && n > 2 && t == 18)
                return Encoding.Unicode.GetString(b, 0, (int)n - 2);
        } catch { }
        return "";
    }

    // 返回每项: [0]实例ID [1]友好名 [2]描述 [3]类别 [4]枚举器 [5]厂商
    //           [6]设备自报型号 [7]驱动服务 [8]位置 [9]故障码
    public static List<string[]> Scan() {
        var res = new List<string[]>();
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x02 | 0x04);  // PRESENT | ALLCLASSES
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return res;
        try {
            SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
            d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++) {
                StringBuilder sb = new StringBuilder(1024); uint need;
                SetupDiGetDeviceInstanceIdW(set, ref d, sb, (uint)sb.Capacity, out need);
                uint st = 0, pr = 0;
                CM_Get_DevNode_Status(out st, out pr, d.DevInst, 0);
                res.Add(new string[] {
                    sb.ToString(),
                    Reg(set, ref d, 0x0C),  // SPDRP_FRIENDLYNAME
                    Reg(set, ref d, 0x00),  // SPDRP_DEVICEDESC
                    Reg(set, ref d, 0x07),  // SPDRP_CLASS
                    Reg(set, ref d, 0x16),  // SPDRP_ENUMERATOR_NAME
                    Reg(set, ref d, 0x0B),  // SPDRP_MFG
                    Prop(set, ref d, B, 4), // BusReportedDeviceDesc
                    Reg(set, ref d, 0x04),  // SPDRP_SERVICE
                    Prop(set, ref d, F, 15),// LocationInfo
                    pr.ToString()
                });
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return res;
    }
}
'@
}

# USB 厂商号对照（只收录本机出现的）
$VendorMap = @{
    '04A5' = '明基 BenQ / Acer'; '05E3' = 'Genesys Logic'; '8087' = 'Intel'; '1BCF' = 'SunplusIT'
    '048D' = 'ITE Tech';         '046D' = '罗技 Logitech';  '0781' = 'SanDisk'; '0951' = '金士顿 Kingston'
    '0BC2' = '希捷 Seagate';     '1058' = '西部数据 WD';    '090C' = 'Silicon Motion'; '13FE' = '群联 Phison'
    '174C' = 'ASMedia';          '152D' = 'JMicron';        '0BDA' = '瑞昱 Realtek'; '0CF3' = '高通 Atheros'
    '8086' = 'Intel';            '04F2' = '群光 Chicony';   '5986' = 'Bison';   '0C45' = '松翰 Sonix'
    '0461' = 'Primax';           '045E' = '微软 Microsoft'; '0458' = 'KYE / Genius'; '093A' = 'PixArt'
    '1B1C' = '海盗船 Corsair';   '1532' = '雷蛇 Razer';     '258A' = 'SINO WEALTH'; '0483' = 'ST'
    '0480' = '东芝 Toshiba';     '04E8' = '三星 Samsung';   '0B05' = '华硕 ASUS'; '1D6B' = '根集线器'
    '1A40' = 'Terminus 集线器';  '2109' = 'VIA Labs 集线器'; '214B' = '集线器';  '0438' = 'AMD'
    '1022' = 'AMD';              '05AC' = 'Apple';          '413C' = 'Dell';    '03F0' = 'HP'
    '04CA' = 'Lite-On';          '8564' = '创见 Transcend'; '058F' = 'Alcor';   '1908' = '创见'
    '3142' = 'Fifine / 音频设备'
}

function Get-Vid([string]$Id) {
    $m = [regex]::Match($Id, 'VID_([0-9A-Fa-f]{4})')
    if ($m.Success) { return $m.Groups[1].Value.ToUpper() }
    return ''
}

Write-Host '正在读取设备信息...' -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$raw = [DevInventory]::Scan()
$sw.Stop()

# Windows 的兜底通用名，信息量太低，要让位给更具体的名字
$GenericName = '^(USB 输入设备|USB 复合设备|USB Composite Device|符合 HID 标准的.*|HID-compliant .*|网络控制器|以太网控制器|视频控制器.*|多媒体控制器|SM 总线控制器|PCI 主桥|通用串行总线.*控制器|蓝牙外围设备|未知设备|Generic .*|Standard .*|.*控制器|卷|磁盘驱动器|基本系统设备)$'

function Get-BestName([string[]]$Cands, [string]$Fallback) {
    # 优先取"不通用且最长"的名字：USB 设备自报型号通常最准，
    # 但 PCI 设备自报的往往是"以太网控制器"这种兜底名，这时该用驱动的型号名。
    $good = @()
    foreach ($c in $Cands) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        if ($c -match $GenericName) { continue }
        $good += $c
    }
    if ($good.Count -gt 0) { return ($good | Sort-Object Length -Descending | Select-Object -First 1) }
    foreach ($c in $Cands) { if (-not [string]::IsNullOrWhiteSpace($c)) { return $c } }
    return $Fallback
}

$rows = foreach ($x in $raw) {
    $best = Get-BestName -Cands @($x[1], $x[6], $x[2]) -Fallback (($x[0] -split '\\')[-1])
    [pscustomobject]@{
        Id = $x[0]; Name = $best; Friendly = $x[1]; Desc = $x[2]; Class = $x[3]
        Enum = $x[4]; Mfg = $x[5]; Reported = $x[6]; Service = $x[7]
        Loc = $x[8]; Problem = [int]$x[9]; Vid = (Get-Vid $x[0])
    }
}

$sb = New-Object System.Text.StringBuilder
function W([string]$s) { [void]$sb.AppendLine($s) }

W "设备清单  ——  生成于 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W "在线设备：$($rows.Count) 个      能读到设备自报型号的：$((@($rows | Where-Object { $_.Reported })).Count) 个"
W ("=" * 100)
W ""
W "说明：名称列优先使用【设备自己上报的型号】，读不到时才退回系统通用名。"
W "      「USB 输入设备」「符合 HID 标准的供应商定义设备」这类是 Windows 的通用叫法，"
W "      它们其实是同一个物理设备的不同接口节点。"
W ""
W "【一】USB 设备（含集线器）—— 按 VID/PID 分组"
W ("-" * 100)
foreach ($g in ($rows | Where-Object { $_.Enum -eq 'USB' } | Group-Object { ($_.Id -split '\\')[1] } | Sort-Object Name)) {
    $vid = Get-Vid $g.Group[0].Id
    $vendor = if ($VendorMap.ContainsKey($vid)) { $VendorMap[$vid] } else { '未收录厂商' }
    W ""
    W ("  {0}    [{1}]    {2} 个节点" -f $g.Name, $vendor, $g.Count)
    foreach ($x in $g.Group) {
        $state = if ($x.Problem -ne 0) { "  <故障码 $($x.Problem)>" } else { '' }
        W ("      - {0,-48} {1,-12} {2}{3}" -f $x.Name, $x.Class, $x.Loc, $state)
    }
}

$cats = @(
    @{ T = '【二】蓝牙设备';   F = { $_.Class -eq 'Bluetooth' } },
    @{ T = '【三】网络适配器'; F = { $_.Class -eq 'Net' } },
    @{ T = '【四】输入设备';   F = { @('Mouse', 'Keyboard', 'HIDClass') -contains $_.Class } },
    @{ T = '【五】存储与磁盘'; F = { @('DiskDrive', 'USBSTOR', 'SCSIAdapter', 'HDC', 'Volume', 'CDROM') -contains $_.Class } },
    @{ T = '【六】显示与显卡'; F = { @('Display', 'Monitor') -contains $_.Class } },
    @{ T = '【七】音频设备';   F = { @('MEDIA', 'AudioEndpoint') -contains $_.Class } },
    @{ T = '【八】摄像头/图像'; F = { @('Image', 'Camera') -contains $_.Class } }
)
$used = @('USB', 'Bluetooth', 'Net', 'Mouse', 'Keyboard', 'HIDClass', 'DiskDrive', 'USBSTOR',
    'SCSIAdapter', 'HDC', 'Volume', 'CDROM', 'Display', 'Monitor', 'MEDIA', 'AudioEndpoint', 'Image', 'Camera')

foreach ($c in $cats) {
    $items = @($rows | Where-Object $c.F)
    W ""
    W ("{0}  （{1} 个）" -f $c.T, $items.Count)
    W ("-" * 100)
    foreach ($x in ($items | Sort-Object Class, Name)) {
        $flag = if ($x.Problem -ne 0) { "  <故障码 $($x.Problem)>" } else { '' }
        W ("  {0,-48} {1,-14} {2}{3}" -f $x.Name, $x.Class, $x.Loc, $flag)
    }
}

$others = @($rows | Where-Object { $used -notcontains $_.Class })
W ""
W ("【九】其它设备（系统/固件/处理器等）  （{0} 个，只列代表性的）" -f $others.Count)
W ("-" * 100)
foreach ($x in (@($others | Where-Object { $_.Reported -or $_.Problem -ne 0 }) | Sort-Object Class, Name)) {
    $flag = if ($x.Problem -ne 0) { "  <故障码 $($x.Problem)>" } else { '' }
    W ("  {0,-48} {1,-14} {2}{3}" -f $x.Name, $x.Class, $x.Loc, $flag)
}

W ""
W ("=" * 100)
W "【全部设备的实例ID】排查问题报给技术支持时用得上"
W ("-" * 100)
foreach ($x in ($rows | Sort-Object Class, Name)) {
    $nm = [string]$x.Name
    if ($nm.Length -gt 42) { $nm = $nm.Substring(0, 42) }
    W ("  {0,-44} {1}" -f $nm, $x.Id)
}

[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))

Write-Host ''
Write-Host ("设备清单已生成：{0}" -f $OutFile) -ForegroundColor Green
Write-Host ("共 {0} 个在线设备，读取耗时 {1} 毫秒" -f $rows.Count, $sw.ElapsedMilliseconds) -ForegroundColor Gray

# 控制台摘要
Write-Host ''
Write-Host '── USB 设备 ──' -ForegroundColor Cyan
$rows | Where-Object { $_.Enum -eq 'USB' } | Group-Object { ($_.Id -split '\\')[1] } | Sort-Object Name | ForEach-Object {
    $v = Get-Vid $_.Group[0].Id
    $vn = if ($VendorMap.ContainsKey($v)) { $VendorMap[$v] } else { '未收录' }
    $nm = ($_.Group | Where-Object { $_.Reported } | Select-Object -First 1).Reported
    if (-not $nm) { $nm = ($_.Group | Select-Object -First 1).Name }
    Write-Host ("  {0,-24} {1,-18} {2}" -f $_.Name, $vn, $nm)
}
Write-Host ''
Write-Host '── 有故障码的设备 ──' -ForegroundColor Yellow
$bad = @($rows | Where-Object { $_.Problem -ne 0 })
if ($bad.Count -eq 0) { Write-Host '  无' -ForegroundColor Green }
else { $bad | ForEach-Object { Write-Host ("  {0,-40} 故障码 {1}" -f $_.Name, $_.Problem) -ForegroundColor Yellow } }


    if ($Open) { try { Start-Process notepad.exe -ArgumentList "`"$OutFile`"" } catch { } }
    return [pscustomobject]@{
        ok    = $true
        file  = $OutFile
        count = $rows.Count
        ms    = [int]$sw.ElapsedMilliseconds
        text  = $sb.ToString()
    }
}

# ============================================================================
#  入口分支
# ============================================================================
if ($Inventory) {
    # 生成设备清单后退出。网页上点按钮走的是 /api/inventory，不经过这里。
    try {
        $r = Invoke-DeviceInventory -Open:$OpenInventory
        Write-Host ''
        Write-Host ("设备清单已生成：{0}" -f $r.file) -ForegroundColor Green
        Write-Host ("共 {0} 个在线设备，读取耗时 {1} 毫秒" -f $r.count, $r.ms) -ForegroundColor Gray
    } catch {
        Write-Host ('生成失败：{0}' -f $_.Exception.Message) -ForegroundColor Red
    }
    exit 0
}

if ($StopMonitor) {
    $selfPath = $MyInvocation.MyCommand.Path
    $targets = @()
    try {
        $targets = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue |
            Where-Object {
                $_.CommandLine -and
                $_.CommandLine -like "*-File*$selfPath*" -and
                $_.CommandLine -notlike '*-StopMonitor*' -and
                $_.ProcessId -ne $PID
            })
    } catch { }

    # 提要：如果监控是以管理员身份跑着的，普通权限用 WMI 查不到它的命令行，
    # 所以先把"停止标志"文件写好 —— 监控每轮循环都会检查这个文件，看到就优雅退出。
    try { [System.IO.File]::WriteAllText($script:StopFlag, 'stop') } catch { }

    if ($targets.Count -eq 0) {
        # 查不到不等于没在跑：等一会儿看标志文件有没有被消费掉
        for ($w = 0; $w -lt 40; $w++) {
            Start-Sleep -Milliseconds 450
            if (-not (Test-Path -LiteralPath $script:StopFlag)) { break }
            try {
                $n2 = @(Get-Process powershell -ErrorAction SilentlyContinue).Count
            } catch { }
        }
        if (-not (Test-Path -LiteralPath $script:StopFlag)) {
            Write-Host '监控已正常停止（它是以管理员身份运行的，统计已保存）。' -ForegroundColor Green
            exit 0
        }
        # 还没停 —— 多半是提权运行的进程（普通权限杀不掉，WMI 也看不到它的命令行）。
        # 写个临时脚本，用管理员权限再杀一次。
        Write-Host '监控没有响应停止请求，正在强制结束...' -ForegroundColor Yellow
        try {
            # 按 PID 精确结束。绝不能用命令行模糊匹配 —— 那会把任何命令行里
            # 恰好含 "DeviceWatch.ps1" 的进程都杀掉（包括用户自己的脚本）。
            $pids = New-Object System.Collections.ArrayList
            try {
                if (Test-Path -LiteralPath $script:PidFile) {
                    $v = 0
                    [void][int]::TryParse(((Get-Content -LiteralPath $script:PidFile -Raw) -replace '\s',''), [ref]$v)
                    if ($v -gt 0) { [void]$pids.Add($v) }
                }
            } catch { }
            if ($pids.Count -eq 0) {
                # 老版本没写 PID 文件：按"命令行以本脚本路径结尾"精确匹配
                $pat = '-File\s+"?' + [regex]::Escape($PSCommandPath) + '"?\s*$'
                foreach ($pr in @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue)) {
                    if ($pr.ProcessId -eq $PID) { continue }
                    if ($pr.CommandLine -and $pr.CommandLine -match $pat) { [void]$pids.Add([int]$pr.ProcessId) }
                }
            }

            $killed = $false
            foreach ($tp in $pids) {
                try { Stop-Process -Id $tp -Force -ErrorAction Stop; $killed = $true } catch { }
            }
            if (-not $killed -and $pids.Count -gt 0) {
                $killer = Join-Path $script:LogDir '_kill-tmp.ps1'
                $killCode = (($pids | ForEach-Object { 'Stop-Process -Id ' + $_ + ' -Force -ErrorAction SilentlyContinue' }) -join "`r`n")
                [System.IO.File]::WriteAllText($killer, $killCode, (New-Object System.Text.UTF8Encoding($true)))
                Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $killer + '"'))
                Start-Sleep -Seconds 5
                Remove-Item -LiteralPath $killer -Force -ErrorAction SilentlyContinue
            }
            Remove-Item -LiteralPath $script:PidFile -Force -ErrorAction SilentlyContinue
            if (@(Get-NetTCPConnection -LocalPort ([int]$script:Settings.WebPort) -State Listen -ErrorAction SilentlyContinue).Count -eq 0) {
                Write-Host '监控已强制停止。' -ForegroundColor Green
            } else {
                Write-Host '监控仍在运行，请到托盘图标右键退出，或重启电脑。' -ForegroundColor Red
            }
        } catch {
            Write-Host ('强制结束失败：{0}' -f $_.Exception.Message) -ForegroundColor Red
        }
        Remove-Item -LiteralPath $script:StopFlag -Force -ErrorAction SilentlyContinue
        exit 0
    }
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $alive = @($targets | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
        if ($alive.Count -eq 0) { break }
    }

    $alive = @($targets | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    if ($alive.Count -gt 0) {
        $alive | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Write-Host "已强制停止 $($alive.Count) 个监控进程（未能优雅退出）。" -ForegroundColor Yellow
    } else {
        Write-Host '监控已正常停止，统计已保存到 logs\summary.txt。' -ForegroundColor Green
    }
    Remove-Item -LiteralPath $script:StopFlag -Force -ErrorAction SilentlyContinue
    exit 0
}

if ($OpenWeb) {
    $wport = [int]$script:Settings.WebPort
    $wurl = "http://127.0.0.1:$wport/"
    $isUp = $false
    try { $c = New-Object System.Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $wport); $isUp = $true; $c.Close() } catch { }
    if (-not $isUp) {
        $lnch = Get-LauncherPath
        if ($lnch) {
            Write-Host '网页面板还没启动，正在启动监控…' -ForegroundColor Yellow
            try {
                if ($lnch -like '*.exe') { Start-Process -FilePath $lnch }
                else { Start-Process -FilePath "$env:SystemRoot\System32\wscript.exe" -ArgumentList "`"$lnch`"" }
            } catch { }
            for ($i = 0; $i -lt 25; $i++) {
                Start-Sleep -Milliseconds 600
                try { $c2 = New-Object System.Net.Sockets.TcpClient; $c2.Connect('127.0.0.1', $wport); $isUp = $true; $c2.Close(); break } catch { }
            }
        }
    }
    if ($isUp) {
        Write-Host ("已在浏览器打开：{0}" -f $wurl) -ForegroundColor Green
        Start-Process $wurl
    } else {
        Write-Host '网页面板没起来。请先双击「设备连接监控.exe」，再执行 设备连接监控.exe web。' -ForegroundColor Red
    }
    exit 0
}

if ($InstallStartup)   { exit ([int](-not (Install-Startup))) }
if ($UninstallStartup) { exit ([int](-not (Uninstall-Startup))) }

if ($TestNotify) {
    Initialize-Notifier
    Show-Alert -Title '设备连接监控 - 测试通知' `
        -Body ("如果你看到这条消息，说明弹窗通知可用。`n时间：{0}" -f (Get-Date -Format 'HH:mm:ss')) `
        -Kind 'Warn' -Force
    Start-Sleep -Seconds 2
    Write-Host '已发送测试通知。' -ForegroundColor Green
    exit 0
}

# 单实例保护
$mutex = New-Object System.Threading.Mutex($false, 'Local\DeviceWatch_SingleInstance')
if (-not $mutex.WaitOne(0, $false)) {
    if ($Console) { Write-Host '设备监控已经在运行了。' -ForegroundColor Yellow }
    exit 0
}

Initialize-Notifier   # 必须先初始化通知通道，否则所有告警都会静默失败
if ($script:Settings.ShowTray -and -not $NoTray) { Initialize-Tray }
if ($script:Settings.WebEnabled -and -not $NoWeb) { Initialize-WebServer | Out-Null }
try { $script:AutoStartOn = Get-AutostartState } catch { }
$script:AutoStartChecked = Get-Date
try { Initialize-Hardware } catch { Write-Log ("硬件采集初始化失败：{0}" -f $_.Exception.Message) -Kind 'Warn' }

# 低层传感器库（LHM）不在这里加载 —— 它的 Open() 要 5~6 秒。
# 放在启动路径上会出现「网页端口已经监听、但迟迟不响应」的白等：
# 实测用户从双击到看见面板要 8.5 秒，其中 5.7 秒全耗在这一步。
# 现在交给主循环惰性加载（见下面「硬件监测」分节的延迟加载逻辑），
# 面板约 4 秒就能用，硬件数据随后自动补上。

# ============================================================================
#  主流程
# ============================================================================
$script:StartTime = Get-Date
Remove-Item -LiteralPath $script:StopFlag -Force -ErrorAction SilentlyContinue
Write-Log ('=' * 70)
Write-Log ("设备连接监控 v{0} 启动" -f $script:Version)
Write-Log ("脚本目录：{0}" -f $script:Root)

# 基线：首次完整扫描，只建立基线不报警
try {
    $detail = [DevNative]::Details()
    $realProb = New-Object System.Collections.ArrayList
    $benign = New-Object System.Collections.ArrayList
    foreach ($row in $detail) {
        $id = $row[0]; $name = (Get-BestDeviceName -Friendly $row[1] -Reported $row[6]); $cls = $row[2]; $enm = $row[3]; $prob = [int]$row[4]
        if (Test-Ignored -Id $id -Name $name -Class $cls) { continue }
        Register-KnownDevice -Id $id -Name $name -Class $cls -Problem $prob -Enumerator $enm
        if ($prob -ne 0) {
            $item = [pscustomobject]@{ name = $name; cls = $cls; id = $id; code = $prob; text = (Get-ProblemText $prob) }
            if (Test-BenignProblem $prob) { [void]$benign.Add($item) } else { [void]$realProb.Add($item) }
        }
    }
    $script:ProblemList = @($realProb)
    $script:BenignList = @($benign)
    $script:ProblemCount = $realProb.Count
    Write-Log ("基线建立完成：当前在线设备 {0} 个" -f $script:Known.Count)
    if ($script:ProblemCount -gt 0) {
        $pn = (@($script:ProblemList | Select-Object -First 3 | ForEach-Object { $_.name }) -join '、')
        Write-Log ("⚠ 发现 {0} 个异常设备：{1}" -f $script:ProblemCount, $pn) -Kind 'Warn'
    } else {
        Write-Log '设备状态检查：没有发现异常设备'
    }
    foreach ($b in $script:BenignList) {
        Write-Log ("已禁用（正常，不报警）：{0}  [{1}]" -f $b.name, $b.text)
    }
} catch {
    Write-Log ("基线扫描失败：{0}" -f $_.Exception.Message) -Kind 'Error'
}

$netSnap = Get-NetworkSnapshot
Write-Log ("网络适配器：{0}" -f (($netSnap.Values | ForEach-Object { "$($_.Name)=$($_.State)" }) -join ', '))

Invoke-Cleanup
$script:LastEvtTime = (Get-Date).AddSeconds(-2)

# WMI 事件订阅（用于立即触发一次差异比对，失败也不影响轮询）
$null = Register-CimIndicationEvent -Query "SELECT * FROM Win32_DeviceChangeEvent" `
    -SourceIdentifier 'DevWatch_Change' -ErrorAction SilentlyContinue

Write-Log '监控已启动，发现设备接入/断开会立即通知。'
try { [System.IO.File]::WriteAllText($script:PidFile, [string]$PID) } catch { }

# 启动阶段的耗时（LHM 加载 2.4 秒 + 基线扫描 + 硬件初始化）不算"运行期卡顿"，
# 这里把看门狗基线清零，否则启动后第一次汇报会把这几秒报成阻塞，误导排查。
$script:MaxBlockMs     = 0.0
$script:MaxBlockWhere  = ''
$script:LastPumpAt     = $null
$script:LastBlockReport = $null

$lastIds = @{}
foreach ($i in [DevNative]::PresentIds()) { $lastIds[$i] = $true }
$lastNet = $netSnap
$lastFullScan = Get-Date
$lastNetCheck = Get-Date
$lastEvtCheck = Get-Date
$lastTrayUpdate = [datetime]::MinValue
$absentSince = @{}

while ($script:Running) {
    # --- 让托盘菜单和网页保持响应 ---
    Invoke-UiPump

    # --- 响应外部停止请求（优雅退出，保留统计）---
    if (Test-Path -LiteralPath $script:StopFlag) {
        Remove-Item -LiteralPath $script:StopFlag -Force -ErrorAction SilentlyContinue
        $script:Running = $false
        break
    }

    # 分片休眠：让网页面板和托盘菜单保持响应（HTTP 请求在这里被处理）
    $sliceTotal = [Math]::Max(100, [int]$script:Settings.PollMs)
    $sliceDone = 0
    # 分片粒度必须保持 50ms 或更小。
    # 托盘菜单的响应全靠这里的 DoEvents 泵消息：改成 100ms 后菜单明显发卡。
    # 而 DoEvents 单次只要 0.008ms —— 为省这点开销牺牲响应是笔亏本账。
    $sliceMs = 50
    while ($sliceDone -lt $sliceTotal -and $script:Running) {
        Invoke-UiPump
        # 合并窗口一到就马上发通知，不等下一轮循环 —— 否则会白等一整个轮询周期
        if ($script:Pending.Count -gt 0 -and ((Get-Date) - $script:PendingSince).TotalSeconds -ge [double]$script:Settings.CoalesceSec) {
            try { Invoke-PendingNotify } catch { Write-Log ("通知合并异常：{0}" -f $_.Exception.Message) -Kind 'Error' }
        }
        Start-Sleep -Milliseconds $sliceMs
        $sliceDone += $sliceMs
    }
    if (-not $script:Running) { break }
    $script:Phase = '外设-设备比对'
    if ($script:Mode -ne 'Hardware') {   # ======== 外设监测 ========
    # === 1. 设备在线列表比对（1~5 毫秒） ===
    try {
        $nowIds = @{}
        foreach ($i in [DevNative]::PresentIds()) { $nowIds[$i] = $true }

        $added = @(); $removed = @()
        foreach ($i in $nowIds.Keys) { if (-not $lastIds.ContainsKey($i)) { $added += $i } }
        foreach ($i in $lastIds.Keys) { if (-not $nowIds.ContainsKey($i)) { $removed += $i } }

        if ($added.Count -gt 0 -or $removed.Count -gt 0) {
            # 记下"轮询发现变化"的时刻，用来算端到端延迟（写进通知日志）
            $script:ChangeAt = Get-Date
            # 短暂去抖：USB 插拔会连续触发多次变化，等稳定后再取最终结果。
            # 注意：这段等待期间必须继续泵消息，否则托盘菜单会整整冻住 DebounceMs 毫秒
            # （默认 350ms，插拔时体感就是"一点就卡"）。
            $dbgLeft = [Math]::Max(50, [int]$script:Settings.DebounceMs)
            while ($dbgLeft -gt 0 -and $script:Running) {
                Invoke-UiPump
                Start-Sleep -Milliseconds 50
                $dbgLeft -= 50
            }
            $nowIds2 = @{}
            foreach ($i in [DevNative]::PresentIds()) { $nowIds2[$i] = $true }
            $added = @(); $removed = @()
            foreach ($i in $nowIds2.Keys) { if (-not $lastIds.ContainsKey($i)) { $added += $i } }
            foreach ($i in $lastIds.Keys) { if (-not $nowIds2.ContainsKey($i)) { $removed += $i } }
            $nowIds = $nowIds2

            # 取出变化设备的名称
            # 只给"新接入"的设备查名字：已经断开的设备在 Details() 里根本不存在，
            # 传 $removed 进去只会白跑一次 600ms 的全量扫描。
            $info = if ($added.Count -gt 0) { Resolve-DeviceInfo -Ids $added } else { @{} }

            $removedEvents = New-Object System.Collections.ArrayList
            $addedEvents = New-Object System.Collections.ArrayList

            foreach ($id in $removed) {
                # 注意：变量名不能叫 $known —— PowerShell 变量名不区分大小写，
                # 顶层作用域下 $known 与 $script:Known 是同一个变量，会覆盖整张设备表。
                $prevInfo = $script:Known[$id]
                $name = if ($prevInfo) { $prevInfo.Name } else { '' }
                $cls = if ($prevInfo) { $prevInfo.Class } else { '' }
                if ($info.ContainsKey($id)) { $name = $info[$id].Name; $cls = $info[$id].Class }
                if (-not $name) { $name = $id }
                if (Test-Ignored -Id $id -Name $name -Class $cls) { continue }

                $alive = ''
                if ($prevInfo -and $prevInfo.Since) {
                    $span = (Get-Date) - $prevInfo.Since
                    $alive = if ($span.TotalHours -ge 1) { "（本次已连接 {0:0.#} 小时）" -f $span.TotalHours }
                    else { "（本次已连接 {0:0} 分钟）" -f $span.TotalMinutes }
                }
                $text = "[断联] {0}  <{1}>{2}" -f $name, (Get-ShortId $id), $alive
                Write-Log $text -Kind 'Off'
                Write-CsvEvent -Kind '断联' -Id $id -Name $name -Class $cls -Extra $alive
                Add-Stat -Id $id -Name $name -Field 'Off'
                [void]$removedEvents.Add([pscustomobject]@{ Id = $id; Name = $name; Class = $cls; WasKnown = $true })
                $script:Known.Remove($id)
            }

            foreach ($id in $added) {
                $devInfo = $info[$id]
                $name = if ($devInfo) { $devInfo.Name } else { '' }
                $cls = if ($devInfo) { $devInfo.Class } else { '' }
                $prob = if ($devInfo) { $devInfo.Problem } else { 0 }
                if (-not $name) { $name = $id }
                if (Test-Ignored -Id $id -Name $name -Class $cls) { continue }

                $wasKnown = $script:Stats.ContainsKey($id)
                Register-KnownDevice -Id $id -Name $name -Class $cls -Problem $prob -Enumerator $(if ($devInfo) { $devInfo.Enumerator } else { '' })

                $kindText = if ($wasKnown) { '重新连接' } else { '接入' }
                $text = "[{0}] {1}  <{2}>" -f $kindText, $name, (Get-ShortId $id)
                Write-Log $text -Kind 'On'
                Write-CsvEvent -Kind $kindText -Id $id -Name $name -Class $cls -Extra ''
                Add-Stat -Id $id -Name $name -Field 'On'
                [void]$addedEvents.Add([pscustomobject]@{ Id = $id; Name = $name; Class = $cls; WasKnown = $wasKnown })
            }

            # 先攒起来：同一物理设备的本体和子接口可能差好几秒才枚举完，
            # 等变化停下来再统一合并通知，避免一个鼠标弹两条
            Add-PendingChange -Events @($removedEvents) -Kind 'Off'
            Add-PendingChange -Events @($addedEvents) -Kind 'On'

            $lastIds = $nowIds
            Update-Tray
        }
    } catch {
        $st = @($_.ScriptStackTrace -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 3
        Write-Log ("设备扫描异常：{0}  @@ {1}" -f $_.Exception.Message, ($st -join ' << ')) -Kind 'Error' -NoConsole:$false
    }
    $script:Phase = '外设-网卡状态'
    # === 2. 网卡状态（约 1 毫秒） ===
    if ($script:Settings.WatchNetwork -and ((Get-Date) - $lastNetCheck).TotalMilliseconds -ge [int]$script:Settings.NetCheckMs) {
        $lastNetCheck = Get-Date
        try {
            $snap = Get-NetworkSnapshot
            foreach ($k in $snap.Keys) {
                $cur = $snap[$k]; $old = $lastNet[$k]
                if (-not $old) {
                    Write-Log ("发现网络适配器：{0} ({1})" -f $cur.Name, $cur.State) -Kind 'Net'
                    continue
                }
                if ($old.State -ne $cur.State) {
                    $isUp = $cur.State -eq 'Up'
                    $msg = "{0} 变为 {1}  {2}" -f $cur.Name, $cur.State, (Format-Speed $cur.Speed)
                    Write-Log ("[网卡] " + $msg) -Kind $(if ($isUp) { 'On' } else { 'Off' })
                    Write-CsvEvent -Kind $(if ($isUp) { '网卡连接' } else { '网卡断开' }) -Id $k -Name $cur.Name -Class 'NetAdapter' -Extra $msg
                    Add-Recent -Kind $(if ($isUp) { '网卡连接' } else { '网卡断开' }) -Label $cur.Name -Id $k -Detail $msg
                    $script:EventCount++
                    $script:LastEvent = Get-Date
                    if (-not $isUp) {
                        Show-Alert -Title '⚠ 网络适配器断开' -Body ("{0}（{1}）已 {2}`n{3}" -f $cur.Name, $cur.Desc, $cur.State, (Get-Date -Format 'HH:mm:ss')) -Kind 'Off'
                    } else {
                        Show-Alert -Title '✅ 网络适配器已连接' -Body ("{0}（{1}）`n速率：{2}" -f $cur.Name, $cur.Desc, (Format-Speed $cur.Speed)) -Kind 'On'
                    }
                }
            }
            foreach ($k in $lastNet.Keys) {
                if (-not $snap.ContainsKey($k)) {
                    Write-Log ("网络适配器已移除：{0}" -f $lastNet[$k].Name) -Kind 'Off'
                }
            }
            $lastNet = $snap
            $script:WebNetSnap = $snap
            Update-Tray
        } catch { }
    }
    $script:Phase = '外设-外网探测'
    # === 3. 外网探测 ===
    if ($script:Settings.WatchInternet) {
        if (-not $script:PingTasks) {
            if ((Get-Date) -ge $script:NextPingAt) {
                $script:NextPingAt = (Get-Date).AddMilliseconds([int]$script:Settings.NetCheckMs)
                Start-PingProbe
            }
        } else {
            $allDone = $true
            foreach ($e in @($script:PingTasks)) {
                if (-not $e.Task.IsCompleted) { $allDone = $false; break }
            }
            $elapsed = ((Get-Date) - $script:PingStart).TotalMilliseconds
            if ($allDone -or $elapsed -gt ([int]$script:Settings.PingTimeoutMs + 800)) {
                Complete-PingProbe
                Update-Tray
            }
        }
    }
    $script:Phase = '外设-系统日志'
    # === 4. 系统日志硬件错误 ===
    if ($script:Settings.WatchEventLog -and ((Get-Date) - $lastEvtCheck).TotalSeconds -ge [int]$script:Settings.EventLogCheckSec) {
        $lastEvtCheck = Get-Date
        Invoke-UiPump
        Invoke-EventLogCheck
        Invoke-UiPump
        Update-Tray
    }
    $script:Phase = '外设-完整扫描'
    # === 5. 定期完整扫描（发现"设备还在但出故障"） ===
    if ($script:ForceFullScan -or ((Get-Date) - $lastFullScan).TotalSeconds -ge [int]$script:Settings.FullScanSec) {
        $script:ForceFullScan = $false
        $lastFullScan = Get-Date
        Invoke-UiPump
        try {
            $realProb = New-Object System.Collections.ArrayList
            $benign = New-Object System.Collections.ArrayList
            foreach ($row in [DevNative]::DetailsFor([string[]]$Ids)) {
                $id = $row[0]; $name = (Get-BestDeviceName -Friendly $row[1] -Reported $row[6]); $cls = $row[2]; $prob = [int]$row[4]
                $was = $script:Known[$id]
                if ($was) {
                    if ($prob -ne 0 -and $was.Problem -eq 0) {
                        $desc = Get-ProblemText $prob
                        if (Test-BenignProblem $prob) {
                            Write-Log ("[状态变化] {0}  {1}（正常，不报警）" -f $name, $desc) -Kind 'Warn'
                        } else {
                            $t = "[设备故障] {0}  <{1}>  {2}" -f $name, (Get-ShortId $id), $desc
                            Write-Log $t -Kind 'Error'
                            Write-CsvEvent -Kind '设备故障' -Id $id -Name $name -Class $cls -Extra $desc
                            Add-Recent -Kind '故障' -Label $name -Id $id -Detail $desc
                            $script:EventCount++
                            Show-Alert -Title ("⚠ 设备故障：" + $name) -Body ("{0}`n类别：{1}`n{2}`n{3}" -f $name, $cls, $desc, (Get-ShortId $id)) -Kind 'Warn'
                        }
                    }
                    $script:Known[$id].Problem = $prob
                    if ($name) { $script:Known[$id].Name = $name }
                }
                if ($prob -ne 0) {
                    $item = [pscustomobject]@{ name = $name; cls = $cls; id = $id; code = $prob; text = (Get-ProblemText $prob) }
                    if (Test-BenignProblem $prob) { [void]$benign.Add($item) } else { [void]$realProb.Add($item) }
                }
            }
            $script:ProblemList = @($realProb)
            $script:BenignList = @($benign)
            $script:ProblemCount = $realProb.Count
            Update-Tray
        } catch { }
    }


    }   # ---- 外设监测结束 ----

    if ($script:Mode -ne 'Device') {   # ======== 硬件监测 ========
        # 懒加载 + 延迟 2 秒：
        #   - 懒加载：只跑外设时不白等；运行中切到硬件模式时才加载
        #   - 延迟 2 秒（仅启动时那一次）：先让面板把设备列表显示出来，
        #     再去啃 LHM 那 5 秒。否则网页端口虽然在监听，页面却一直是空的
        $startupDelayOk = $true
        if ($script:StartTime) {
            $startupDelayOk = (((Get-Date) - $script:StartTime).TotalSeconds -ge 2)
        }
        if (-not $script:LhmTried -and $startupDelayOk) {
            $script:LhmTried = $true
            if ($Console) { Write-Host '正在加载低层传感器库（约 5 秒，界面会卡一下）…' -ForegroundColor DarkGray }
            try { Initialize-LhmSensors } catch { }
            # 这是启动期的一次性成本，不算"运行期卡顿"，否则看门狗会误报一条 3 秒阻塞
            $script:MaxBlockMs      = 0.0
            $script:MaxBlockWhere   = ''
            $script:LastPumpAt      = $null
            $script:LastBlockReport = $null
        }
    # === 5b. 开机自启状态缓存（每 300 秒才查一次；Get-ScheduledTask 一次要 500ms）===
    if (((Get-Date) - $script:AutoStartChecked).TotalSeconds -ge 300) {
        $script:AutoStartChecked = Get-Date
        try { $script:AutoStartOn = Get-AutostartState } catch { }
    }
    $script:Phase = '硬件-低层传感器'
    # === 6. 低层传感器（内存温度 / CPU 功耗 / GPU 热点等，需管理员）===
    if ((Get-Date) -ge $script:NextExtAt) {
        $script:NextExtAt = (Get-Date).AddSeconds([Math]::Max(2, [int]$script:Settings.LhmSampleSec))
        if ($script:LhmOk) {
            $d2 = $null
            try { $d2 = Read-LhmSensors } catch { }
            if ($d2) { $script:SensorsExt = $d2 }
        }
    }
    $script:Phase = '硬件-采样'
    # === 7. 硬件采样（CPU/内存/温度/GPU/磁盘/网络） ===
    Invoke-UiPump
    if ((Get-Date) -ge $script:NextHwAt) {
        $script:NextHwAt = (Get-Date).AddMilliseconds([Math]::Max(500, [int]$script:Settings.HwSampleMs))
        try { Update-Hardware } catch { }
    }

    }   # ---- 硬件监测结束 ----
    # === 7. 变化停下来后，合并成通知 ===
    if ($script:Pending.Count -gt 0 -and ((Get-Date) - $script:PendingSince).TotalSeconds -ge [double]$script:Settings.CoalesceSec) {
        try { Invoke-PendingNotify } catch { Write-Log ("通知合并异常：{0}" -f $_.Exception.Message) -Kind 'Error' }
    }

    # === 9. 卡顿看门狗汇报（每 30 秒一次，>250ms 才记）===
    if ($null -eq $script:LastBlockReport) { $script:LastBlockReport = Get-Date }
    if (((Get-Date) - $script:LastBlockReport).TotalSeconds -ge 30) {
        if ($script:MaxBlockMs -gt 250) {
            Write-Log ("界面卡顿看门狗：过去 30 秒内最长阻塞 {0:N0} ms（发生在：{1}）" -f $script:MaxBlockMs, $script:MaxBlockWhere) -Kind 'Warn'
        }
        $script:MaxBlockMs = 0.0
        $script:LastBlockReport = Get-Date
    }

    # === 10. 托盘刷新 ===
    if (((Get-Date) - $lastTrayUpdate).TotalSeconds -ge 5) {
        $lastTrayUpdate = Get-Date
        Update-Tray
    }
}

# ============================================================================
#  退出
# ============================================================================
Write-Log '监控已停止。'
try { Show-Stats } catch { }   # 静默写 logs\summary.txt，不弹窗
try {
    Get-EventSubscriber -SourceIdentifier 'DevWatch_Change' -ErrorAction SilentlyContinue | Unregister-Event -ErrorAction SilentlyContinue
} catch { }
if ($script:WebListener) {
    try { $script:WebListener.Stop(); $script:WebListener.Close() } catch { }
}
if ($script:Tray) {
    try { $script:Tray.Visible = $false; $script:Tray.Dispose() } catch { }
}
try { $mutex.ReleaseMutex() } catch { }
try { $mutex.Dispose() } catch { }

# LibreHardwareMonitor 会留下后台线程，脚本跑完了进程却退不掉 ——
# 表现就是：日志已经写了"监控已停止"，但互斥锁还被占着，下次启动会误判"已经在运行"。
# 所以这里显式收尾，再用 Exit 强制结束进程。
try { if ($script:LhmPc) { $script:LhmPc.Close() } } catch { }
try { Remove-Item -LiteralPath $script:PidFile -Force -ErrorAction SilentlyContinue } catch { }
[System.Environment]::Exit(0)