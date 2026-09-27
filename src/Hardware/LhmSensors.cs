// ============================================================================
//  LhmSensors.cs —— 低层传感器：内存条温度 / CPU 封装功耗 / GPU 核心热点与显存结温
//
//  数据来源：lib\LibreHardwareMonitorLib.dll（MPL-2.0，随工具分发）。
//  这几项读数必须走内核驱动（SMBus / MSR / NVAPI），只有管理员 + 该库能拿到，
//  所以它是 HwState 之外的一条“补充通道”：拿不到任何东西时整条通道静默关闭，
//  AppState.Ext 保持 null，绝不拖累设备监控、硬件监控、网页面板。
//
//  【为什么全程用反射，而不是引用 DLL / 用 dynamic】
//    1) 这个 DLL 不参与编译：它和 12 个依赖 DLL（HidSharp、System.IO.Ports、
//       System.Management、RAMSPDToolkit-NDD ...）都在运行时的 lib\ 目录里，
//       是“随包分发、按需加载”的，编译期根本没有类型可用；
//    2) net48 下 dynamic 还要额外带一个 Microsoft.CSharp.dll，而且失败方式不可控；
//       反射可以自己决定每一步的降级路径，也便于兼容以后的库版本；
//    3) 换 lib\ 里的 DLL 不需要重新编译本文件（对外接口只有 TryOpen/Read/Close）。
//
//  【为什么 AssemblyResolve 一定要加重入保护】
//    LibreHardwareMonitorLib.dll 在被加载时会去解析同目录的依赖 DLL，解析失败时
//    CLR 触发 AppDomain.AssemblyResolve；我们在这个事件里再调 Assembly.LoadFrom，
//    而这次加载又会（间接）触发同一个事件 —— 不拦就会无限递归。
//    StackOverflowException 在 .NET 里是不可捕获的，进程直接被干掉（PowerShell 版
//    最初就踩过这个坑）。所以用一个 HashSet 记“正在解析中的程序集名”：
//    进来先查、有就立刻返回 null 让 CLR 走正常失败路径，finally 里再移除。
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeviceWatch
{
    /// <summary>
    /// LibreHardwareMonitorLib 的反射封装。除了管理员权限，什么都不需要。
    /// 用法：
    ///   var lhm = new LhmSensors();
    ///   // 启动阶段放后台线程：Open() 实测 5~6 秒（还要加载内核驱动），绝不能进主循环
    ///   if (lhm.TryOpen()) Log.Write("低层传感器已启用");
    ///   // 采样线程里按 LhmSampleSec 调用，未打开时它是空操作
    ///   lhm.Read();
    ///   // 退出前
    ///   lhm.Close();
    /// </summary>
    public sealed class LhmSensors
    {
        private const string LibDllName = "LibreHardwareMonitorLib.dll";
        private const string ComputerTypeName = "LibreHardwareMonitor.Hardware.Computer";
        private const string IHardwareTypeName = "LibreHardwareMonitor.Hardware.IHardware";
        private const string ISensorTypeName = "LibreHardwareMonitor.Hardware.ISensor";

        // ------------------------------------------------------------------
        //  一、程序集解析（lib\ 里的 13 个 DLL 互相依赖，运行时按需加载）
        // ------------------------------------------------------------------
        private static readonly object ResolveLock = new object();

        /// <summary>正在解析中的程序集简单名（重入保护，见文件头说明）。</summary>
        private static readonly HashSet<string> Resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string _libDir;
        private static int _resolverAttached;   // 0/1：事件只挂一次，避免重复挂导致重复解析

        /// <summary>挂上 AssemblyResolve（进程级、只挂一次），并记下 lib\ 目录。</summary>
        private static void AttachResolver(string libDir)
        {
            _libDir = libDir;
            if (Interlocked.Exchange(ref _resolverAttached, 1) == 1) return;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLib;
        }

        private static Assembly ResolveFromLib(object sender, ResolveEventArgs e)
        {
            try
            {
                string simple = new AssemblyName(e.Name).Name;
                if (string.IsNullOrEmpty(simple)) return null;

                // 已经加载过的直接复用：省一次磁盘探测，也顺手挡掉一部分重入
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase)) return a;
                }

                // 重入保护：同名程序集正在解析中 = 这是我们自己触发的嵌套解析，
                // 直接放弃（返回 null），绝不能再递归进去一次。
                lock (ResolveLock)
                {
                    if (Resolving.Contains(simple)) return null;
                    Resolving.Add(simple);
                }
                try
                {
                    string dir = _libDir;
                    if (string.IsNullOrEmpty(dir)) return null;
                    string path = Path.Combine(dir, simple + ".dll");
                    if (!File.Exists(path)) return null;
                    return Assembly.LoadFrom(path);          // 注意：在锁外加载，锁只保护集合
                }
                finally
                {
                    lock (ResolveLock) { Resolving.Remove(simple); }
                }
            }
            catch
            {
                // 解析失败必须安静地返回 null，让 CLR 走它自己的失败路径
                return null;
            }
        }

        /// <summary>从 exe 所在目录逐级向上找 lib\LibreHardwareMonitorLib.dll（打包后 lib\ 与 exe 同级）。</summary>
        private static string FindLibDir()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    string cand = Path.Combine(dir, "lib");
                    if (File.Exists(Path.Combine(cand, LibDllName))) return cand;
                    DirectoryInfo up = Directory.GetParent(dir);
                    if (up == null) break;
                    dir = up.FullName;
                }
            }
            catch { }
            return null;
        }

        private static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        //  二、状态
        // ------------------------------------------------------------------
        private readonly object _sync = new object();
        private object _computer;          // non-null = 已 Open
        private bool _tried;               // 尝试过就不再试：Open() 要 5~6 秒，不能被主循环反复触发

        /// <summary>最近一次成功 Open() 的耗时（毫秒）。给日志/看门狗看。</summary>
        public int LastOpenMs;

        /// <summary>库是否已经打开。</summary>
        public bool IsOpened { get { lock (_sync) { return _computer != null; } } }

        // 反射出来的成员句柄，Open() 时缓存一次，之后每次 Read 直接用
        private PropertyInfo _pHwList;      // Computer.Hardware
        private PropertyInfo _pSub;         // IHardware.SubHardware
        private PropertyInfo _pSensors;     // IHardware.Sensors
        private PropertyInfo _pHwName;      // IHardware.Name
        private PropertyInfo _pHwType;      // IHardware.HardwareType
        private MethodInfo _mUpdate;        // IHardware.Update()
        private PropertyInfo _pSensorName;  // ISensor.Name
        private PropertyInfo _pSensorType;  // ISensor.SensorType
        private PropertyInfo _pSensorValue; // ISensor.Value（float?）

        // ------------------------------------------------------------------
        //  三、打开 / 读取 / 关闭
        // ------------------------------------------------------------------

        /// <summary>
        /// 尝试加载并打开低层传感器库。成功返回 true。需要管理员权限。
        /// 【会阻塞 5~6 秒】必须放在后台线程或启动阶段调用。
        /// </summary>
        public bool TryOpen()
        {
            lock (_sync)
            {
                if (_computer != null) return true;   // 已经开着
                if (_tried) return false;             // 失败过就不要再试（见 _tried 注释）
                _tried = true;
            }

            Stopwatch sw = Stopwatch.StartNew();
            object computer = null;
            try
            {
                string libDir = FindLibDir();
                if (libDir == null)
                {
                    Log.Write("低层传感器未启用：找不到 lib\\" + LibDllName);
                    return false;
                }

                // 非管理员时驱动加载不了，直接放弃（也避免白白等 5 秒）
                if (!IsAdmin())
                {
                    Log.Write("低层传感器未启用：当前不是管理员，内核驱动加载不了");
                    return false;
                }

                AttachResolver(libDir);

                Assembly asm = Assembly.LoadFrom(Path.Combine(libDir, LibDllName));
                Type computerType = asm.GetType(ComputerTypeName, false);
                if (computerType == null)
                {
                    Log.Write("低层传感器未启用：DLL 里找不到类型 " + ComputerTypeName);
                    return false;
                }

                CacheMembers(asm, computerType);

                computer = Activator.CreateInstance(computerType);

                // 只开需要的类别。笔记本的 EC（主板/控制器）不暴露，开了白占内存
                // （实测 7MB 且 0 个传感器），所以显式关掉。
                SetBool(computerType, computer, "IsCpuEnabled", true);          // CPU 温度、封装功耗
                SetBool(computerType, computer, "IsMemoryEnabled", true);       // 内存条温度（SPD）
                SetBool(computerType, computer, "IsGpuEnabled", true);          // GPU 热点、显存结温
                SetBool(computerType, computer, "IsMotherboardEnabled", false);
                SetBool(computerType, computer, "IsControllerEnabled", false);

                MethodInfo open = computerType.GetMethod("Open", Type.EmptyTypes);
                if (open == null)
                {
                    Log.Write("低层传感器未启用：" + ComputerTypeName + " 上没有 Open()");
                    return false;
                }

                open.Invoke(computer, null);   // ← 慢在这里：加载内核驱动 + 枚举所有总线

                // 一个硬件都没有说明驱动没起来（库内部把每个组的异常都吞了，只能这样判断）
                if (CountOf(Prop(computer, _pHwList, "Hardware")) == 0)
                {
                    Log.Write("低层传感器未启用：驱动未加载成功（枚举到 0 个硬件）");
                    SafeClose(computer);
                    computer = null;
                    return false;
                }

                LastOpenMs = (int)sw.ElapsedMilliseconds;
                lock (_sync) { _computer = computer; }
                Log.Write(string.Format("低层传感器已启用（内存条温度 / CPU 封装功耗 / GPU 热点与显存结温，耗时 {0} ms）", LastOpenMs));
                return true;
            }
            catch (Exception ex)
            {
                // 任何异常都吞掉：低层传感器是可选功能，绝不能影响其它监控
                SafeClose(computer);
                Log.Write("低层传感器初始化失败：" + Unwrap(ex).Message);
                return false;
            }
        }

        /// <summary>读一次所有传感器，更新 AppState.I.Ext。未打开时直接返回。</summary>
        public void Read()
        {
            object computer = _computer;
            if (computer == null) return;   // 没打开（非管理员 / 没 lib）：什么都不做

            ExtSensors ext = new ExtSensors();
            try
            {
                foreach (object hw in Each(Prop(computer, _pHwList, "Hardware")))
                {
                    Collect(hw, ext);                                        // 主硬件
                    foreach (object sub in Each(Prop(hw, _pSub, "SubHardware")))
                        Collect(sub, ext);                                   // 子硬件（核心温度常在这里）
                }
            }
            catch (Exception ex)
            {
                Log.Write("低层传感器读取失败：" + Unwrap(ex).Message);
            }

            // 时间戳和来源照写：即使这一轮什么也没读到，前端也要知道“最后一次采样是什么时候”
            ext.At = DateTime.Now.ToString("HH:mm:ss");
            ext.Source = "admin";
            AppState.I.Ext = ext;
            AppState.I.ExtSource = "admin";
        }

        /// <summary>关闭并释放。多次调用安全。</summary>
        public void Close()
        {
            object computer;
            lock (_sync)
            {
                computer = _computer;
                _computer = null;
            }
            if (computer == null) return;
            try
            {
                MethodInfo close = computer.GetType().GetMethod("Close", Type.EmptyTypes);
                if (close != null) close.Invoke(computer, null);
            }
            catch (Exception ex)
            {
                Log.Write("低层传感器关闭失败：" + Unwrap(ex).Message);
            }
        }

        // ------------------------------------------------------------------
        //  四、传感器归类
        // ------------------------------------------------------------------

        /// <summary>一个硬件（或子硬件）的传感器快照。</summary>
        private sealed class SensorItem
        {
            public string Name;
            public string Type;    // Temperature / Power / Voltage / Fan ...
            public double Value;
        }

        /// <summary>读一个硬件上的所有传感器并归类到 ExtSensors。</summary>
        private void Collect(object hw, ExtSensors ext)
        {
            // 【必须先 Update()】库不会自己刷新，不调拿到的是上一次的缓存值
            try
            {
                MethodInfo up = _mUpdate;
                if (up == null) up = hw.GetType().GetMethod("Update", Type.EmptyTypes);
                if (up != null) up.Invoke(hw, null);
            }
            catch { }   // 单个硬件读失败不影响其它硬件

            string ht = Text(Prop(hw, _pHwType, "HardwareType"));    // Cpu / Memory / GpuNvidia / GpuAmd ...
            string hwName = Text(Prop(hw, _pHwName, "Name"));

            // 先把这一层的传感器取出来：GPU 要先知道“有没有 Hot Spot”，再决定收不收 GPU Core
            List<SensorItem> items = new List<SensorItem>();
            foreach (object s in Each(Prop(hw, _pSensors, "Sensors")))
            {
                if (s == null) continue;
                object raw = Prop(s, _pSensorValue, "Value");
                if (raw == null) continue;                     // 传感器没激活 / 这一轮读不到
                double v;
                try { v = Convert.ToDouble(raw, CultureInfo.InvariantCulture); }
                catch { continue; }
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;

                SensorItem it = new SensorItem();
                it.Name = Text(Prop(s, _pSensorName, "Name"));
                it.Type = Text(Prop(s, _pSensorType, "SensorType"));
                it.Value = v;
                items.Add(it);
            }

            // 这块 GPU 有没有真正的热点传感器
            bool gpuHasHotSpot = false;
            if (IsGpu(ht))
            {
                foreach (SensorItem it in items)
                {
                    if (it.Type == "Temperature" && it.Name.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gpuHasHotSpot = true;
                        break;
                    }
                }
            }

            foreach (SensorItem it in items)
            {
                switch (it.Type)
                {
                    case "Temperature":
                        // 名字里带这些词的是“分辨率 / 上下限 / 临界值 / 到 TjMax 的距离”，不是实时温度
                        if (it.Value <= 0 || JunkRe.IsMatch(it.Name)) continue;

                        if (ht == "Memory")
                        {
                            Add(ext.MemoryTemps, DimmName(it.Name, hwName, ext.MemoryTemps.Count), it.Value);
                        }
                        else if (ht == "Cpu")
                        {
                            AddCn(ext.CpuTemps, it.Name, it.Value);
                        }
                        else if (IsGpu(ht))
                        {
                            // GPU 只要 nvidia-smi / WMI 给不了的那几个（核心热点、显存结温）。
                            // 有 Hot Spot 时丢掉 "GPU Core"：它等于 hw.GpuTempC，而且两者都会被
                            // 翻成“核心热点”，留着就会出现两行同名不同值的条目。
                            if (gpuHasHotSpot && string.Equals(it.Name, "GPU Core", StringComparison.OrdinalIgnoreCase)) continue;
                            AddCn(ext.GpuTemps, it.Name, it.Value);
                        }
                        break;

                    case "Fan":
                        if (it.Value > 0) AddCn(ext.Fans, it.Name, it.Value);
                        break;

                    case "Voltage":
                        // 每个核都有一份 VID，只取第一个当“CPU 核心电压”；GPU 只要 Core Voltage。
                        // 电压传感器动辄几十个，全收会把面板刷满没意义的数。
                        if (ht == "Cpu" && ext.Volts.Count == 0 && IsCpuCoreVoltage(it.Name))
                            Add(ext.Volts, "CPU 核心电压", it.Value);
                        else if (IsGpu(ht) && it.Name.IndexOf("Core Voltage", StringComparison.OrdinalIgnoreCase) >= 0)
                            Add(ext.Volts, "GPU 核心电压", it.Value);
                        break;

                    case "Power":
                        // 只要 CPU 的整包功耗。必须限定 ht == "Cpu"：GPU 也有个叫 "GPU Package"
                        // 的功耗项，不限定就会被当成 CPU 功耗混进来。
                        if (ht == "Cpu" && it.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                            AddCn(ext.Power, it.Name, it.Value);
                        break;
                }
            }
        }

        private static bool IsGpu(string hardwareType)
        {
            return hardwareType != null && hardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>CPU 核心电压：VID（AMD 每核 / Intel 每核）或整包电压，取第一个命中的。</summary>
        private static bool IsCpuCoreVoltage(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("VID", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("SVI2", StringComparison.OrdinalIgnoreCase) >= 0) return true;   // AMD Zen 的 SVI2 遥测
            if (name.IndexOf("Vcore", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return string.Equals(name.Trim(), "CPU Core Voltage", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>同名传感器只留第一条（例如两根内存条都报 "DIMM #0" 这种异常情况）。</summary>
        private static void Add(List<SensorReading> list, string name, double value)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (SensorReading r in list)
                if (string.Equals(r.Name, name, StringComparison.Ordinal)) return;
            list.Add(new SensorReading(name, Math.Round(value, 1)));   // 与 PowerShell 版一致：保留 1 位小数
        }

        private static void AddCn(List<SensorReading> list, string rawName, double value)
        {
            string cn;
            if (!TryCnName(rawName, out cn)) return;   // 命中“跳过”名单
            Add(list, cn, value);
        }

        // ------------------------------------------------------------------
        //  五、中文名翻译（对齐 PowerShell 版 Get-CnSensorName）
        // ------------------------------------------------------------------

        /// <summary>温度传感器里要排掉的名字：不是实时温度。</summary>
        private static readonly Regex JunkRe = new Regex(
            @"Resolution|Limit|Critical|Warning|Distance to TjMax",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex DimmRe = new Regex(@"^\s*DIMM\s*#?\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ParenSlotRe = new Regex(@"\(#\s*(\d+)\s*\)", RegexOptions.CultureInvariant);
        private static readonly Regex CpuCoreRe = new Regex(@"^(?:CPU\s+Core|Core)\s*#\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FanRe = new Regex(@"^Fan\s*#?\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CcdRe = new Regex(@"^CCD\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        /// <summary>AMD 的温度名带 " (Tctl/Tdie)" 之类后缀，查表前先去掉。</summary>
        private static readonly Regex SuffixRe = new Regex(@"\s*\((?:Tctl/Tdie|Tctl|Tdie)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>明确的“不要”名单：这些不是内存条温度。</summary>
        private static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Total Memory", "Virtual Memory"
        };

        /// <summary>精确名表。先查表，再按模式匹配，最后原样返回 —— 宁可显示英文，也不显示猜错的翻译。</summary>
        private static readonly Dictionary<string, string> NameMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- CPU 温度 ---
            { "CPU Package",            "CPU 封装" },
            { "Package",                "CPU 封装" },
            { "CPU Core",               "CPU 核心" },
            { "Core",                   "CPU 核心" },
            { "CPU Cores",              "CPU 核心" },
            { "Core (Tctl/Tdie)",       "CPU 核心" },
            { "Core (Tctl)",            "CPU 核心" },
            { "Core (Tdie)",            "CPU 核心" },
            { "Core Max",               "核心最高" },
            { "Core Average",           "核心平均" },
            { "CPU Core Max",           "核心最高" },
            { "CPU Core Average",       "核心平均" },
            { "CCDs Max (Tdie)",        "CCD 最高" },
            { "CCDs Average (Tdie)",    "CCD 平均" },
            { "CCD1 (Tdie)",            "CCD1" },
            { "CCD2 (Tdie)",            "CCD2" },
            { "CCD3 (Tdie)",            "CCD3" },
            // --- GPU ---
            { "GPU Core",               "核心热点" },
            { "GPU Hot Spot",           "核心热点" },
            { "GPU Memory Junction",    "显存结温" },
            { "GPU Memory",             "显存" },
            { "GPU Fan",                "GPU 风扇" },
            { "GPU VRM",                "显卡供电" },
            { "GPU Core Voltage",       "GPU 核心电压" },
            // --- 电压 ---
            { "CPU Core Voltage",       "CPU 核心电压" },
            { "CPU VDDCR_SOC",          "SoC 电压" },
            // --- 风扇 / 主板 ---
            { "CPU Fan",                "CPU 风扇" },
            { "System Fan",             "机箱风扇" },
            { "VRM",                    "供电模块" },
            { "VRM MOS",                "供电模块" },
            { "Chipset",                "芯片组" },
            { "Motherboard",            "主板" },
            { "System",                 "主板" },
            { "Ambient",                "机箱环境" },
            { "Memory",                 "内存" },
            { "Temperature",            "温度" },
            { "Bus Speed",              "总线频率" },
            { "CPU IOD Hotspot",        "IOD 热点" },
            { "CPU SoC",                "SoC" }
        };

        /// <summary>
        /// 英文传感器名 → 中文。返回 false 表示“这个传感器不要”。
        /// 顺序：跳过名单 → 精确表 → 去后缀再查表 → 模式匹配 → 原样保留。
        /// </summary>
        private static bool TryCnName(string raw, out string cn)
        {
            cn = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Trim();
            if (SkipNames.Contains(s)) return false;

            string hit;
            if (NameMap.TryGetValue(s, out hit)) { cn = hit; return true; }

            string bare = SuffixRe.Replace(s, string.Empty).Trim();
            if (bare.Length > 0 && !string.Equals(bare, s, StringComparison.Ordinal) && NameMap.TryGetValue(bare, out hit))
            {
                cn = hit;
                return true;
            }

            Match m = DimmRe.Match(s);                       // DIMM #0 / DIMM 0
            if (m.Success) { cn = "内存条 " + Slot1(m.Groups[1].Value); return true; }

            m = CpuCoreRe.Match(s);                          // CPU Core #3 / Core #3
            if (m.Success) { cn = "核心 " + m.Groups[1].Value; return true; }

            m = FanRe.Match(s);                              // Fan #1 / Fan 1
            if (m.Success) { cn = "风扇 " + m.Groups[1].Value; return true; }

            m = CcdRe.Match(s);                              // CCD1 (Tdie) / CCD1
            if (m.Success) { cn = "CCD" + m.Groups[1].Value; return true; }

            if (s.StartsWith("PCH", StringComparison.Ordinal)) { cn = "芯片组"; return true; }
            if (s.StartsWith("GPU", StringComparison.Ordinal)) { cn = ("显卡 " + s.Substring(3)).Trim(); return true; }
            if (s.StartsWith("CPU", StringComparison.Ordinal)) { cn = ("CPU " + s.Substring(3)).Trim(); return true; }

            cn = s;                                          // 认不出来就照原样显示
            return true;
        }

        /// <summary>槽位号 0 基 → 用户看到的 1 基编号（"内存条 1" / "内存条 2"）。</summary>
        private static string Slot1(string digits)
        {
            int idx;
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) idx = 0;
            return (idx + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 内存条命名。库里的传感器叫 "DIMM #0"，硬件名形如
        /// "Crucial Technology - CT16G56C46S5.M8G1 (#0)"，两种都带槽位号；
        /// 都取不到时按枚举顺序编号，保证不会出现两条同名。
        /// </summary>
        private static string DimmName(string sensorName, string hwName, int ordinal)
        {
            int idx = SlotIndex(sensorName);
            if (idx < 0) idx = SlotIndex(hwName);
            if (idx < 0) idx = ordinal;
            return "内存条 " + (idx + 1).ToString(CultureInfo.InvariantCulture);
        }

        private static int SlotIndex(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            Match m = DimmRe.Match(s);                       // DIMM #0
            if (!m.Success) m = ParenSlotRe.Match(s);        // ... CT16G56C46S5.M8G1 (#0)
            if (!m.Success) return -1;
            int idx;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return -1;
            return idx;
        }

        // ------------------------------------------------------------------
        //  六、反射小工具
        // ------------------------------------------------------------------

        private void CacheMembers(Assembly asm, Type computerType)
        {
            Type tHw = asm.GetType(IHardwareTypeName, false);
            Type tSensor = asm.GetType(ISensorTypeName, false);

            try { _pHwList = computerType.GetProperty("Hardware"); } catch { }

            if (tHw != null)
            {
                _pSub = tHw.GetProperty("SubHardware");
                _pSensors = tHw.GetProperty("Sensors");
                _pHwName = tHw.GetProperty("Name");
                _pHwType = tHw.GetProperty("HardwareType");
                _mUpdate = tHw.GetMethod("Update", Type.EmptyTypes);
            }
            if (tSensor != null)
            {
                _pSensorName = tSensor.GetProperty("Name");
                _pSensorType = tSensor.GetProperty("SensorType");
                _pSensorValue = tSensor.GetProperty("Value");
            }
        }

        private static void SetBool(Type type, object obj, string property, bool value)
        {
            try
            {
                PropertyInfo p = type.GetProperty(property);
                if (p != null && p.CanWrite) p.SetValue(obj, value, null);
            }
            catch { }   // 库版本不同少一个开关不算错
        }

        /// <summary>取属性值；缓存句柄缺失时（换库版本）退回按名字现查。</summary>
        private static object Prop(object obj, PropertyInfo cached, string name)
        {
            if (obj == null) return null;
            try
            {
                PropertyInfo p = cached;
                if (p == null) p = obj.GetType().GetProperty(name);
                return p == null ? null : p.GetValue(obj, null);
            }
            catch { return null; }
        }

        private static readonly object[] NoItems = new object[0];

        private static IEnumerable Each(object o)
        {
            return (o as IEnumerable) ?? NoItems;
        }

        private static int CountOf(object o)
        {
            ICollection c = o as ICollection;
            if (c != null) return c.Count;
            int n = 0;
            foreach (object x in Each(o)) n++;
            return n;
        }

        private static string Text(object o)
        {
            return o == null ? string.Empty : o.ToString();
        }

        /// <summary>反射调用抛的是 TargetInvocationException，日志里要打里层那句。</summary>
        private static Exception Unwrap(Exception ex)
        {
            TargetInvocationException tie = ex as TargetInvocationException;
            return (tie != null && tie.InnerException != null) ? tie.InnerException : ex;
        }

        private static void SafeClose(object computer)
        {
            if (computer == null) return;
            try
            {
                MethodInfo close = computer.GetType().GetMethod("Close", Type.EmptyTypes);
                if (close != null) close.Invoke(computer, null);
            }
            catch { }
        }
    }
}
