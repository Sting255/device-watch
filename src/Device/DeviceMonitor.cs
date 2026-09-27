// ============================================================================
//  DeviceMonitor.cs —— 设备接入/断开监测
//
//  一轮 Poll() 的流程（与 DeviceWatch.ps1 第 5540~5768 行的"外设监测"分节一致）：
//    在线 ID 快照 → 和上一轮比对 → 去抖（边等边泵消息）→ 重新比对
//      → 查名字 / 写日志 / 更新 Known → 进 Pending 队列
//      → 合并窗口到期后按物理设备分组、冷却过滤、合并成 1~N 条通知
//
//  【和主循环的分工】（Program.cs 已经写死了调用点，成员名不能改）
//    Initialize()      启动时建基线（登记 Known，不报警）
//    Poll()            按 PollMs 调用：比对 + 去抖 + 入队（内含每 FullScanSec 一次的故障码刷新）
//    FlushPending()    主循环每 50ms 调一次：非阻塞地检查合并窗口，到期就发通知
//    CheckEventLog()   主循环按 EventLogCheckSec 调用一次：扫系统日志里的硬件错误
//    ForceFullScan     置 true 时下一次 Poll 重做完整详情扫描（改完忽略规则要立刻生效）
//    PumpHook          可选：主循环把它的 Pump 挂进来，去抖那 200ms 里网页请求照常被处理
//
//  AppState.ProblemList / BenignList 由 Program.ScanProblems 负责（每 FullScanSec 一次全量
//  Details()），这里不再写：两个写入者用不同的过滤口径互相覆盖，网页上会看到列表来回跳。
//  本类只负责 Known[].Problem 的刷新和"从正常变故障"那一次弹窗（PS 版也是这么分工的）。
//
//  两个必须守住的点：
//    1. 每 200ms 就在跑，热路径里不能有大分配、不能读文件；
//    2. 任何一段抛异常都不能让主循环整个停摆 —— 每一节都单独 try 住。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceWatch
{
    /// <summary>设备监测。主循环创建一次，先 Initialize() 建基线，然后反复 Poll()。</summary>
    public sealed class DeviceMonitor
    {
        // ====================================================================
        //  主循环用的接口
        // ====================================================================
        /// <summary>
        /// 可选钩子：主循环把自己的 Pump（DoEvents + 处理网页请求）挂进来。
        /// 去抖等待期间会调它，这样插拔那 200ms 里网页请求也不会被吊着。
        /// Program.cs 里加一行即可：<c>DeviceMonitor.PumpHook = Pump;</c>
        /// 不挂也能跑，只是去抖期间网页要等这一下。
        /// </summary>
        public static Action PumpHook;

        // ====================================================================
        //  内部类型
        // ====================================================================
        /// <summary>一条待通知的变化（一个设备节点）。</summary>
        private sealed class Pending
        {
            public string Id = "";
            public string Name = "";
            public string Class = "";
            public bool WasKnown;
            public string Kind = "Off";     // Off / On
        }

        /// <summary>同一个物理设备（一次插拔）的一批节点。</summary>
        private sealed class Group
        {
            public string Key = "";         // 物理设备分组键，如 USB\VID_046D&PID_C534
            public string Kind = "Off";
            public readonly List<Pending> Events = new List<Pending>();
        }

        private sealed class ProviderRule
        {
            public Regex Pattern;
            public string Name;
        }

        // ====================================================================
        //  状态
        // ====================================================================
        // 上一轮的在线 ID 快照。注意：它包含被忽略规则命中的设备，
        // 所以不能拿 AppState.Known 顶替 —— Known 里没有那些设备，
        // 每一轮都会被当成"新增"，去抖 + 查名字会一直白跑。
        private HashSet<string> _lastIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _since = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _everSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 合并通知
        private readonly List<Pending> _pending = new List<Pending>();
        private DateTime _pendingSince = DateTime.MinValue;
        private readonly Dictionary<string, DateTime> _cooldown = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _changeAt = DateTime.MinValue;     // 本轮"发现变化"的时刻，用来算端到端延迟

        // 定时任务
        private DateTime _lastFullScan = DateTime.MinValue;
        private DateTime _lastForcedScan = DateTime.MinValue;
        private DateTime _lastEvtTime = DateTime.MinValue;
        private DateTime _evtRetryAt = DateTime.MinValue;
        private readonly HashSet<long> _seenRecordIds = new HashSet<long>();
        private readonly HashSet<string> _seenEvtKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 置 true 时下一次 Poll 重做一次完整详情扫描（托盘菜单「立即完整扫描」/ 网页改完忽略规则用）。
        /// volatile：托盘和网页线程都会置它，主循环这边读。
        /// 标志由主循环负责清掉（Program.cs 的完整扫描分支），这里只读不写。
        /// </summary>
        public static volatile bool ForceFullScan;

        private bool _initialized;

        // ====================================================================
        //  静态表：故障码 / 通用名 / 计分 / 日志提供程序
        // ====================================================================
        /// <summary>设备故障码中文解释（ConfigManager 的 DN_HAS_PROBLEM 代码）。</summary>
        private static readonly Dictionary<int, string> ProblemText = new Dictionary<int, string>
        {
            { 0, "正常" }, { 1, "设备未配置" }, { 2, "内存不足" }, { 3, "驱动未安装" },
            { 4, "注册表信息不完整" }, { 5, "系统资源不足" }, { 6, "BIOS 未分配资源" }, { 7, "需要手动配置" },
            { 8, "BIOS 报告资源冲突" }, { 9, "驱动加载失败" }, { 10, "设备无法启动" }, { 11, "设备故障" },
            { 12, "资源不足" }, { 13, "需要重新枚举" }, { 14, "需要重启电脑" }, { 15, "正在重新枚举" },
            { 16, "资源重新分配失败" }, { 17, "需要更多资源" }, { 18, "驱动需要重新安装" }, { 19, "注册表损坏" },
            { 20, "驱动加载失败" }, { 21, "设备正在移除" }, { 22, "设备已被禁用" }, { 23, "驱动加载失败" },
            { 24, "设备不存在" }, { 25, "设备已被移除" }, { 26, "驱动安装失败" }, { 27, "驱动未安装" },
            { 28, "驱动未安装" }, { 29, "固件未提供必需资源" }, { 30, "不支持 IRQ 共享" }, { 31, "设备无法使用" },
            { 32, "驱动加载失败" }, { 33, "驱动加载失败" }, { 34, "需要重启" }, { 35, "固件版本不匹配" },
            { 36, "设备正在关机" }, { 37, "驱动加载失败" }, { 38, "驱动重复安装" }, { 39, "驱动加载失败" },
            { 40, "信息不完整" }, { 41, "加载驱动失败" }, { 42, "设备正在等待重启" }, { 43, "驱动报告设备出错，已被停止" },
            { 44, "设备被软件阻止启动" }, { 45, "设备当前未连接" }, { 46, "设备被系统安全策略阻止" }, { 47, "设备正在等待删除" },
            { 48, "设备被软件阻止启动" }, { 49, "设备被系统策略阻止启动" }, { 50, "设备被系统策略阻止启动" },
            { 51, "设备被用户手动禁用" }, { 52, "设备被组策略禁用" }, { 53, "设备被管理员禁用" }, { 54, "设备正在等待驱动安装" }
        };

        // 这些故障码表示"被主动禁用/未连接"，是正常状态，不是故障。
        // 22（设备已被禁用）最常见：用户自己在设备管理器里禁掉的声卡、摄像头都长这样。
        private static readonly HashSet<int> BenignProblemCodes = new HashSet<int> { 21, 22, 45, 51, 52, 53 };

        /// <summary>Windows 的兜底通用名，信息量太低，命名时让位给更具体的名字。</summary>
        private static readonly Regex GenericName = new Regex(
            @"^(USB 输入设备|USB 复合设备|USB Composite Device|符合 HID 标准的.*|HID-compliant .*|HID Keyboard Device|HID 设备|I2C HID 设备|蓝牙 HID 设备|Microsoft Input Configuration Device|网络控制器|以太网控制器|视频控制器.*|多媒体控制器|SM 总线控制器|PCI 主桥|通用串行总线.*控制器|蓝牙外围设备|未知设备|Generic .*|Standard .*|.*控制器|卷|磁盘驱动器|基本系统设备)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GroupRx = new Regex(@"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}", RegexOptions.Compiled);
        private static readonly Regex RxClassMouse = new Regex("Mouse", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxClassKeyboard = new Regex("Keyboard", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxClassDisk = new Regex("DiskDrive|USBSTOR|SCSIAdapter|HDC", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxClassCamera = new Regex("Camera|Image", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxClassAudio = new Regex("AudioEndpoint|MEDIA|Volume", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxClassNet = new Regex("^Net$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxClassMonitor = new Regex("Monitor", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNameMouse = new Regex("鼠标|Mouse", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNameKeyboard = new Regex("键盘|Keyboard", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNameOther = new Regex("摄像头|Camera|耳机|Headset|手柄|Gamepad|打印机|Printer", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxHub = new Regex("集线器|Hub|HUB", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>系统日志里与硬件相关的提供程序（顺序即优先级，先命中先用）。</summary>
        private static readonly ProviderRule[] ProviderRules =
        {
            new ProviderRule { Pattern = new Regex("Kernel-PnP", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "设备管理器" },
            new ProviderRule { Pattern = new Regex("DriverFrameworks-UserMode", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "USB 驱动框架" },
            new ProviderRule { Pattern = new Regex("^disk$|volmgr|partmgr", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "磁盘" },
            new ProviderRule { Pattern = new Regex("Ntfs", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "文件系统" },
            new ProviderRule { Pattern = new Regex("storahci|stornvme|storport|msahci|iaStor", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "存储控制器" },
            new ProviderRule { Pattern = new Regex("USB|usbhub|usbxhci|usbccgp|UcmUcsi", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "USB" },
            new ProviderRule { Pattern = new Regex("BTHUSB|BTHPORT|Bluetooth", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "蓝牙" },
            new ProviderRule { Pattern = new Regex("WHEA-Logger", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "硬件错误" },
            new ProviderRule { Pattern = new Regex("ndis|Netwtw|e1[ide]|rt6|rtw|mtkwl|netwtw", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "网卡" },
            new ProviderRule { Pattern = new Regex("Kernel-Power|Kernel-Boot", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "电源" },
            new ProviderRule { Pattern = new Regex("Display|nvlddmkm|amdkmdag|igfx", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "显卡" },
            new ProviderRule { Pattern = new Regex("HDAudBus|IntcAudioBus|ksthunk", RegexOptions.IgnoreCase | RegexOptions.Compiled), Name = "音频" }
        };

        // ====================================================================
        //  Initialize —— 建立基线
        // ====================================================================
        /// <summary>首次完整扫描：登记已知设备，只建基线不报警。</summary>
        public void Initialize()
        {
            int ignored = 0;
            try
            {
                foreach (var row in DevNative.Details())
                {
                    if (row == null || row.Length < 7 || string.IsNullOrEmpty(row[0])) continue;
                    string id = row[0];
                    string name = BestName(row[1], row[6]);
                    string cls = row[2];

                    if (IsIgnoredDevice(id, name, cls)) { ignored++; continue; }
                    RegisterKnown(id, name, row[1], cls, row[3], ParseInt(row[4], 0), ParseUInt(row[5]), row[6]);
                    if (!_since.ContainsKey(id)) _since[id] = DateTime.Now;
                }
                Log.Write($"设备基线：已登记 {AppState.I.Known.Count} 台在线设备（按规则忽略 {ignored} 台）");
            }
            catch (Exception ex)
            {
                // 基线失败只影响"已知设备表"，监测照样跑：后续靠每轮的差异比对把设备补进来
                Log.Write($"基线扫描失败：{ex.Message}", "Error");
            }

            var snap = SnapshotIds();
            if (snap != null) _lastIds = snap;

            _lastFullScan = DateTime.Now;
            _lastEvtTime = DateTime.Now.AddSeconds(-2);   // 只看启动之后的日志，别把上次开机以来的陈年旧账翻出来
            _initialized = true;
        }

        // ====================================================================
        //  Poll —— 比对 → 去抖 → 入队
        // ====================================================================
        /// <summary>跑一轮。</summary>
        public void Poll()
        {
            try
            {
                // 主循环每 50ms 会调 FlushPending()，这里再兜一次：
                // 万一将来调用点被挪走，通知也不会一直卡在队列里
                FlushPending();

                if (IsHardwareOnly()) return;   // 只跑硬件模式时不做设备比对（PS: if ($Mode -ne 'Hardware')）

                // 每一节单独兜异常：任何一节炸了都只是少跑一节，不能让主循环停摆
                try { PollDevices(); }
                catch (Exception ex) { Log.Write($"设备扫描异常：{ex.GetType().Name}: {ex.Message}", "Error"); }

                try { RefreshProblemsIfDue(); }
                catch (Exception ex) { Log.Write($"设备状态刷新异常：{ex.GetType().Name}: {ex.Message}", "Error"); }
            }
            catch (Exception ex)
            {
                try { Log.Write($"设备监测轮询异常：{ex.GetType().Name}: {ex.Message}", "Error"); } catch { }
            }
        }

        private void PollDevices()
        {
            // 主循环万一忘了先 Initialize，这里兜一次：否则启动瞬间会把几百台设备
            // 全当成"新接入"报一遍（比多花 118ms 建基线糟糕得多）
            if (!_initialized)
            {
                Log.Write("设备监测未初始化就被轮询，自动补一次基线", "Warn");
                Initialize();
                return;
            }

            AppState.I.CurrentPhase = "外设-设备比对";

            var nowIds = SnapshotIds();
            if (nowIds == null) return;      // 枚举失败：当这一轮没跑（绝不能当"全掉线"处理）

            var added = new List<string>();
            var removed = new List<string>();
            Diff(_lastIds, nowIds, added, removed);
            if (added.Count == 0 && removed.Count == 0) return;

            _changeAt = DateTime.Now;        // 记下"轮询发现变化"的时刻，用来算端到端延迟

            // 去抖：USB 插拔会连着触发好几次变化，等它稳定再取最终结果。
            // 这段等待期间必须继续泵消息，否则托盘菜单会整整冻住 DebounceMs 毫秒
            // （默认 200ms，插拔时体感就是"一点就卡"）。
            SleepPump(Math.Max(50, ClampInt(Settings.I.DebounceMs, 50, 10000)));

            var nowIds2 = SnapshotIds();
            if (nowIds2 == null) return;     // 去抖期间枚举失败：下一轮重新比对，_lastIds 保持不动

            added.Clear();
            removed.Clear();
            Diff(_lastIds, nowIds2, added, removed);
            nowIds = nowIds2;
            if (added.Count == 0 && removed.Count == 0)
            {
                _lastIds = nowIds;           // 抖回去了（枚举残留），什么都不做
                return;
            }

            // 只给"新接入"的设备查名字：已经断开的设备在 SetupAPI 里根本打不开，
            // 把 removed 也传进去只会白跑一趟（全量扫描要 118ms，按 ID 直开只要 3ms）
            Dictionary<string, string[]> info = null;
            if (added.Count > 0)
            {
                try { info = ResolveInfo(added); }
                catch (Exception ex) { Log.Write($"查询新设备详情失败：{ex.Message}", "Warn"); }
            }

            var removedEvents = new List<Pending>();
            var addedEvents = new List<Pending>();

            foreach (string id in removed)
            {
                string name = null, cls = null;
                DeviceEntry prev = null;
                lock (AppState.I.Sync) AppState.I.Known.TryGetValue(id, out prev);
                if (prev != null) { name = prev.Name; cls = prev.Class; }
                string[] row;
                if (info != null && info.TryGetValue(id, out row)) { name = BestName(row[1], row[6]); cls = row[2]; }
                if (string.IsNullOrEmpty(name)) name = id;
                if (IsIgnoredDevice(id, name, cls)) continue;

                string alive = "";
                DateTime since;
                if (_since.TryGetValue(id, out since))
                {
                    TimeSpan span = DateTime.Now - since;
                    alive = span.TotalHours >= 1
                        ? $"（本次已连接 {span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} 小时）"
                        : $"（本次已连接 {span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} 分钟）";
                }

                Log.Write($"[断联] {name}  <{ShortId(id)}>{alive}", "Warn");
                removedEvents.Add(new Pending { Id = id, Name = name, Class = cls, WasKnown = true, Kind = "Off" });
                _since.Remove(id);
                lock (AppState.I.Sync) AppState.I.Known.Remove(id);
            }

            foreach (string id in added)
            {
                string name = "", cls = "", enm = "", reported = "", friendly = "";
                int prob = 0;
                uint status = 0;
                string[] row;
                if (info != null && info.TryGetValue(id, out row))
                {
                    friendly = row[1];
                    name = BestName(row[1], row[6]);
                    cls = row[2];
                    enm = row[3];
                    prob = ParseInt(row[4], 0);
                    status = ParseUInt(row[5]);
                    reported = row[6];
                }
                if (string.IsNullOrEmpty(name)) name = id;
                if (IsIgnoredDevice(id, name, cls)) continue;

                // 以前记录过 = 重新连接，从没见过 = 新设备接入
                bool wasKnown = _everSeen.Contains(id);
                _everSeen.Add(id);

                RegisterKnown(id, name, friendly, cls, enm, prob, status, reported);
                if (!_since.ContainsKey(id)) _since[id] = DateTime.Now;

                string kindText = wasKnown ? "重新连接" : "接入";
                Log.Write($"[{kindText}] {name}  <{ShortId(id)}>", "Info");
                addedEvents.Add(new Pending { Id = id, Name = name, Class = cls, WasKnown = wasKnown, Kind = "On" });
            }

            // 先攒起来：同一物理设备的本体和子接口可能差好几秒才枚举完，
            // 等变化停下来再统一合并通知，避免一个鼠标弹两条
            AddPending(removedEvents, "Off");
            AddPending(addedEvents, "On");

            _lastIds = nowIds;
        }

        /// <summary>在线 ID 快照。失败返回 null（调用方必须当"这轮没跑"，不能当"全掉线"）。</summary>
        private static HashSet<string> SnapshotIds()
        {
            try
            {
                var ids = DevNative.PresentIds();
                if (ids == null || ids.Length == 0) return null;   // 活的 Windows 不可能一台设备都没有 → 只可能是 API 失败
                var set = new HashSet<string>(ids.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids) if (!string.IsNullOrEmpty(id)) set.Add(id);
                return set.Count > 0 ? set : null;
            }
            catch { return null; }
        }

        private static void Diff(HashSet<string> before, HashSet<string> now, List<string> added, List<string> removed)
        {
            foreach (var id in now) if (!before.Contains(id)) added.Add(id);
            foreach (var id in before) if (!now.Contains(id)) removed.Add(id);
        }

        /// <summary>只为发生变化的设备取名称，避免整体扫描。</summary>
        private static Dictionary<string, string[]> ResolveInfo(List<string> ids)
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in DevNative.DetailsFor(ids.ToArray()))
            {
                if (row == null || row.Length < 7 || string.IsNullOrEmpty(row[0])) continue;
                map[row[0]] = row;
            }
            return map;
        }

        // ====================================================================
        //  FlushPending —— 合并窗口到期就发通知（非阻塞）
        // ====================================================================
        /// <summary>
        /// 检查合并窗口是否到期，到期就把攒下的变化合并成通知发出去。
        /// 主循环每 50ms 调一次，所以这里必须是非阻塞的检查 —— 绝不能在里头 Sleep。
        /// </summary>
        public void FlushPending()
        {
            if (_pending.Count == 0) return;
            double win = Settings.I.CoalesceSec;
            if (win < 0) win = 0;
            if ((DateTime.Now - _pendingSince).TotalSeconds < win) return;
            try { SendPendingNow(); }
            catch (Exception ex) { Log.Write($"通知合并异常：{ex.GetType().Name}: {ex.Message}", "Error"); }
        }

        private void AddPending(List<Pending> evs, string kind)
        {
            if (evs == null || evs.Count == 0) return;
            foreach (var e in evs) { e.Kind = kind; _pending.Add(e); }
            _pendingSince = DateTime.Now;      // 每来一批就重置窗口：等"没有新变化"的 CoalesceSec 秒
        }

        /// <summary>把攒下的变化合并后统一通知（PS 版的 Invoke-PendingNotify）。</summary>
        private void SendPendingNow()
        {
            if (_pending.Count == 0) return;

            int latMs = -1;
            if (_changeAt != DateTime.MinValue)
            {
                try { latMs = (int)(DateTime.Now - _changeAt).TotalMilliseconds; } catch { }
            }

            var batch = new List<Pending>(_pending);
            _pending.Clear();

            // 按「方向 + 物理设备」分组：一个鼠标被 Windows 拆成 8 个设备节点，只该弹一条
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in batch)
            {
                string gk = DeviceGroupKey(ev.Id);
                string key = ev.Kind + "|" + gk;
                Group g;
                if (!groups.TryGetValue(key, out g))
                {
                    g = new Group { Key = gk, Kind = ev.Kind };
                    groups[key] = g;
                }
                g.Events.Add(ev);
            }

            int threshold = ClampInt(Settings.I.BusEventThreshold, 1, 1000);

            // 刚通知过的同一台设备（同方向）直接跳过：本体和子接口经常差几秒才枚举完，
            // 第一批已经弹过窗了，第二批不该再弹
            var freshOff = new List<Group>();
            var freshOn = new List<Group>();
            foreach (var g in groups.Values)
            {
                if (IsCooled(g.Kind + "|" + g.Key)) continue;
                if (g.Kind == "Off") freshOff.Add(g); else freshOn.Add(g);
            }

            Dispatch(freshOff, "Off", threshold, latMs);
            Dispatch(freshOn, "On", threshold, latMs);
        }

        private void Dispatch(List<Group> keys, string kind, int threshold, int latMs)
        {
            if (keys.Count == 0) return;
            if (keys.Count >= threshold)
            {
                // 同一时刻好几台物理设备一起变化 —— 基本都是集线器复位或者供电问题。
                // 合成一条"总线事件"，比刷一堆弹窗有用得多。
                SendBusAlert(keys, kind);
                return;
            }
            foreach (var g in keys) SendGroupAlert(g, kind, latMs);
        }

        /// <summary>发一条"单个物理设备"的通知。</summary>
        private void SendGroupAlert(Group g, string kind, int latMs)
        {
            if (g.Events.Count == 0) return;
            Pending best = BestOf(g.Events);
            int n = g.Events.Count;

            // 标题按 CONTRACT.md 的格式（设备断开：X / 设备接入：X），重点关注的前面加 ⭐
            string title = (kind == "Off" ? "设备断开：" : "设备接入：") + best.Name;
            if (IsFocus(best)) title = "⭐ " + title;

            var body = new List<string>(4);
            body.Add("类别：" + (best.Class ?? ""));
            if (n > 1) body.Add($"同一物理设备的 {n} 个子设备一起变化");
            body.Add(ShortId(g.Key));
            body.Add(DateTime.Now.ToString("HH:mm:ss"));

            // 断联排行按物理设备统计：一次拔插算一次，而不是算 8 次
            BumpGroupStat(g.Key, best.Name, kind == "Off");
            AppState.I.AddRecent(kind == "Off" ? "断联" : "接入", best.Name, g.Key,
                n > 1 ? n + " 个子设备" : (best.Class ?? ""));
            SetCooldown(kind + "|" + g.Key);

            if (latMs >= 0)
            {
                var st = Settings.I;
                Log.Write($"  [延迟] 从发现变化到弹窗 {latMs} ms（轮询 {st.PollMs} + 去抖 {st.DebounceMs} + 查名字 + 合并 {(int)(st.CoalesceSec * 1000)}）");
            }

            Notify(title, string.Join("\n", body), kind);
        }

        /// <summary>同一时间窗内多台物理设备一起变化 → 一条总线事件通知。</summary>
        private void SendBusAlert(List<Group> keys, string kind)
        {
            var pairs = new List<KeyValuePair<string, Pending>>(keys.Count);
            foreach (var g in keys) pairs.Add(new KeyValuePair<string, Pending>(g.Key, BestOf(g.Events)));

            // 名字最像真实型号的排前面，通知里先列它们
            var ordered = pairs.OrderByDescending(p => NameScore(p.Value.Name, p.Value.Class)).ToList();
            foreach (var p in ordered) BumpGroupStat(p.Key, p.Value.Name, kind == "Off");

            bool isHub = ordered.Any(p => RxHub.IsMatch(p.Value.Name ?? ""));
            string busName = isHub ? "USB 集线器复位" : "USB 总线事件";

            string title = kind == "Off"
                ? $"⚠ {busName}：{keys.Count} 个设备同时断开"
                : $"✅ 总线恢复：{keys.Count} 个设备同时接回";

            var body = new List<string>(keys.Count + 3);
            int show = Math.Min(4, ordered.Count);
            for (int i = 0; i < show; i++) body.Add("· " + ordered[i].Value.Name);
            if (ordered.Count > show) body.Add($"· 等共 {ordered.Count} 个设备");
            body.Add("");
            body.Add("多个设备共用的链路（集线器或供电）出了问题，不是单个设备的毛病");
            body.Add(DateTime.Now.ToString("HH:mm:ss"));

            if (kind == "Off")
                Log.Write($"[总线事件] {keys.Count} 个设备同时断开：{string.Join("、", ordered.Select(p => p.Value.Name))}", "Warn");
            else
                Log.Write($"[总线恢复] {keys.Count} 个设备同时接回", "Info");

            AppState.I.AddRecent(kind == "Off" ? "总线事件" : "总线恢复", $"{busName}（{keys.Count} 个设备）", "BUS",
                string.Join("、", ordered.Take(3).Select(p => p.Value.Name)));

            foreach (var g in keys) SetCooldown(g.Kind + "|" + g.Key);

            Notify(title, string.Join("\n", body), kind);
        }

        /// <summary>
        /// 统一出口：先过一遍"暂停弹窗 / 学习模式"，再交给 Toast。
        /// PS 版的 Show-Alert 就是在这里判的；Toast.Show 只认 Settings.Notify，
        /// 托盘上的"暂停通知"和"学习模式 10 分钟"是运行时开关，必须在这里拦。
        /// </summary>
        private static void Notify(string title, string body, string kind)
        {
            try
            {
                if (!AppState.I.NotifyEnabled) return;
                if (AppState.I.Learning)
                {
                    DateTime? until = AppState.I.LearnUntil;
                    if (!until.HasValue || until.Value > DateTime.Now) return;
                }
            }
            catch { }
            Toast.Show(title, body, kind);
        }

        // ====================================================================
        //  故障码刷新（完整扫描由 Program.ScanProblems 负责，这里只管 Known 和那一次弹窗）
        // ====================================================================
        private void RefreshProblemsIfDue()
        {
            bool force = ForceFullScan;
            if (force)
            {
                // 用户手点的（托盘「立即完整扫描」/ 网页改了规则）：必须立刻做。
                // 但不能用"距上次扫描的间隔"当门槛 —— 刚启动时上次扫描就在眼前，
                // 会把用户的第一次请求吞掉。所以单独记"上次强制扫描"的时刻，
                // 只在标志没被主循环清掉时挡一下：全量枚举 120ms，200ms 来一次会吃满 CPU。
                if (_lastForcedScan != DateTime.MinValue && (DateTime.Now - _lastForcedScan).TotalSeconds < 5) return;
                _lastForcedScan = DateTime.Now;
            }
            else
            {
                if (_lastFullScan != DateTime.MinValue &&
                    (DateTime.Now - _lastFullScan).TotalSeconds < ClampInt(Settings.I.FullScanSec, 5, 86400)) return;
            }

            _lastFullScan = DateTime.Now;
            AppState.I.CurrentPhase = "外设-完整扫描";
            RefreshProblems(force);
        }

        /// <summary>
        /// 刷新每台已知设备的故障码，并在"从正常变成有故障"的那一刻报一次。
        /// full=true（改了忽略规则）时重新全量枚举，让新规则立刻生效。
        /// </summary>
        private void RefreshProblems(bool full)
        {
            string[] ids;
            lock (AppState.I.Sync)
            {
                ids = new string[AppState.I.Known.Count];
                AppState.I.Known.Keys.CopyTo(ids, 0);
            }

            List<string[]> rows;
            if (full)
            {
                // 指令是"重做完整详情扫描"，而且忽略规则可能刚变过：
                // 必须重新全量枚举，才能把"现在该忽略的"踢出去、
                // "以前被忽略、现在要看的"收进来（按 ID 直开是查不到没登记过的设备的）
                rows = DevNative.Details();
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                    if (row != null && row.Length >= 7 && !string.IsNullOrEmpty(row[0])) present.Add(row[0]);

                var gone = new List<string>();
                lock (AppState.I.Sync)
                {
                    foreach (var id in AppState.I.Known.Keys) if (!present.Contains(id)) gone.Add(id);
                }
                foreach (var id in gone)
                {
                    lock (AppState.I.Sync) AppState.I.Known.Remove(id);
                    _since.Remove(id);
                }
            }
            else
            {
                // 周期刷新用 DetailsFor(已知设备)：拿到的故障码和全量 Details() 一样，
                // 但全量枚举实测 118ms（289 台 × 4 次属性读），每 90 秒在主循环里卡一下
                // 会被看门狗记成"界面卡顿"；按 ID 直开只要 3ms。
                rows = ids.Length > 0 ? DevNative.DetailsFor(ids) : DevNative.Details();
            }

            foreach (var row in rows)
            {
                if (row == null || row.Length < 7 || string.IsNullOrEmpty(row[0])) continue;
                string id = row[0];
                string name = BestName(row[1], row[6]);
                string cls = row[2];
                int prob = ParseInt(row[4], 0);

                DeviceEntry cur = null;
                lock (AppState.I.Sync) AppState.I.Known.TryGetValue(id, out cur);

                if (cur == null)
                {
                    // 全量模式：以前被忽略、现在该看的设备直接补进已知表。
                    // 不报"接入"—— 它并没有真的插上，只是规则变了
                    if (!full || IsIgnoredDevice(id, name, cls)) continue;
                    RegisterKnown(id, name, row[1], cls, row[3], prob, ParseUInt(row[5]), row[6]);
                    if (!_since.ContainsKey(id)) _since[id] = DateTime.Now;
                    continue;
                }

                // 规则改严了：这台设备现在该被忽略，从已知表里拿掉
                if (IsIgnoredDevice(id, name, cls))
                {
                    lock (AppState.I.Sync) AppState.I.Known.Remove(id);
                    _since.Remove(id);
                    continue;
                }

                // 只在"从正常变成有故障"的那一刻报警，否则每 90 秒重复弹一次同样的问题
                if (prob != 0 && cur.Problem == 0)
                {
                    string desc = ProblemTextOf(prob);
                    if (IsBenignProblem(prob))
                    {
                        // 22（设备已被禁用）这类是用户主动为之，不算异常
                        Log.Write($"[状态变化] {name}  {desc}（正常，不报警）", "Warn");
                    }
                    else
                    {
                        Log.Write($"[设备故障] {name}  <{ShortId(id)}>  {desc}", "Error");
                        AppState.I.AddRecent("故障", name, id, desc);
                        Notify($"⚠ 设备故障：{name}", $"{name}\n类别：{cls}\n{desc}\n{ShortId(id)}", "Warn");
                    }
                }

                lock (AppState.I.Sync)
                {
                    cur.Problem = prob;
                    if (!string.IsNullOrEmpty(name)) cur.Name = name;
                }
            }
        }

        // ====================================================================
        //  CheckEventLog —— 系统日志里的硬件错误
        // ====================================================================
        /// <summary>
        /// 扫一次系统日志里新增的硬件错误（磁盘重置 storahci 129、WHEA、USB 驱动框架报错等）。
        /// 主循环按 EventLogCheckSec 调用；内部做了失败退避，不会失败一次就每 20 秒刷一遍日志。
        /// </summary>
        public void CheckEventLog()
        {
            try
            {
                if (!Settings.I.WatchEventLog) return;
                if (_evtRetryAt != DateTime.MinValue && DateTime.Now < _evtRetryAt) return;
                AppState.I.CurrentPhase = "外设-系统日志";
                CheckEventLogCore();
            }
            catch (Exception ex)
            {
                _evtRetryAt = DateTime.Now.AddSeconds(300);
                try { Log.Write($"系统日志检查异常：{ex.GetType().Name}: {ex.Message}", "Warn"); } catch { }
            }
        }

        /// <summary>
        /// 用 EventLogReader 而不是 System.Diagnostics.EventLog：后者的 Entries 是顺序读，
        /// 取最后一条也要把整个日志走一遍（实测几百毫秒到几秒），20 秒跑一次会把主循环拖死；
        /// EventLogReader 走 Windows 事件日志 API + XPath 时间过滤，只读增量。
        /// </summary>
        private void CheckEventLogCore()
        {
            // 去重表涨到几千条就整体清掉（PS 版的 SeenEvents 也是超过 5000 就重置）
            if (_seenRecordIds.Count > 5000)
            {
                _seenRecordIds.Clear();
                _seenEvtKeys.Clear();
            }

            DateTime from = _lastEvtTime;
            if (from == DateTime.MinValue)
            {
                _lastEvtTime = DateTime.Now.AddSeconds(-5);
                return;
            }

            try
            {
                string since = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                string xpath = "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime >= '" + since + "']]]";
                var query = new EventLogQuery("System", PathType.LogName, xpath);
                query.TolerateQueryErrors = true;

                using (var reader = new EventLogReader(query))
                {
                    EventRecord rec;
                    while ((rec = reader.ReadEvent()) != null)
                    {
                        try { HandleEventRecord(rec); }
                        catch { }                 // 日志里什么样的怪记录都有，坏一条不能拖垮整轮
                        finally { try { rec.Dispose(); } catch { } }
                    }
                }

                // 下一轮只读增量（PS 版起点一直停在启动时刻，等于每次都把启动以来的日志重扫一遍）
                _lastEvtTime = DateTime.Now.AddSeconds(-1);
                _evtRetryAt = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                // 读日志失败（权限 / 服务被停）时不要每 20 秒刷一条日志，缓 5 分钟再试
                _evtRetryAt = DateTime.Now.AddSeconds(300);
                Log.Write($"系统日志检查失败：{ex.GetType().Name}: {ex.Message}（300 秒后重试）", "Warn");
            }
        }

        private void HandleEventRecord(EventRecord rec)
        {
            long rid = rec.RecordId ?? 0;
            if (rid != 0 && !_seenRecordIds.Add(rid)) return;      // 同一条记录只处理一次

            string provider = rec.ProviderName ?? "";
            string cat = HwCategory(provider);
            if (cat == null) return;                               // 与硬件无关的提供程序直接不看

            string msg = FirstLine(SafeMessage(rec));
            if (msg.Length > 180) msg = msg.Substring(0, 180) + "...";

            int lvl = rec.Level ?? 4;
            string lvlName = lvl == 1 ? "严重" : lvl == 2 ? "错误" : lvl == 3 ? "警告" : "信息";
            int eid = rec.Id;

            Log.Write($"[系统日志/{cat}] {lvlName} ({provider}) ID={eid}  {msg}", lvl <= 2 ? "Error" : "Warn");

            // 忽略规则也管日志：有的机器某条 storahci 129 天天刷，用户加了规则就该闭嘴
            if (MatchesAnyPattern(provider + " " + msg)) return;

            string key = provider + "|" + eid.ToString(CultureInfo.InvariantCulture) + "|" + msg;
            if (!_seenEvtKeys.Add(key)) return;                    // 同一条消息反复刷屏只提醒一次

            if (Settings.I.NotifyEventLog)
                Notify($"[硬件告警] {cat}", $"{lvlName}\n{provider}\nID={eid}  {msg}", "Warn");

            // 计数由 AddRecent 自己维护（AppState.AddRecent 会递增 EventCount），手动再加会重复计数
            AppState.I.AddRecent("系统", cat + "/" + provider, "", msg);
        }

        private static string SafeMessage(EventRecord rec)
        {
            // EventRecord 上没有 Message 属性（那是 EventLogRecord 的，构造不出来），
            // .NET Framework 里拿"人话"只能用 FormatDescription()：它去加载提供程序的
            // 消息 DLL 并套用参数。消息资源缺失时它抛 EventLogException，所以必须兜住。
            try { return rec.FormatDescription() ?? ""; } catch { return ""; }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (var line in text.Split('\n'))
            {
                string s = line.Trim().TrimEnd('\r');
                if (s.Length > 0) return s;
            }
            return "";
        }

        private static string HwCategory(string provider)
        {
            if (string.IsNullOrEmpty(provider)) return null;
            foreach (var r in ProviderRules) if (r.Pattern.IsMatch(provider)) return r.Name;
            return null;
        }

        // ====================================================================
        //  命名 / 分组 / 规则匹配
        // ====================================================================
        /// <summary>
        /// 取"不通用且最长"的名字：USB 设备自报的型号最准，
        /// 但 PCI 设备自报的往往是"以太网控制器"这种兜底名，这时该用驱动给的型号名。
        /// </summary>
        private static string BestName(string friendly, string reported)
        {
            string best = null;
            if (!IsGenericOrBlank(friendly) && (best == null || friendly.Length > best.Length)) best = friendly;
            if (!IsGenericOrBlank(reported) && (best == null || reported.Length > best.Length)) best = reported;
            if (best != null) return best;
            if (!string.IsNullOrWhiteSpace(friendly)) return friendly;
            if (!string.IsNullOrWhiteSpace(reported)) return reported;
            return "";
        }

        private static bool IsGenericOrBlank(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            return GenericName.IsMatch(s);
        }

        /// <summary>
        /// 物理设备分组键。一个 USB 鼠标会拆出本体 + 各接口 + HID 集合，
        /// 靠 VID/PID 认亲；非 USB 设备退化成"去掉最后一段实例路径"。
        /// </summary>
        private static string DeviceGroupKey(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var m = GroupRx.Match(id);
            if (m.Success) return @"USB\" + m.Value.ToUpperInvariant();
            int i = id.LastIndexOf('\\');
            if (i > 0) return id.Substring(0, i);
            return id;
        }

        /// <summary>决定一组里拿哪个名字做标题：优先「鼠标」「键盘」，而不是「USB 输入设备」。</summary>
        private static int NameScore(string name, string cls)
        {
            int s = 0;
            string c = cls ?? "";
            if (RxClassMouse.IsMatch(c)) s += 40;
            if (RxClassKeyboard.IsMatch(c)) s += 30;
            if (RxClassDisk.IsMatch(c)) s += 25;
            if (RxClassCamera.IsMatch(c)) s += 25;
            if (RxClassAudio.IsMatch(c)) s += 20;
            if (RxClassNet.IsMatch(c)) s += 20;
            if (RxClassMonitor.IsMatch(c)) s += 15;

            if (string.IsNullOrWhiteSpace(name)) s -= 100;
            else
            {
                if (RxNameMouse.IsMatch(name)) s += 20;
                if (RxNameKeyboard.IsMatch(name)) s += 15;
                if (RxNameOther.IsMatch(name)) s += 15;
                if (GenericName.IsMatch(name)) s -= 40;   // 系统兜底名，让位给真实型号
                else s += 25;                             // 像真实型号的名字，优先显示
            }
            return s;
        }

        private static Pending BestOf(List<Pending> events)
        {
            Pending best = null;
            int bestScore = int.MinValue;
            foreach (var e in events)
            {
                int s = NameScore(e.Name, e.Class);
                if (best == null || s > bestScore) { best = e; bestScore = s; }   // 同分保留先出现的
            }
            return best;
        }

        /// <summary>匹配 Settings.FocusPatterns（PS 的 -like：整串匹配、不区分大小写）。</summary>
        private static bool IsFocus(Pending best)
        {
            var pats = Settings.I.FocusPatterns;
            if (pats == null || pats.Count == 0) return false;
            string hay = (best.Id ?? "") + " " + (best.Name ?? "") + " " + (best.Class ?? "");
            foreach (var p in pats)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (Wildcard(hay, p)) return true;
            }
            return false;
        }

        private static bool IsIgnoredDevice(string id, string name, string cls)
        {
            if (Settings.I.IgnoreSoftwareDevices)
            {
                if (StartsWith(id, "SW\\") || StartsWith(id, "SWD\\") || StartsWith(id, "ROOT\\")) return true;
            }
            return MatchesAnyPattern((id ?? "") + " " + (name ?? "") + " " + (cls ?? ""));
        }

        private static bool MatchesAnyPattern(string hay)
        {
            var pats = Settings.I.IgnorePatterns;
            if (pats == null || pats.Count == 0) return false;
            foreach (var p in pats)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (Wildcard(hay, p)) return true;
            }
            return false;
        }

        private static bool StartsWith(string s, string prefix)
        {
            return !string.IsNullOrEmpty(s) && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把 PowerShell 的 -like 通配符（* 和 ?）翻成正则：整串匹配、不区分大小写。</summary>
        private static bool Wildcard(string text, string pattern)
        {
            if (pattern == null) return false;
            var sb = new StringBuilder(pattern.Length + 8);
            sb.Append('^');
            foreach (char ch in pattern)
            {
                if (ch == '*') sb.Append(".*");
                else if (ch == '?') sb.Append('.');
                else sb.Append(Regex.Escape(ch.ToString()));
            }
            sb.Append('$');
            try { return Regex.IsMatch(text ?? "", sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline); }
            catch { return false; }        // 用户写的规则千奇百怪，坏规则不能把监测搞崩
        }

        // ====================================================================
        //  共享状态小工具
        // ====================================================================
        private static void RegisterKnown(string id, string name, string friendly, string cls, string enumerator, int problem, uint status, string reported)
        {
            lock (AppState.I.Sync)
            {
                DeviceEntry e;
                if (!AppState.I.Known.TryGetValue(id, out e))
                {
                    e = new DeviceEntry();
                    AppState.I.Known[id] = e;
                }
                e.Id = id;
                e.Name = name ?? "";
                e.Friendly = friendly ?? "";
                e.Desc = string.IsNullOrEmpty(friendly) ? (name ?? "") : friendly;
                e.Class = cls ?? "";
                e.Enumerator = enumerator ?? "";
                e.Problem = problem;
                e.Status = status;
                e.Reported = reported ?? "";
            }
        }

        /// <summary>断联排行：按物理设备累加（一次拔插算一次），也是网页「断联排行」卡片的数据源。</summary>
        private static void BumpGroupStat(string key, string name, bool off)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (AppState.I.Sync)
            {
                DeviceStat st;
                if (!AppState.I.Stats.TryGetValue(key, out st))
                {
                    st = new DeviceStat { Key = key };
                    AppState.I.Stats[key] = st;
                }
                if (!string.IsNullOrEmpty(name)) st.Name = name;
                if (off) st.Off++; else st.On++;
            }
        }

        private bool IsCooled(string key)
        {
            DateTime t;
            if (!_cooldown.TryGetValue(key, out t)) return false;
            return (DateTime.Now - t).TotalSeconds < Math.Max(0, Settings.I.NotifyCooldownSec);
        }

        private void SetCooldown(string key) { _cooldown[key] = DateTime.Now; }

        private static bool IsBenignProblem(int code) { return BenignProblemCodes.Contains(code); }

        private static string ProblemTextOf(int code)
        {
            if (code <= 0) return "正常";
            string t;
            if (ProblemText.TryGetValue(code, out t)) return t;
            return $"未知故障(代码 {code})";
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.Length <= 46 ? id : id.Substring(0, 43) + "...";
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static uint ParseUInt(string s)
        {
            uint v;
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static bool IsHardwareOnly()
        {
            return string.Equals(AppState.I.Mode, "Hardware", StringComparison.OrdinalIgnoreCase);
        }

        // ====================================================================
        //  消息泵（去抖等待期间保持托盘/网页响应）
        // ====================================================================
        [ThreadStatic] private static bool _pumping;

        private static void Pump()
        {
            if (_pumping) return;      // DoEvents 会重入消息循环，挡住递归
            _pumping = true;
            try
            {
                System.Windows.Forms.Application.DoEvents();
                // 主循环挂了钩子的话，顺便把网页请求也处理掉（PS 版的 Invoke-UiPump 就是这个顺序）
                Action hook = PumpHook;
                if (hook != null) hook();
            }
            catch { }
            finally { _pumping = false; }
        }

        /// <summary>分片等待。粒度保持 50ms —— 再粗托盘菜单就会明显发卡。</summary>
        private static void SleepPump(int totalMs)
        {
            int left = totalMs;
            while (left > 0)
            {
                Pump();
                int slice = left > 50 ? 50 : left;
                try { System.Threading.Thread.Sleep(slice); } catch { }
                left -= slice;
            }
        }
    }
}
