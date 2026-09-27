// ============================================================================
//  Settings.cs —— 配置项：默认值 / 读写 settings.json / 校验 / 网页 schema
//
//  设计要点（为什么这么写）：
//   1. 单一实例 Settings.I。采集线程、网页线程、托盘菜单都读同一份，
//      改一个值立刻全局生效，不需要事件通知。
//   2. 从 settings.json 读进来时，**只认识键名**、不认识的键原样存进 Extra，
//      写回去时合并输出。PowerShell 版就是这个行为（它把整个 Settings 哈希表
//      序列化回去），用户手工加的字段不能被我们吃掉。
//   3. 范围（min/max）只在 SchemaItem 里写一份，Apply 的校验和 /api/settings
//      的 schema 都从它取数，避免两处数值不一致。
//   4. 写出格式：一行一个键、带两空格缩进、中文不转义、UTF-8 with BOM。
//      这个文件是给用户手改的，可读性优先于体积。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeviceWatch
{
    public sealed class Settings
    {
        // ------------------------------------------------------------ 单例
        private static readonly Settings _i = new Settings();
        public static Settings I { get { return _i; } }
        private Settings() { }

        /// <summary>Load() / Save() 之间互斥：网页线程保存配置的同时主循环再读，不会读到写了一半的文件。</summary>
        private readonly object _sync = new object();

        // ============================================================ 字段
        // ---- 通知 ----
        public bool Notify = true;                  // 弹窗总开关
        public bool Sound = true;                   // 提示音
        public bool NotifyEventLog = true;          // 系统日志里的硬件错误也弹窗
        public string ToastDuration = "long";       // short / long
        public int NotifyCooldownSec = 8;           // 同一台设备这么多秒内只通知一次
        public int BusEventThreshold = 3;           // 同一窗口内多少台设备一起变化 → 合并成"总线事件"

        // ---- 采集 ----
        public int PollMs = 200;                    // 设备在线列表轮询间隔（枚举一次只要 0.06ms，调小几乎不花 CPU）
        public int DebounceMs = 200;                // 插拔去抖：USB 插拔会连触发多次
        public double CoalesceSec = 0.2;            // 变化合并窗口（秒）
        public int HwSampleMs = 2000;               // 硬件采样间隔
        public int LhmSampleSec = 8;                // 低层传感器（走驱动读）采样间隔
        public int NetCheckMs = 8000;               // 网卡 Up/Down 检查间隔
        public int EventLogCheckSec = 20;           // 系统日志硬件错误检查间隔
        public int FullScanSec = 90;                // 完整设备详情扫描间隔
        public bool WatchNetwork = true;            // 监控网卡状态
        public bool WatchEventLog = true;           // 监控系统日志
        public bool WatchInternet = true;           // 探测外网

        // ---- 网络 ----
        public List<string> PingTargets = new List<string> { "223.5.5.5", "119.29.29.29", "1.1.1.1" };
        public int InternetFailThreshold = 2;       // 连续失败几次算断网
        public int PingTimeoutMs = 1500;            // 单目标超时

        // ---- 网页 ----
        public bool WebEnabled = true;              // 网页面板（只监听 127.0.0.1）
        public int WebPort = 8787;                  // 端口（需重启）

        // ---- 界面 ----
        public bool ShowTray = true;                // 托盘图标
        public string HwMetric = "both";            // temp / load / both
        public string RunMode = "All";              // All / Device / Hardware

        // ---- 规则 ----
        public List<string> IgnorePatterns = new List<string>();    // 忽略规则（通配符）
        public List<string> FocusPatterns = new List<string>();     // 重点关注（通知加 ⭐）
        public bool IgnoreSoftwareDevices = false;  // 忽略 SW\ / SWD\ / ROOT\ 虚拟设备

        // ---- 日志 ----
        public int LogRetentionDays = 60;           // 0 = 永久保留
        public int MaxLogFileMB = 20;               // 单文件上限，超了切分

        /// <summary>
        /// schema 之外、但 settings.json 里存在的键（用户手工加的）。
        /// 写回时必须原样输出，否则会把用户的字段删掉。
        /// </summary>
        public readonly Dictionary<string, object> Extra = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>Extra 里键的原始出现顺序，写回时按这个顺序排，文件看起来和用户写的一致。</summary>
        private readonly List<string> _extraOrder = new List<string>();

        private bool _loaded;

        /// <summary>是否已经 Load 过一次（日志/网页在没有配置文件时也能拿到默认值）。</summary>
        public bool IsLoaded { get { return _loaded; } }

        // ------------------------------------------------------------ 路径
        /// <summary>exe 同目录。AppDomain.BaseDirectory 就是 exe 所在目录（不是当前工作目录）。</summary>
        public static string Root
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>settings.json 的完整路径：exe 同目录。</summary>
        public static string ConfigPath
        {
            get { return Path.Combine(Root, "settings.json"); }
        }

        // ============================================================ 加载
        /// <summary>
        /// 从 settings.json 读取。文件不存在 → 用默认值并把文件创建出来；
        /// 文件坏了（不是合法 JSON）→ 保留默认值、不覆盖用户的文件（免得把手工内容写没了）。
        ///
        /// **绝不抛异常**：Load() 在命令行解析之前就会被调用（要拿 WebPort 判断单实例），
        /// 这个时机挂掉等于程序起不来，所以任何意外都退化成"用默认值继续跑"。
        /// 可重复调用：Load() 和 Save() 之间用同一把锁串起来，网页线程保存配置的同时
        /// 主循环再 Load 也不会读到写了一半的文件。
        /// </summary>
        public void Load()
        {
            lock (_sync)
            {
                try { LoadCore(); }
                catch (Exception ex)
                {
                    // 连日志都不一定能写（Log 可能还没 Init），失败原因至少留给 debug 模式看
                    try { Log.Write("配置文件读取失败，改用默认配置：" + ex.Message, "Warn"); } catch { }
                }
                finally { _loaded = true; }
            }
        }

        private void LoadCore()
        {
            string path = ConfigPath;

            if (!File.Exists(path))
            {
                // 首次运行：把默认值落盘，用户打开就能看到所有可改的键
                try { SaveCore(); } catch { /* 目录只读等情况下不致命，内存里照样有默认值 */ }
                return;
            }

            Dictionary<string, object> obj = null;
            try
            {
                string text = ReadTextUtf8(path);
                obj = JsonParser.Parse(text) as Dictionary<string, object>;
            }
            catch
            {
                obj = null;   // 解析失败：全部用默认值
            }

            if (obj == null) return;

            // 逐个键套用；非法值（类型不对 / 超范围）直接忽略，保持默认值，
            // 这样用户手改坏了也只是那一项回到默认，不至于起不来。
            foreach (var kv in obj)
            {
                if (!IsKnown(kv.Key))
                {
                    Extra[kv.Key] = kv.Value;
                    _extraOrder.Add(kv.Key);
                }
            }
            ApplyFromObject(obj, strict: false);
        }

        /// <summary>读文本：优先按 UTF-8 带 BOM 解，没 BOM 时也按 UTF-8（用户手改过大概率还是 UTF-8）。</summary>
        private static string ReadTextUtf8(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>把 JSON 对象里的已知键套用到字段上。strict=true 时第一次出错就返回 false。</summary>
        private bool ApplyFromObject(Dictionary<string, object> obj, bool strict)
        {
            foreach (var it in Schema)
            {
                object raw;
                if (!obj.TryGetValue(it.Key, out raw)) continue;
                string err; bool restart;
                if (!Apply(it, raw, out err, out restart)) { if (strict) return false; }
            }
            return true;
        }

        // ============================================================ 保存
        /// <summary>
        /// 写回 settings.json（UTF-8 with BOM，一行一个键）。
        /// 网页 POST 和主循环可能同时碰这个函数，所以整段加锁；
        /// 先写临时文件再改名：中途被强杀也不会留下半个文件导致下次起不来。
        /// </summary>
        public void Save()
        {
            lock (_sync) { SaveCore(); }
        }

        private void SaveCore()
        {
            string path = ConfigPath;

            var root = new Dictionary<string, object>();
            foreach (var it in Schema) root[it.Key] = CurrentValue(it);

            // 不在 schema 里的键放在最后，保持它们原来的相对顺序
            foreach (var k in _extraOrder)
            {
                object v;
                if (Extra.TryGetValue(k, out v)) root[k] = v;
            }
            foreach (var kv in Extra)
            {
                if (!root.ContainsKey(kv.Key)) root[kv.Key] = kv.Value;
            }

            var w = new JsonWriter(true);           // pretty：两空格缩进 + 换行
            w.WriteValue(root);
            string json = w.ToString();

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 临时文件名带上 pid + 线程号：两个线程同时 Save 也不会互相踩同一个临时文件
            string tmp = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + "."
                       + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
            try
            {
                File.WriteAllText(tmp, json, new UTF8Encoding(true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        // ============================================================ 应用单项
        /// <summary>
        /// 应用单个设置项（网页 POST /api/settings、托盘菜单都用它）。
        /// 成功返回 true；失败返回 false 并把中文原因写进 error。
        /// needRestart 为 true 表示这一项改动要重启监控才生效。
        /// </summary>
        public bool Apply(string key, object value, out string error, out bool needRestart)
        {
            error = null;
            needRestart = false;

            if (string.IsNullOrEmpty(key)) { error = "设置项名字为空"; return false; }

            SchemaItem it = FindItem(key);
            if (it == null) { error = "未知的设置项：" + key; return false; }

            return Apply(it, value, out error, out needRestart);
        }

        private bool Apply(SchemaItem it, object value, out string error, out bool needRestart)
        {
            error = null;
            needRestart = false;

            // schema 项缺字段属于内部错误，直接当未知键处理，不让它把网页线程打崩
            if (it == null || string.IsNullOrEmpty(it.Key) || string.IsNullOrEmpty(it.Type))
            {
                error = "未知的设置项：" + (it == null ? "(空)" : it.Key);
                return false;
            }

            switch (it.Type)
            {
                case "bool":
                    {
                        bool v;
                        if (!ToBool(value, out v)) { error = it.Key + ": 需要 true 或 false"; return false; }
                        if (GetBool(it.Key) == v) return true;
                        SetBool(it.Key, v);
                        break;
                    }

                case "int":
                    {
                        double d;
                        if (!ToNumber(value, out d)) { error = it.Key + ": 需要一个整数"; return false; }
                        if (Math.Abs(d - Math.Round(d)) > 1e-9) { error = it.Key + ": 需要一个整数"; return false; }
                        if (d < it.Min || d > it.Max)
                        {
                            error = string.Format(CultureInfo.InvariantCulture,
                                "{0}: 数值超出范围，应在 {1} ~ {2} 之间", it.Key, Fmt(it.Min), Fmt(it.Max));
                            return false;
                        }
                        int iv = (int)Math.Round(d);
                        if (GetInt(it.Key) == iv) return true;
                        SetInt(it.Key, iv);
                        break;
                    }

                case "double":
                    {
                        double d;
                        if (!ToNumber(value, out d)) { error = it.Key + ": 需要一个数字"; return false; }
                        if (d < it.Min || d > it.Max)
                        {
                            error = string.Format(CultureInfo.InvariantCulture,
                                "{0}: 数值超出范围，应在 {1} ~ {2} 之间", it.Key, Fmt(it.Min), Fmt(it.Max));
                            return false;
                        }
                        if (Math.Abs(GetDouble(it.Key) - d) < 1e-9) return true;
                        SetDouble(it.Key, d);
                        break;
                    }

                case "enum":
                    {
                        string s = ToText(value);
                        if (s == null) { error = it.Key + ": 需要一个字符串"; return false; }
                        s = s.Trim();
                        bool ok = false;
                        foreach (var o in it.Opts) if (string.Equals(o, s, StringComparison.OrdinalIgnoreCase)) { s = o; ok = true; break; }
                        if (!ok)
                        {
                            error = string.Format("{0}: 只能是 {1}", it.Key, string.Join(" / ", it.Opts));
                            return false;
                        }
                        if (string.Equals(GetString(it.Key), s, StringComparison.Ordinal)) return true;
                        SetString(it.Key, s);
                        break;
                    }

                case "list":
                    {
                        if (value is string) { error = it.Key + ": 需要一个字符串数组，例如 [\"223.5.5.5\"]"; return false; }
                        var arr = value as System.Collections.IEnumerable;
                        if (arr == null) { error = it.Key + ": 需要一个字符串数组"; return false; }
                        var list = new List<string>();
                        foreach (var x in arr)
                        {
                            if (x == null) continue;
                            if (x is bool) continue;                       // 和 PowerShell 版一致：布尔值当噪声丢掉
                            string xs = ToText(x);
                            if (xs == null) continue;
                            xs = xs.Trim();
                            if (xs.Length > 0) list.Add(xs);
                        }
                        var cur = GetList(it.Key);
                        if (SameList(cur, list)) return true;
                        SetList(it.Key, list);
                        break;
                    }

                default:   // string
                    {
                        string s = ToText(value);
                        if (s == null) { error = it.Key + ": 需要一个字符串"; return false; }
                        if (string.Equals(GetString(it.Key), s, StringComparison.Ordinal)) return true;
                        SetString(it.Key, s);
                        break;
                    }
            }

            needRestart = it.Restart;   // 只有真的改动了才会走到这里
            return true;
        }

        // ------------------------------------------------------------ 类型转换
        private static bool ToBool(object v, out bool r)
        {
            r = false;
            if (v == null) return false;
            if (v is bool) { r = (bool)v; return true; }
            string s = v as string;
            if (s != null)
            {
                s = s.Trim();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) { r = true; return true; }
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) { return true; }
                if (s == "1") { r = true; return true; }
                if (s == "0") { return true; }
                return false;
            }
            if (v is double) { r = Math.Abs((double)v) > 1e-9; return true; }
            return false;   // 数组 / 对象不接受
        }

        private static bool ToNumber(object v, out double d)
        {
            d = 0;
            if (v == null) return false;
            if (v is bool) return false;
            if (v is double) { d = (double)v; return !double.IsNaN(d) && !double.IsInfinity(d); }
            if (v is float) { d = (float)v; return true; }
            if (v is decimal) { d = (double)(decimal)v; return true; }
            if (v is byte || v is sbyte || v is short || v is ushort ||
                v is int || v is uint || v is long || v is ulong)
            { d = Convert.ToDouble(v, CultureInfo.InvariantCulture); return true; }
            var s = v as string;
            if (s != null)
                return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return false;   // 数组 / 对象不接受
        }

        /// <summary>转成字符串；数组和对象返回 null，让调用方报错。</summary>
        private static string ToText(object v)
        {
            if (v == null) return null;
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is System.Collections.IEnumerable) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>范围提示里的数字：整数不带小数点，小数去掉多余的 0。</summary>
        private static string Fmt(double d)
        {
            if (Math.Abs(d - Math.Round(d)) < 1e-9) return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // ------------------------------------------------------------ 字段访问器
        // 用 switch 按字段名读写：Apply / BuildSchema 都只认键名，
        // 这样加一个设置项只需要改三处（字段、Schema 表、这两个 switch）。
        private static readonly string[] NoKeys = new string[0];

        private bool GetBool(string k)
        {
            switch (k)
            {
                case "Notify": return Notify;
                case "Sound": return Sound;
                case "NotifyEventLog": return NotifyEventLog;
                case "WatchNetwork": return WatchNetwork;
                case "WatchEventLog": return WatchEventLog;
                case "WatchInternet": return WatchInternet;
                case "WebEnabled": return WebEnabled;
                case "ShowTray": return ShowTray;
                case "IgnoreSoftwareDevices": return IgnoreSoftwareDevices;
            }
            return false;
        }

        private void SetBool(string k, bool v)
        {
            switch (k)
            {
                case "Notify": Notify = v; break;
                case "Sound": Sound = v; break;
                case "NotifyEventLog": NotifyEventLog = v; break;
                case "WatchNetwork": WatchNetwork = v; break;
                case "WatchEventLog": WatchEventLog = v; break;
                case "WatchInternet": WatchInternet = v; break;
                case "WebEnabled": WebEnabled = v; break;
                case "ShowTray": ShowTray = v; break;
                case "IgnoreSoftwareDevices": IgnoreSoftwareDevices = v; break;
            }
        }

        private int GetInt(string k)
        {
            switch (k)
            {
                case "NotifyCooldownSec": return NotifyCooldownSec;
                case "BusEventThreshold": return BusEventThreshold;
                case "PollMs": return PollMs;
                case "DebounceMs": return DebounceMs;
                case "HwSampleMs": return HwSampleMs;
                case "LhmSampleSec": return LhmSampleSec;
                case "NetCheckMs": return NetCheckMs;
                case "EventLogCheckSec": return EventLogCheckSec;
                case "FullScanSec": return FullScanSec;
                case "InternetFailThreshold": return InternetFailThreshold;
                case "PingTimeoutMs": return PingTimeoutMs;
                case "WebPort": return WebPort;
                case "LogRetentionDays": return LogRetentionDays;
                case "MaxLogFileMB": return MaxLogFileMB;
            }
            return 0;
        }

        private void SetInt(string k, int v)
        {
            switch (k)
            {
                case "NotifyCooldownSec": NotifyCooldownSec = v; break;
                case "BusEventThreshold": BusEventThreshold = v; break;
                case "PollMs": PollMs = v; break;
                case "DebounceMs": DebounceMs = v; break;
                case "HwSampleMs": HwSampleMs = v; break;
                case "LhmSampleSec": LhmSampleSec = v; break;
                case "NetCheckMs": NetCheckMs = v; break;
                case "EventLogCheckSec": EventLogCheckSec = v; break;
                case "FullScanSec": FullScanSec = v; break;
                case "InternetFailThreshold": InternetFailThreshold = v; break;
                case "PingTimeoutMs": PingTimeoutMs = v; break;
                case "WebPort": WebPort = v; break;
                case "LogRetentionDays": LogRetentionDays = v; break;
                case "MaxLogFileMB": MaxLogFileMB = v; break;
            }
        }

        private double GetDouble(string k)
        {
            if (k == "CoalesceSec") return CoalesceSec;
            return 0;
        }

        private void SetDouble(string k, double v)
        {
            if (k == "CoalesceSec") CoalesceSec = v;
        }

        private string GetString(string k)
        {
            switch (k)
            {
                case "ToastDuration": return ToastDuration;
                case "HwMetric": return HwMetric;
                case "RunMode": return RunMode;
            }
            return null;
        }

        private void SetString(string k, string v)
        {
            switch (k)
            {
                case "ToastDuration": ToastDuration = v; break;
                case "HwMetric": HwMetric = v; break;
                case "RunMode": RunMode = v; break;
            }
        }

        private List<string> GetList(string k)
        {
            switch (k)
            {
                case "PingTargets": return PingTargets;
                case "IgnorePatterns": return IgnorePatterns;
                case "FocusPatterns": return FocusPatterns;
            }
            return new List<string>();
        }

        private void SetList(string k, List<string> v)
        {
            switch (k)
            {
                case "PingTargets": PingTargets = v; break;
                case "IgnorePatterns": IgnorePatterns = v; break;
                case "FocusPatterns": FocusPatterns = v; break;
            }
        }

        // ============================================================ schema
        /// <summary>一个设置项的元数据。范围 / 取值只在这里写一份。</summary>
        private sealed class SchemaItem
        {
            public string Key;
            public string Type;          // bool / int / double / enum / string / list
            public string Label;
            public string Hint;
            public string Unit;
            public double Min;
            public double Max;
            public string[] Opts;
            public bool Restart;
            public string Group;

            public SchemaItem(string k, string t, string label, string hint, string unit,
                              double min, double max, string[] opts, bool restart, string group)
            {
                Key = k; Type = t; Label = label; Hint = hint; Unit = unit;
                Min = min; Max = max; Opts = opts; Restart = restart; Group = group;
            }
        }

        // 分组顺序固定，网页照着这个顺序渲染；schema 里多出来的分组排在后面。
        // 严格按 CONTRACT.md「设置项清单」表的分组列，共 7 组 30 项。
        private static readonly string[] GroupOrder = { "通知", "采集", "网络", "网页", "界面", "规则", "日志" };

        private static readonly SchemaItem[] Schema = new SchemaItem[]
        {
            // ---------------- 通知 ----------------
            new SchemaItem("Notify", "bool", "弹窗总开关",
                "关闭后设备插拔、断网、硬件错误都不再弹窗，只写日志；临时不想被打扰时关它最省事。",
                "", 0, 0, null, false, "通知"),
            new SchemaItem("Sound", "bool", "提示音",
                "弹窗时同时响一声系统提示音；开会或安静场合关掉，只留视觉提醒。",
                "", 0, 0, null, false, "通知"),
            new SchemaItem("NotifyEventLog", "bool", "日志错误弹窗",
                "系统日志里出现磁盘 / 硬件错误时弹窗；关掉后错误仍然会写进日志，只是不打扰你。",
                "", 0, 0, null, false, "通知"),
            new SchemaItem("ToastDuration", "enum", "弹窗时长",
                "系统通知在屏幕上停留的档位：short 约 5 秒就消失，不容易漏看时选 long 更稳妥。",
                "", 0, 0, new string[] { "short", "long" }, false, "通知"),
            new SchemaItem("NotifyCooldownSec", "int", "同设备冷却",
                "同一台设备在这个秒数内只弹一次窗。USB 设备本体和它的子接口常分两批变化，调大更省心；调太小会出现同一设备连弹好几条。",
                "秒", 1, 600, null, false, "通知"),
            new SchemaItem("CoalesceSec", "double", "变化合并窗口",
                "这段时间内攒下的设备变化合并成一次提醒。调大：一次插拔只弹一条，但提醒来得晚；设 0：立刻弹，但批量插拔会连弹很多条。",
                "秒", 0, 10, null, false, "通知"),
            new SchemaItem("BusEventThreshold", "int", "总线事件阈值",
                "同一时间窗内这么多台设备一起变化，就合并成一条「总线复位」提醒。调大：只有大规模掉线才合并；调小：几台设备一起变化就会被归成总线事件。",
                "台", 2, 50, null, false, "通知"),

            // ---------------- 采集 ----------------
            new SchemaItem("PollMs", "int", "设备轮询间隔",
                "每隔多少毫秒重新枚举一次在线设备。枚举一次约 0.06 毫秒，调小几乎不占 CPU，但插拔提醒更及时；调到 5000 会明显感觉延迟。",
                "毫秒", 100, 5000, null, false, "采集"),
            new SchemaItem("DebounceMs", "int", "插拔去抖",
                "设备出现或消失后先等这么久再确认，避免 USB 接触不良的抖动造成反复误报。调大更稳但提醒更慢；调 0 最灵敏，也最容易误报。",
                "毫秒", 0, 3000, null, false, "采集"),
            new SchemaItem("NetCheckMs", "int", "网卡检查间隔",
                "每隔多少毫秒检查一次网卡的 Up / Down 状态。调小能更快发现拔网线，调大省 CPU；600000 相当于十分钟才看一次。",
                "毫秒", 1000, 600000, null, false, "采集"),
            new SchemaItem("FullScanSec", "int", "完整扫描间隔",
                "每隔多少秒做一次全量设备详情扫描，用来发现「设备还在、但已经出故障」的情况。全量扫描较慢，调太小会一直占着 CPU。",
                "秒", 15, 3600, null, false, "采集"),
            new SchemaItem("EventLogCheckSec", "int", "日志检查间隔",
                "每隔多少秒读一次系统日志里的硬件错误。读日志本身不慢，但太频繁没意义；调大最多让错误提醒晚一点。",
                "秒", 5, 3600, null, false, "采集"),
            new SchemaItem("HwSampleMs", "int", "硬件采样间隔",
                "每隔多少毫秒采样一次温度、占用率、速率等硬件数据。调小曲线更细但更费 CPU；调大到 60000 基本只剩一个大盘走势。",
                "毫秒", 500, 60000, null, false, "采集"),
            new SchemaItem("LhmSampleSec", "int", "低层传感器间隔",
                "内存温度 / CPU 功耗 / GPU 热点这类要走驱动读的传感器，每隔多少秒读一次。它们变化慢，调大能明显省 CPU，但峰值温度可能被漏掉。",
                "秒", 2, 600, null, false, "采集"),
            new SchemaItem("HwMetric", "enum", "硬件主指标",
                "硬件面板上的大字显示哪个：temp 只显示温度、load 只显示占用率、both 两个都显示。",
                "", 0, 0, new string[] { "temp", "load", "both" }, false, "采集"),
            new SchemaItem("RunMode", "enum", "运行模式",
                "All 外设和硬件全监控；Device 只盯设备插拔、完全不采硬件数据最省资源；Hardware 只看温度占用、不做设备提醒。",
                "", 0, 0, new string[] { "All", "Device", "Hardware" }, false, "采集"),

            // ---------------- 网络 ----------------
            new SchemaItem("PingTargets", "list", "探测目标",
                "用来判断外网是否连通的 IP 列表，默认网关会自动加入、不用手写。目标越多判定越准；只写内网地址会导致断网时判定不出来。",
                "", 0, 0, null, false, "网络"),
            new SchemaItem("InternetFailThreshold", "int", "断网判定次数",
                "所有目标连续失败这么多次才判定为断网。调大可以避免路由器瞬断造成的误报，代价是真正断网时提醒晚几轮。",
                "次", 1, 20, null, false, "网络"),
            new SchemaItem("PingTimeoutMs", "int", "单目标超时",
                "Ping 一个目标最多等多少毫秒，超时就记为该目标失败。调大：网络慢时不容易误判断网，但每轮检查更耗时；调小：反应快，弱网下容易误报。",
                "毫秒", 200, 10000, null, false, "网络"),

            // ---------------- 网页 ----------------
            new SchemaItem("WebEnabled", "bool", "网页面板",
                "开启后可以用浏览器打开 http://127.0.0.1:端口 查看和控制监控，只监听本机、外网访问不到。关掉后不再占用端口。",
                "", 0, 0, null, true, "网页"),
            new SchemaItem("WebPort", "int", "网页端口",
                "网页面板监听的端口号，被别的程序占用时换一个即可；1024 以下需要管理员权限，所以不开放。",
                "端口", 1024, 65535, null, true, "网页"),

            // ---------------- 界面 ----------------
            new SchemaItem("ShowTray", "bool", "托盘图标",
                "是否在任务栏通知区域显示图标（右键可退出、可切模式）。关掉后只能靠网页面板或命令行 stop 停止监控。",
                "", 0, 0, null, true, "界面"),
            new SchemaItem("WatchNetwork", "bool", "监控网卡状态",
                "网卡 Up / Down 时提醒；关掉后仍然会读网卡速率，只是不弹提醒。",
                "", 0, 0, null, false, "界面"),
            new SchemaItem("WatchEventLog", "bool", "监控系统日志",
                "读取系统日志里的硬件错误并提醒；关闭后完全不读日志，也就不会有这类提醒。",
                "", 0, 0, null, false, "界面"),
            new SchemaItem("WatchInternet", "bool", "探测外网",
                "外网从通变断、或从断变通时提醒。关掉后不再 Ping 任何地址，也省掉一点网络开销。",
                "", 0, 0, null, false, "界面"),

            // ---------------- 规则 ----------------
            new SchemaItem("IgnorePatterns", "list", "忽略规则",
                "匹配这些通配符的设备完全不监控，例如 *WPDBUSENUM* 可以屏蔽手机 MTP 反复插拔的噪声。加错规则会让真设备漏报，谨慎添加。",
                "", 0, 0, null, false, "规则"),
            new SchemaItem("FocusPatterns", "list", "重点关注设备",
                "匹配这些通配符的设备通知前会加 ⭐ 并优先显示，例如 *VID_04A5&PID_8002*。用来盯住键鼠、加密狗这类关键外设。",
                "", 0, 0, null, false, "规则"),
            new SchemaItem("IgnoreSoftwareDevices", "bool", "忽略虚拟设备",
                "忽略 SW\\、SWD\\、ROOT\\ 开头的软件虚拟设备（各种虚拟声卡、虚拟网卡）。开启后设备数会少一大截，提醒也更干净。",
                "", 0, 0, null, false, "规则"),

            // ---------------- 日志 ----------------
            new SchemaItem("LogRetentionDays", "int", "日志保留天数",
                "超期的日志在启动时自动删除；空间紧张就调小，需要长期追溯就调大，设 0 表示永久保留（文件会一直堆积）。",
                "天", 0, 3650, null, false, "日志"),
            new SchemaItem("MaxLogFileMB", "int", "单文件体积上限",
                "单个日志文件超过这个大小就切成 DeviceWatch-日期.1.log。调小方便记事本打开，调大能少几个文件。",
                "MB", 1, 1024, null, false, "日志"),
        };

        private static SchemaItem FindItem(string key)
        {
            foreach (var it in Schema) if (string.Equals(it.Key, key, StringComparison.Ordinal)) return it;
            return null;
        }

        /// <summary>键名是否是本程序认识的设置项（Extra 只收不认识的键）。</summary>
        public static bool IsKnown(string key)
        {
            return FindItem(key) != null;
        }

        /// <summary>当前值，按 schema 类型归一化（list 一定是数组，bool 一定是布尔…）。</summary>
        private object CurrentValue(SchemaItem it)
        {
            switch (it.Type)
            {
                case "bool": return GetBool(it.Key);
                case "int": return GetInt(it.Key);
                case "double": return GetDouble(it.Key);
                case "list":
                    {
                        var src = GetList(it.Key);
                        var outList = new List<object>();
                        if (src != null) foreach (var s in src) if (s != null && s.Trim().Length > 0) outList.Add(s);
                        return outList;
                    }
                default: return GetString(it.Key);
            }
        }

        /// <summary>
        /// 生成 GET /api/settings 的响应体：
        /// {"ok":true,"groups":[{"name":"通知","items":[{k,t,label,hint,unit,v,min,max,opts,restart},…]},…]}
        /// 键名和顺序严格照 CONTRACT.md，前端直接照着渲染。
        /// </summary>
        public Dictionary<string, object> BuildSchema()
        {
            var bucket = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            var names = new List<string>();

            foreach (var it in Schema)
            {
                string g = string.IsNullOrEmpty(it.Group) ? "其他" : it.Group;
                List<object> list;
                if (!bucket.TryGetValue(g, out list))
                {
                    list = new List<object>();
                    bucket[g] = list;
                    names.Add(g);
                }

                object min = null, max = null, opts = null;
                if (it.Type == "int" || it.Type == "double") { min = it.Min; max = it.Max; }
                else if (it.Type == "enum") { opts = new List<object>(it.Opts); }

                var item = new Dictionary<string, object>();
                item["k"] = it.Key;
                item["t"] = it.Type;
                item["label"] = it.Label;
                item["hint"] = it.Hint;
                item["unit"] = it.Unit;
                item["v"] = CurrentValue(it);
                item["min"] = min;
                item["max"] = max;
                item["opts"] = opts;
                item["restart"] = it.Restart;
                list.Add(item);
            }

            // 固定分组顺序在前，schema 里多出来的分组按出现顺序接在后面
            var ordered = new List<string>();
            foreach (var g in GroupOrder) if (bucket.ContainsKey(g)) ordered.Add(g);
            foreach (var g in names) if (!ordered.Contains(g)) ordered.Add(g);

            var groups = new List<object>();
            foreach (var g in ordered)
            {
                var go = new Dictionary<string, object>();
                go["name"] = g;
                go["items"] = bucket[g];
                groups.Add(go);
            }

            var root = new Dictionary<string, object>();
            root["ok"] = true;
            root["groups"] = groups;
            return root;
        }
    }
}
