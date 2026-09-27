// ============================================================================
//  HardwareMonitor.cs —— 硬件采集层
//
//  调用方式（和 PowerShell 版的 Initialize-Hardware / Update-Hardware 一一对应）：
//      hw.Initialize();      // 只调一次：缓存静态信息 + 建立基准点 + 起后台 GPU 线程
//      while (...) { hw.Update(); Thread.Sleep(Settings.I.HwSampleMs); }
//
//  为什么这么写（都是踩过的坑）：
//   1. Update() 每 2 秒跑一次，且和主循环同一个线程 —— 它一慢，托盘菜单和网页
//      HTTP 都会卡。所以：
//        - WMI 查询器（ManagementObjectSearcher）在 Initialize 里建好反复用
//          （新建一次约 20ms，复用约 0.7ms，差 28 倍）；
//        - nvidia-smi 放到独立后台线程，绝不在采样里同步等进程（一次 60~90ms）；
//        - 不在这里读文件、不查 Win32_Processor 整类（整类查询会连带计算
//          LoadPercentage，本机实测固定 1130ms；投影查询只要 10ms）。
//   2. 每一项单独 try-catch：某个 WMI 类不存在 / 无权限 / 计数器被禁用，
//      只让那一项保持原值或 null，绝不连累其它项，更不把异常抛回主循环。
//   3. 列表字段（Disks / DiskIo / MemModules）一律"新建 List 再整体赋值"，
//      不原地 Clear+Add —— 网页线程此刻正在序列化 JSON，引用赋值是原子的，
//      它读到的要么是旧表、要么是新表，不会是半张表。
//   4. 所有速率都是 **KB/s**（不是累计字节），单位与网页契约一致。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeviceWatch
{
    public sealed class HardwareMonitor
    {
        // ------------------------------------------------------------ 常量
        const double GB = 1073741824.0;      // 1024^3
        const double KB = 1024.0;

        const int GpuIntervalMs = 1500;      // GPU 采样间隔：nvidia-smi 单次要 60~90ms，给足间隔别把 CPU 吃满
        const int GpuTimeoutMs = 5000;       // 单次 nvidia-smi 超时（独显在 D3 省电态被唤醒时会慢）
        const int NicRefreshMs = 30000;      // 网卡列表重建周期（热插拔 / 休眠唤醒后新网卡要能进来）
        const int TzRetryMs = 60000;         // CPU 温度常驻查询器坏掉后的重建冷却

        const string GpuArgs =
            "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,power.draw," +
            "power.limit,clocks.gr,clocks.mem,fan.speed,pstate --format=csv,noheader,nounits";

        static readonly Regex LeadingNumber = new Regex(@"^(\d+)", RegexOptions.Compiled);
        static readonly Regex DeviceIdValue = new Regex("DeviceID\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex PhysicalDrive = new Regex(@"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        bool _inited;

        // ---- CPU 占用基准点 ----
        bool _cpuBase;
        ulong _cpuIdle, _cpuTotal, _cpuUser;

        // ---- CPU 实时频率 ----
        PerformanceCounter _cpuPerf;                  // Processor Information\% Processor Performance\_Total
        bool _useWmiCpuFreq;                          // 上面建不起来（非英文系统）→ 走 WMI
        ManagementObjectSearcher _cpuFreqSe;          // Win32_PerfFormattedData_Counters_ProcessorInformation
        ManagementObjectSearcher _cpuClockSe;         // Win32_Processor.CurrentClockSpeed（最后兜底）

        // ---- CPU 温度 ----
        ManagementObjectSearcher _tzSe;               // Win32_PerfFormattedData_Counters_ThermalZoneInformation
        int _tzRetryTick;

        // ---- 磁盘 IO ----
        PerformanceCounter _diskReadSe, _diskWriteSe; // PhysicalDisk\_Total
        readonly Dictionary<int, IoPair> _diskIoCounters = new Dictionary<int, IoPair>();
        int[] _diskIoOrder = new int[0];              // 缓存好的盘号顺序（避免每次采样都排序）
        bool _useWmiDiskIo;
        ManagementObjectSearcher _wmiDiskIoSe;

        // ---- 磁盘空间：盘符 → 所属物理磁盘 ----
        readonly Dictionary<string, DiskOwner> _diskMap = new Dictionary<string, DiskOwner>(StringComparer.OrdinalIgnoreCase);

        // ---- 网络 ----
        List<NetworkInterface> _nics = new List<NetworkInterface>();
        readonly Dictionary<string, NicBytes> _nicPrev = new Dictionary<string, NicBytes>(StringComparer.OrdinalIgnoreCase);
        Stopwatch _sw;
        double _netStamp = -1;
        int _nicRefreshTick;

        // ---- GPU ----
        string _nvidia;                    // nvidia-smi 全路径；null = 没有 N 卡
        volatile GpuSample _gpu;           // 后台线程写，Update() 只读
        Thread _gpuThread;

        // ================================================================== 初始化

        /// <summary>只调用一次：缓存静态信息、建立基准点、起后台 GPU 线程。</summary>
        public void Initialize()
        {
            if (_inited) return;   // 重复调用（模式来回切）不重建常驻对象，否则 WMI / 计数器句柄会堆积
            _inited = true;

            try { _sw = Stopwatch.StartNew(); } catch { }

            InitCpuStatic();       // WMI：型号 / 核心 / 线程 / 基频 / 缓存（投影查询，只查一次）
            InitMemModules();      // WMI：内存条明细（不会热插拔，读一次就够）
            InitDiskMap();         // WMI：盘符 → 物理磁盘号 / 型号
            InitPerfCounters();    // 常驻性能计数器（磁盘 IO + CPU 频率）
            InitThermalZone();     // CPU 温度的常驻 WMI 查询器
            InitNetwork();         // Up 网卡列表 + 累计字节基准
            InitGpu();             // nvidia-smi 路径 + 后台采样线程（先同步取一次型号）
            InitCpuBase();         // GetSystemTimes 基准点（放最后，尽量贴近第一次采样）

            try { Update(); } catch { }   // 先跑一次，网页第一帧就有数据（和 PowerShell 版一致）
        }

        // ---- NVMe SMART 也走后台线程 ----
        // 为什么：DeviceIoControl 单独测只要 15ms，但在主循环里实测每 30 秒制造一次
        // 300ms 以上的界面阻塞（WMI/内核态偶尔会卡）。硬盘温度几分钟才变一度，
        // 用后台线程 10 秒读一次、主循环只读缓存完全够用。
        volatile List<DriveSmartInfo> _smartCached;
        int _smartTicks;
        static Thread _smartThread;

        void StartDriveSmartThread()
        {
            if (_smartThread != null) return;
            _smartThread = new Thread(SmartLoop);
            _smartThread.IsBackground = true;
            _smartThread.Name = "NvmeSmart";
            _smartThread.Start();
        }

        void SmartLoop()
        {
            while (true)
            {
                try
                {
                    var seen = new HashSet<int>();
                    var list = new List<DriveSmartInfo>();
                    foreach (var v in AppState.I.Hw.Disks)
                    {
                        if (v.Pnum < 0 || !seen.Add(v.Pnum)) continue;
                        var info = NvmeSmart.Read(v.Pnum);
                        if (info != null) list.Add(info);
                    }
                    if (list.Count > 0) { _smartCached = list; _smartTicks = Environment.TickCount; }
                }
                catch { }
                Thread.Sleep(10000);
            }
        }

        /// <summary>发布后台线程最近一次读到的 SMART。超过 1 分钟没更新就当读不到。</summary>
        void UpdateDriveSmart(HwState hw)
        {
            if (_smartThread == null) StartDriveSmartThread();
            if (_smartTicks == 0) return;                                  // 还没出第一次结果
            if (Environment.TickCount - _smartTicks > 60000) return;
            var c = _smartCached;
            if (c != null) hw.DriveSmart = c;
        }

        /// <summary>所有非回环适配器的 Up/Down 状态（面板的「网络」卡片要用）。</summary>
        void UpdateAdapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var a = new AdapterInfo();
                        a.Name = ni.Name ?? "";
                        bool up = ni.OperationalStatus == OperationalStatus.Up;
                        a.State = up ? "Up" : "Down";
                        // 只有 Up 的才显示速率，Down 的留空（和 PowerShell 版一致）
                        long bps = 0;
                        try { bps = ni.Speed; } catch { }
                        a.Speed = (up && bps > 0) ? FormatSpeed(bps) : "";
                        list.Add(a);
                    }
                    catch { }
                }
            }
            catch { }
            AppState.I.Adapters = list;
        }

        /// <summary>把 bit/s 变成 "2.5 Gbps" 这种人看的形式。</summary>
        static string FormatSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0) return "";
            double g = bitsPerSecond / 1e9;
            if (g >= 1) return (g >= 10 ? Math.Round(g).ToString(CultureInfo.InvariantCulture) : g.ToString("0.#", CultureInfo.InvariantCulture)) + " Gbps";
            double m = bitsPerSecond / 1e6;
            if (m >= 1) return Math.Round(m).ToString(CultureInfo.InvariantCulture) + " Mbps";
            return Math.Round(bitsPerSecond / 1e3).ToString(CultureInfo.InvariantCulture) + " Kbps";
        }

        /// <summary>子项之间泵一次消息（接到主循环的泵上）。</summary>
        static void PumpBetween()
        {
            var h = DeviceMonitor.PumpHook;
            if (h != null) { try { h(); } catch { } }
        }

        /// <summary>采样一次，把结果写进 AppState.I.Hw。</summary>
        public void Update()
        {
            HwState hw = AppState.I.Hw;
            if (hw == null) return;   // 还没初始化：静默返回，不抛异常

            // 阶段名给"界面卡顿看门狗"用：卡住时能看出停在采集的哪一段（和 PowerShell 版同名）
            AppState.I.CurrentPhase = "采样/CPU占用";
            try { UpdateCpu(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/内存";
            try { UpdateMemory(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/CPU温度";
            try { UpdateCpuTemp(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/GPU";
            try { UpdateGpu(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/CPU频率";
            try { UpdateCpuFreq(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/分盘IO";
            try { UpdateDiskIo(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/网络";
            try { UpdateNetwork(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/磁盘空间";
            try { UpdateDisks(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/电池";
            try { UpdatePower(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            AppState.I.CurrentPhase = "采样/开机时长";
            try { UpdateUptime(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            // NVMe SMART 每 10 秒读一次就够（每次要走 DeviceIoControl），
            // 硬盘温度和寿命变化很慢，读太勤没意义还费 IO。
            AppState.I.CurrentPhase = "采样/硬盘SMART";
            try { UpdateDriveSmart(hw); } catch { }
            // 子项之间泵一次：界面保持响应，看门狗也能精确指到是哪一个子项卡住
            PumpBetween();
            // 适配器列表要包含 Down 的（面板上要显示哪些网卡是断的），
            // 所以不能复用只统计 Up 网卡速率的那份列表。
            AppState.I.CurrentPhase = "采样/网络适配器";
            try { UpdateAdapters(); } catch { }

            try { hw.LastUpdate = DateTime.Now.ToString("HH:mm:ss"); } catch { }
        }

        // ================================================================== 初始化分步

        /// <summary>CPU 型号 / 核心 / 线程 / 基频 / 缓存。只读一次。</summary>
        void InitCpuStatic()
        {
            // 关键：只 SELECT 需要的列。整类查询会让 WMI 连带计算 LoadPercentage，
            // 本机实测固定 ~1130ms；投影查询只要 ~10ms，取到的值一模一样。
            const string q = "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, " +
                             "CurrentClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor";
            try
            {
                HwState hw = AppState.I.Hw;
                using (var se = new ManagementObjectSearcher("root\\CIMV2", q))
                using (ManagementObjectCollection rows = se.Get())
                {
                    foreach (ManagementBaseObject mo in rows)
                    {
                        string name = Str(mo["Name"]);
                        if (name.Length > 0) hw.CpuName = name;

                        int cores = Int(mo["NumberOfCores"]);
                        if (cores > 0) hw.CpuCores = cores;

                        int threads = Int(mo["NumberOfLogicalProcessors"]);
                        if (threads > 0) hw.CpuThreads = threads;

                        int maxMHz = Int(mo["MaxClockSpeed"]);
                        if (maxMHz > 0) hw.CpuMaxMHz = maxMHz;

                        // 初始值；真正的实时频率每次采样由 UpdateCpuFreq 刷新
                        // （AMD 上这个字段永远返回基频，只能用百分比 × 基频算）
                        int curMHz = Int(mo["CurrentClockSpeed"]);
                        if (curMHz > 0) hw.CpuCurMHz = curMHz;

                        // WMI 的 L2CacheSize / L3CacheSize 单位是 **KB**，契约要 MB
                        int l2 = Int(mo["L2CacheSize"]);
                        if (l2 > 0) hw.CpuL2MB = (int)Math.Round(l2 / 1024.0);

                        int l3 = Int(mo["L3CacheSize"]);
                        if (l3 > 0) hw.CpuL3MB = (int)Math.Round(l3 / 1024.0);

                        break;   // 多路 CPU 只取第一颗（和 PowerShell 版的 Select -First 1 一致）
                    }
                }
            }
            catch (Exception ex) { LogMsg("读取 Win32_Processor 失败：" + ex.Message); }
        }

        /// <summary>内存条明细。内存条不会热插拔，只在初始化时读一次。</summary>
        void InitMemModules()
        {
            try
            {
                var list = new List<MemModuleInfo>();
                const string q = "SELECT PartNumber, Manufacturer, Capacity, Speed, ConfiguredClockSpeed, " +
                                 "DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory";
                using (var se = new ManagementObjectSearcher("root\\CIMV2", q))
                using (ManagementObjectCollection rows = se.Get())
                {
                    foreach (ManagementBaseObject mo in rows)
                    {
                        var m = new MemModuleInfo();
                        m.Part = Str(mo["PartNumber"]);                        // WMI 会给一堆尾部空格，Str 里已 Trim
                        m.Mfg = Str(mo["Manufacturer"]);
                        m.SizeGB = (int)Math.Round(ToDouble(mo["Capacity"]) / GB);
                        m.Speed = Int(mo["ConfiguredClockSpeed"]);             // 实际运行频率
                        m.Rated = Int(mo["Speed"]);                            // 标称频率
                        m.Ddr = Int(mo["SMBIOSMemoryType"]);                   // 26=DDR4 34=DDR5
                        m.Slot = Str(mo["DeviceLocator"]);
                        list.Add(m);
                    }
                }
                if (list.Count > 0) AppState.I.Hw.MemModules = list;
            }
            catch (Exception ex) { LogMsg("读取 Win32_PhysicalMemory 失败：" + ex.Message); }
        }

        /// <summary>盘符 → 物理磁盘号 / 型号。</summary>
        void InitDiskMap()
        {
            // 这台机器是 2 块 NVMe 分了 3 个区，必须标清每个区属于哪块盘，
            // 否则界面上会误以为"有 3 块硬盘"；而且 Windows 的性能计数器、NVMe SMART
            // 都是按物理盘编号（pnum）来的，标对了才能把容量 / 温度 / IO 拼到同一张卡片上。
            try
            {
                // (1) 物理盘：Index（0/1…）→ 型号；DeviceID（\\.\PHYSICALDRIVE0）→ Index
                var modelByIndex = new Dictionary<int, string>();
                var indexByDevice = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                using (var se = new ManagementObjectSearcher("root\\CIMV2", "SELECT Index, Model, DeviceID FROM Win32_DiskDrive"))
                using (ManagementObjectCollection rows = se.Get())
                {
                    foreach (ManagementBaseObject mo in rows)
                    {
                        int idx = Int(mo["Index"]);
                        string dev = Str(mo["DeviceID"]);
                        modelByIndex[idx] = Str(mo["Model"]);
                        if (dev.Length > 0) indexByDevice[dev] = idx;
                    }
                }

                // (2) 分区 → 物理磁盘号。
                //     主路：Win32_DiskDriveToDiskPartition 关联（Antecedent=物理盘，Dependent=分区），
                //           从 \\.\PHYSICALDRIVE0 里抠盘号 —— 和 PowerShell 版走同一条路；
                //     兜底：Win32_DiskPartition 自己就带 DiskIndex，一次查询拿全，
                //           关联类被安全软件拦掉时仍然能用。
                var diskByPartition = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (var se = new ManagementObjectSearcher("root\\CIMV2", "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
                    using (ManagementObjectCollection rows = se.Get())
                    {
                        foreach (ManagementBaseObject mo in rows)
                        {
                            string part = DeviceIdIn(RefText(mo["Dependent"]));
                            string diskDev = DeviceIdIn(RefText(mo["Antecedent"]));
                            if (part.Length == 0 || diskDev.Length == 0) continue;

                            int idx;
                            if (indexByDevice.TryGetValue(diskDev, out idx)) diskByPartition[part] = idx;
                            else
                            {
                                // 关联里的 DeviceID 万一没带盘号，直接从字符串里抠
                                int pn = PhysicalDriveNumber(diskDev);
                                if (pn >= 0) diskByPartition[part] = pn;
                            }
                        }
                    }
                }
                catch { }
                if (diskByPartition.Count == 0)
                {
                    using (var se = new ManagementObjectSearcher("root\\CIMV2", "SELECT DeviceID, DiskIndex FROM Win32_DiskPartition"))
                    using (ManagementObjectCollection rows = se.Get())
                    {
                        foreach (ManagementBaseObject mo in rows)
                        {
                            string part = Str(mo["DeviceID"]);
                            if (part.Length > 0) diskByPartition[part] = Int(mo["DiskIndex"]);
                        }
                    }
                }

                // (3) 盘符 → 分区：Win32_LogicalDiskToPartition（Antecedent=分区，Dependent=逻辑盘）
                using (var se = new ManagementObjectSearcher("root\\CIMV2", "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                using (ManagementObjectCollection rows = se.Get())
                {
                    foreach (ManagementBaseObject mo in rows)
                    {
                        string letter = DeviceIdIn(RefText(mo["Dependent"]));    // "C:"
                        string part = DeviceIdIn(RefText(mo["Antecedent"]));     // "Disk #0, Partition #2"
                        if (letter.Length == 0 || part.Length == 0) continue;

                        int idx;
                        if (!diskByPartition.TryGetValue(part, out idx)) continue;

                        string model;
                        if (!modelByIndex.TryGetValue(idx, out model)) model = "";
                        _diskMap[letter] = new DiskOwner(idx, model);
                    }
                }
            }
            catch (Exception ex) { LogMsg("建立盘符→物理磁盘映射失败：" + ex.Message); }
        }

        /// <summary>常驻性能计数器：每块物理盘的读写速率 + 整机合计 + CPU 实时频率。</summary>
        void InitPerfCounters()
        {
            // 注意：PerformanceCounter 用的是**英文类别名**，而类别名在非英文 Windows 上是
            // 本地化的（德语 Prozessorinformationen、日语 プロセッサ情報），英文名会直接抛异常。
            // 所以每一项单独判断，建不起来就退回 WMI —— WMI 的性能类名永远是英文。
            try
            {
                _diskReadSe = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                _diskReadSe.NextValue();   // 速率计数器第一次必为 0，先建立基准
            }
            catch { DisposeQuietly(_diskReadSe); _diskReadSe = null; }

            try
            {
                _diskWriteSe = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                _diskWriteSe.NextValue();
            }
            catch { DisposeQuietly(_diskWriteSe); _diskWriteSe = null; }

            // 每块物理盘各自的速率。PhysicalDisk 的实例名形如 "0 C: D:" / "1 E:"，
            // 开头的数字就是物理磁盘号，正好和 NVMe SMART 的 pnum、分区归属对得上。
            try
            {
                var cat = new PerformanceCounterCategory("PhysicalDisk");
                foreach (string ins in cat.GetInstanceNames())
                {
                    if (ins == "_Total") continue;
                    Match m = LeadingNumber.Match(ins);
                    if (!m.Success) continue;

                    int pnum = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (_diskIoCounters.ContainsKey(pnum)) continue;

                    var rd = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", ins, true);
                    var wr = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", ins, true);
                    rd.NextValue();
                    wr.NextValue();
                    _diskIoCounters[pnum] = new IoPair(rd, wr);
                }
            }
            catch
            {
                foreach (KeyValuePair<int, IoPair> kv in _diskIoCounters) kv.Value.Dispose();
                _diskIoCounters.Clear();
            }
            // 盘号顺序缓存下来：采样时不用再排序，也不产生垃圾
            var order = new List<int>(_diskIoCounters.Keys);
            order.Sort();
            _diskIoOrder = order.ToArray();

            // CPU 实时频率：用「% Processor Performance」× 基频算出来，免管理员。
            // Win32_Processor.CurrentClockSpeed 在 AMD 上永远返回基频（本机恒为 2401），
            // 拿它当实时频率是错的 —— 只留作最后兜底。
            try
            {
                _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                _cpuPerf.NextValue();
            }
            catch { DisposeQuietly(_cpuPerf); _cpuPerf = null; }

            if (_cpuPerf == null)
            {
                _useWmiCpuFreq = true;
                LogMsg("性能计数器「% Processor Performance」不可用（多半是非英文系统），CPU 实时频率改用 WMI 读取");
            }
            if (_diskReadSe == null || _diskWriteSe == null || _diskIoCounters.Count == 0)
            {
                _useWmiDiskIo = true;
                LogMsg("性能计数器「PhysicalDisk」不可用（多半是非英文系统），磁盘读写速率改用 WMI 读取");
            }
        }

        /// <summary>建立（或重建）CPU 温度用的常驻 WMI 查询器。</summary>
        void InitThermalZone()
        {
            DisposeQuietly(_tzSe);
            _tzSe = null;
            try
            {
                // 有的机器没有这个类（或被安全软件禁用），建不起来就保持 null，采样时温度给 null。
                var se = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
                using (ManagementObjectCollection probe = se.Get()) { }   // 预热：类不存在在这里就抛，别留到热路径
                _tzSe = se;
            }
            catch { }
        }

        /// <summary>网卡列表 + 累计字节基准。</summary>
        void InitNetwork()
        {
            _nics = new List<NetworkInterface>();
            _nicPrev.Clear();
            RefreshNicList(false);
            _nicRefreshTick = Environment.TickCount;
            _netStamp = _sw != null ? _sw.Elapsed.TotalSeconds : -1;
        }

        /// <summary>找 nvidia-smi、同步取一次型号，然后交给后台线程持续采样。</summary>
        void InitGpu()
        {
            // 两个常见位置都找一遍；找不到（非 N 卡 / 没装驱动）就完全不启用，
            // GPU 相关字段全部留 null，不报错、不影响别的采集项。
            try
            {
                string root = Environment.GetEnvironmentVariable("SystemRoot");
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string[] cands = new string[]
                {
                    string.IsNullOrEmpty(root) ? null : Path.Combine(root, @"System32\nvidia-smi.exe"),
                    string.IsNullOrEmpty(pf) ? null : Path.Combine(pf, @"NVIDIA Corporation\NVSMI\nvidia-smi.exe"),
                    string.IsNullOrEmpty(pf86) ? null : Path.Combine(pf86, @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")
                };
                foreach (string c in cands)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    try { if (File.Exists(c)) { _nvidia = c; break; } } catch { }
                }
            }
            catch { }

            if (_nvidia == null) return;   // 非 N 卡：GPU 字段保持 null

            // 初始化时同步取一次（最多等 GpuTimeoutMs），保证网页第一帧就有显卡型号和温度
            try
            {
                GpuSample first = ParseGpu(RunNvidiaSmi());
                if (first != null) _gpu = first;
            }
            catch { }

            // 之后交给后台线程：nvidia-smi 一次要 60~90ms，同步等会卡住主循环
            // （主循环还负责泵托盘消息和网页 HTTP 请求，那正是托盘卡顿的根源）。
            try
            {
                if (_gpuThread == null)
                {
                    _gpuThread = new Thread(GpuLoop);
                    _gpuThread.IsBackground = true;   // 后台线程：退出进程时不会被它拖住
                    _gpuThread.Name = "DeviceWatch-Gpu";
                    _gpuThread.Start();
                }
            }
            catch { }
        }

        /// <summary>GetSystemTimes 基准点。之后每次采样都是"和上一次比"。</summary>
        void InitCpuBase()
        {
            try
            {
                ulong idle, kernel, user;
                if (HwNative.GetCpuTimes(out idle, out kernel, out user))
                {
                    _cpuIdle = idle;
                    _cpuUser = user;
                    _cpuTotal = kernel + user;   // kernel 时间里含 idle
                    _cpuBase = true;
                }
            }
            catch { }
        }

        // ================================================================== 采样分步

        /// <summary>CPU 占用：两次采样的时间差（% 用户态 / 内核态）。</summary>
        void UpdateCpu(HwState hw)
        {
            ulong idle, kernel, user;
            if (!HwNative.GetCpuTimes(out idle, out kernel, out user)) return;

            ulong total = kernel + user;
            if (_cpuBase)
            {
                double dIdle = (double)idle - (double)_cpuIdle;
                double dTotal = (double)total - (double)_cpuTotal;
                double dUser = (double)user - (double)_cpuUser;

                if (dTotal > 0)
                {
                    // kernel 时间里含 idle，所以"忙" = total - idle
                    double busy = ((dTotal - dIdle) / dTotal) * 100.0;
                    double up = (dUser / dTotal) * 100.0;
                    double kp = ((dTotal - dIdle - dUser) / dTotal) * 100.0;

                    hw.CpuPct = Math.Round(Clamp(busy), 1);
                    hw.CpuUserPct = Math.Round(Clamp(up), 1);
                    hw.CpuKernelPct = (int)Math.Round(Clamp(kp));
                }
            }

            // 不管这次算没算出来，基准点都要前移（否则下次差值会跨两个间隔）
            _cpuIdle = idle;
            _cpuTotal = total;
            _cpuUser = user;
            _cpuBase = true;
        }

        /// <summary>物理内存 / 提交量。</summary>
        void UpdateMemory(HwState hw)
        {
            ulong total, avail, commit, commitMax;
            if (!HwNative.GetMemory(out total, out avail, out commit, out commitMax)) return;
            if (total == 0) return;

            double totGB = total / GB;
            double availGB = avail / GB;
            double usedGB = totGB - availGB;

            hw.MemTotalGB = Math.Round(totGB, 1);
            hw.MemUsedGB = Math.Round(usedGB, 1);
            hw.MemFreeGB = Math.Round(availGB, 1);
            hw.MemPct = Math.Round(usedGB / totGB * 100.0, 1);

            if (commitMax > 0)
            {
                hw.MemCommitMaxGB = Math.Round(commitMax / GB, 1);
                hw.MemCommitGB = Math.Round(commit / GB, 1);
            }
        }

        /// <summary>CPU 温度：取所有热区的最大值。复用常驻查询器（新建一次要 20ms）。</summary>
        /// <summary>真正去查 WMI 的那一次（会阻塞）。只由后台线程调用。</summary>
        double? QueryCpuTempBlocking()
        {
            // 常驻查询器坏掉（WMI 服务重启 / 类被禁用）时隔一分钟重建一次，
            // 重建太频繁会退化成"每次采样都 new"，那正是要避免的 20ms
            if (_tzSe == null)
            {
                int now = Environment.TickCount;
                if (now - _tzRetryTick > TzRetryMs)
                {
                    _tzRetryTick = now;
                    InitThermalZone();
                }
            }
            if (_tzSe == null) { return null; }

            double? maxTenthK = null;
            try
            {
                using (ManagementObjectCollection rows = _tzSe.Get())
                {
                    foreach (ManagementBaseObject z in rows)
                    {
                        object raw = z["HighPrecisionTemperature"];
                        if (raw == null) continue;
                        double v = ToDouble(raw);
                        if (v <= 0) continue;                                   // 有的热区给 0，忽略
                        if (!maxTenthK.HasValue || v > maxTenthK.Value) maxTenthK = v;
                    }
                }
            }
            catch
            {
                DisposeQuietly(_tzSe);
                _tzSe = null;
                return null;
            }

            if (!maxTenthK.HasValue) { return null; }

            // 这个类给的是"十分之一开尔文"：3430 → 343.0K → 69.85°C
            double c = (maxTenthK.Value / 10.0) - 273.15;
            return (c > -30 && c < 150) ? (double?)Math.Round(c, 1) : null;   // 明显是乱码值就不要
        }

        // ---- CPU 温度改成后台线程异步查 ----
        // 为什么：WMI 热区查询平均 1ms，但 WMI 服务一抖动就会飙到 500ms 以上。
        // 实测它稳定地每 30~90 秒制造一次 550ms 的界面阻塞 —— 放在热路径里不可接受。
        // 温度变化很慢，用后台线程 2 秒查一次、主循环只读缓存，完全够用。
        double? _tzCached;
        int _tzTicks;
        static Thread _tzThread;

        void StartCpuTempThread()
        {
            if (_tzThread != null) return;
            _tzThread = new Thread(TzLoop);
            _tzThread.IsBackground = true;
            _tzThread.Name = "CpuTemp";
            _tzThread.Start();
        }

        void TzLoop()
        {
            while (true)
            {
                int period = 2000;
                try { double? v = QueryCpuTempBlocking(); _tzCached = v; _tzTicks = Environment.TickCount; } catch { }
                Thread.Sleep(period);
            }
        }

        /// <summary>发布后台线程最近一次的温度。超过 15 秒没更新就当作读不到。</summary>
        void UpdateCpuTemp(HwState hw)
        {
            if (_tzThread == null) StartCpuTempThread();
            if (_tzTicks == 0) return;                                  // 还没出第一次结果，保留上一次的显示
            if (Environment.TickCount - _tzTicks > 15000) { hw.CpuTempC = null; return; }
            hw.CpuTempC = _tzCached;
        }

        /// <summary>把后台线程最近一次采样结果发布到 HwState。没有 N 卡时什么都不做。</summary>
        void UpdateGpu(HwState hw)
        {
            GpuSample g = _gpu;          // volatile 读：要么上一份，要么最新一份，不会是半个对象
            if (g == null) return;

            if (!string.IsNullOrEmpty(g.Name)) hw.GpuName = g.Name;
            hw.GpuTempC = g.TempC;
            hw.GpuUtilPct = g.UtilPct;
            hw.GpuMemPct = g.MemPct;
            hw.GpuMemUsedMB = g.MemUsedMB;
            hw.GpuMemTotalMB = g.MemTotalMB;
            hw.GpuPowerW = g.PowerW;
            hw.GpuPowerMaxW = g.PowerMaxW;
            hw.GpuClockMHz = g.ClockMHz;
            hw.GpuMemClockMHz = g.MemClockMHz;
            hw.GpuFanPct = g.FanPct;
            hw.GpuPstate = g.Pstate;
        }

        /// <summary>CPU 实时频率。</summary>
        void UpdateCpuFreq(HwState hw)
        {
            if (hw.CpuMaxMHz <= 0) return;

            // (1) 常驻性能计数器「% Processor Performance」：最便宜（约 0.3ms）也最准。
            if (_cpuPerf != null)
            {
                try
                {
                    double pp = _cpuPerf.NextValue();
                    if (pp > 0)
                    {
                        hw.CpuCurMHz = (int)Math.Round(hw.CpuMaxMHz * pp / 100.0);
                        return;
                    }
                }
                catch
                {
                    DisposeQuietly(_cpuPerf);
                    _cpuPerf = null;
                    _useWmiCpuFreq = true;
                }
            }

            if (!_useWmiCpuFreq) return;

            // (2) WMI 后备（非英文系统上计数器类别名对不上时走这里）。
            //     WMI 的性能类名永远是英文，不受系统语言影响。
            try
            {
                if (_cpuFreqSe == null)
                {
                    _cpuFreqSe = new ManagementObjectSearcher("root\\CIMV2",
                        "SELECT PercentProcessorPerformance, ProcessorFrequency FROM " +
                        "Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
                }
                using (ManagementObjectCollection rows = _cpuFreqSe.Get())
                {
                    foreach (ManagementBaseObject mo in rows)
                    {
                        double pp = ToDouble(mo["PercentProcessorPerformance"]);
                        if (pp > 0)
                        {
                            hw.CpuCurMHz = (int)Math.Round(hw.CpuMaxMHz * pp / 100.0);
                            return;
                        }
                        // ProcessorFrequency 其实也是标称基频（本机 2401），只在拿不到百分比时凑合用
                        double freq = ToDouble(mo["ProcessorFrequency"]);
                        if (freq > 0)
                        {
                            hw.CpuCurMHz = (int)Math.Round(freq);
                            return;
                        }
                    }
                }
            }
            catch { DisposeQuietly(_cpuFreqSe); _cpuFreqSe = null; }

            // (3) 最后兜底：Win32_Processor.CurrentClockSpeed
            //     （AMD 上等于基频，但至少不会在界面上留个 0）
            try
            {
                if (_cpuClockSe == null)
                {
                    _cpuClockSe = new ManagementObjectSearcher("root\\CIMV2",
                        "SELECT CurrentClockSpeed FROM Win32_Processor");
                }
                using (ManagementObjectCollection rows = _cpuClockSe.Get())
                {
                    foreach (ManagementBaseObject mo in rows)
                    {
                        double freq = ToDouble(mo["CurrentClockSpeed"]);
                        if (freq > 0)
                        {
                            hw.CpuCurMHz = (int)Math.Round(freq);
                            return;
                        }
                    }
                }
            }
            catch { DisposeQuietly(_cpuClockSe); _cpuClockSe = null; }
        }

        /// <summary>每块物理磁盘的读写速率 + 整机合计（都是 KB/s）。</summary>
        void UpdateDiskIo(HwState hw)
        {
            if (_useWmiDiskIo) { UpdateDiskIoFromWmi(hw); return; }

            try
            {
                if (_diskIoOrder.Length > 0)
                {
                    var list = new List<DiskIoInfo>(_diskIoOrder.Length);
                    foreach (int pnum in _diskIoOrder)
                    {
                        IoPair pair;
                        if (!_diskIoCounters.TryGetValue(pnum, out pair)) continue;

                        double rd = 0, wr = 0;
                        try { rd = pair.Read.NextValue() / KB; } catch { }    // NextValue 自己算 (本次-上次)/间隔
                        try { wr = pair.Write.NextValue() / KB; } catch { }
                        if (rd < 0) rd = 0;   // 计数器重启 / 实例重建会出现负值
                        if (wr < 0) wr = 0;

                        var io = new DiskIoInfo();
                        io.Pnum = pnum;
                        io.ReadKB = Math.Round(rd, 1);
                        io.WriteKB = Math.Round(wr, 1);
                        list.Add(io);
                    }
                    if (list.Count > 0) hw.DiskIo = list;   // 整体替换：网页线程不会读到半张表
                }
            }
            catch { }

            // 整机合计：_Total 计数器（单次 0.3~1ms）
            try
            {
                if (_diskReadSe != null)
                {
                    double v = _diskReadSe.NextValue() / KB;
                    hw.DiskReadKB = Math.Round(v < 0 ? 0 : v, 1);
                }
            }
            catch { }
            try
            {
                if (_diskWriteSe != null)
                {
                    double v = _diskWriteSe.NextValue() / KB;
                    hw.DiskWriteKB = Math.Round(v < 0 ? 0 : v, 1);
                }
            }
            catch { }
        }

        /// <summary>性能计数器不可用时的退路：Win32_PerfFormattedData_PerfDisk_PhysicalDisk。</summary>
        void UpdateDiskIoFromWmi(HwState hw)
        {
            // 这个类一次给全：整机合计（_Total）+ 每块物理盘。它是"格式化"性能类，
            // 值本身就是每秒速率，不用再做两次采样的差值。
            try
            {
                if (_wmiDiskIoSe == null)
                {
                    _wmiDiskIoSe = new ManagementObjectSearcher("root\\CIMV2",
                        "SELECT Name, DiskReadBytesPersec, DiskWriteBytesPersec FROM " +
                        "Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
                }

                var list = new List<DiskIoInfo>();
                double totalRead = -1, totalWrite = -1;
                using (ManagementObjectCollection rows = _wmiDiskIoSe.Get())
                {
                    foreach (ManagementBaseObject mo in rows)
                    {
                        double rd = ToDouble(mo["DiskReadBytesPersec"]) / KB;
                        double wr = ToDouble(mo["DiskWriteBytesPersec"]) / KB;
                        if (rd < 0) rd = 0;
                        if (wr < 0) wr = 0;

                        string name = Str(mo["Name"]);
                        if (name == "_Total") { totalRead = rd; totalWrite = wr; continue; }   // 合计另外用

                        Match m = LeadingNumber.Match(name);   // "0 C: D:" → 0
                        if (!m.Success) continue;

                        int pnum = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        var io = new DiskIoInfo();
                        io.Pnum = pnum;
                        io.ReadKB = Math.Round(rd, 1);
                        io.WriteKB = Math.Round(wr, 1);
                        list.Add(io);
                    }
                }

                if (totalRead >= 0) hw.DiskReadKB = Math.Round(totalRead, 1);
                if (totalWrite >= 0) hw.DiskWriteKB = Math.Round(totalWrite, 1);
                if (list.Count > 0) hw.DiskIo = list;
            }
            catch { }
        }

        /// <summary>网络收发速率：所有 Up 网卡的累计字节差 / 间隔秒数。</summary>
        void UpdateNetwork(HwState hw)
        {
            // 网卡会热插拔（USB 网卡）/ 休眠唤醒后重新枚举：定期重建列表，
            // 保留老网卡的基准，所以速率不会因为重建而跳变
            int tick = Environment.TickCount;
            if (_nics.Count == 0 || tick - _nicRefreshTick > NicRefreshMs)
            {
                _nicRefreshTick = tick;
                RefreshNicList(true);
            }

            double stamp = _sw != null ? _sw.Elapsed.TotalSeconds : -1;
            double dt = (_netStamp >= 0 && stamp >= 0) ? stamp - _netStamp : 0;

            double rxSum = 0, txSum = 0;
            bool valid = false;
            foreach (NetworkInterface ni in _nics)
            {
                try
                {
                    IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                    if (st == null) continue;

                    long rx = st.BytesReceived;
                    long tx = st.BytesSent;

                    NicBytes prev = null;
                    if (dt > 0.05) _nicPrev.TryGetValue(ni.Id, out prev);
                    if (prev != null)
                    {
                        long drx = rx - prev.Rx;
                        long dtx = tx - prev.Tx;
                        if (drx < 0) drx = 0;   // 网卡重启后计数归零，按 0 处理
                        if (dtx < 0) dtx = 0;
                        rxSum += drx / dt;
                        txSum += dtx / dt;
                        valid = true;
                    }
                    else
                    {
                        prev = new NicBytes();
                        _nicPrev[ni.Id] = prev;
                    }

                    prev.Rx = rx;
                    prev.Tx = tx;
                }
                catch { }
            }

            if (valid)
            {
                hw.NetRxKB = Math.Round(rxSum / KB, 1);
                hw.NetTxKB = Math.Round(txSum / KB, 1);
            }
            if (stamp >= 0) _netStamp = stamp;
        }

        /// <summary>各分区容量 / 剩余 / 所属物理磁盘。</summary>
        void UpdateDisks(HwState hw)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); } catch { return; }

            var list = new List<DiskVolume>();
            foreach (DriveInfo di in drives)
            {
                try
                {
                    if (di.DriveType != DriveType.Fixed) continue;   // 只算本地固定盘：U 盘 / 光驱 / 网络盘不参与
                    if (!di.IsReady) continue;                       // 空读卡器槽会在这里抛异常，必须先判断

                    long tot = di.TotalSize;
                    if (tot <= 0) continue;
                    long free = di.AvailableFreeSpace;

                    string mount = di.Name != null ? di.Name.TrimEnd('\\') : "";   // "C:\" → "C:"
                    if (mount.Length != 2 || mount[1] != ':') continue;            // 挂到文件夹的卷没有盘符，跳过

                    DiskOwner owner;
                    _diskMap.TryGetValue(mount, out owner);

                    var v = new DiskVolume();
                    v.Mount = mount;
                    v.TotalGB = (int)Math.Round(tot / GB);                         // 契约里 totalGB 是整数
                    v.FreeGB = Math.Round(free / GB, 1);
                    v.Pct = Math.Round((tot - free) / (double)tot * 100.0, 1);
                    v.Pnum = owner != null ? owner.Pnum : -1;
                    v.Phys = owner != null ? owner.Phys : "";
                    list.Add(v);
                }
                catch { }
            }
            if (list.Count > 0) hw.Disks = list;   // 整体替换
        }

        /// <summary>电池 / 电源。</summary>
        void UpdatePower(HwState hw)
        {
            int pct;
            bool onAC;
            if (!HwNative.GetPower(out pct, out onAC)) return;   // 读不到就保持上一次的值

            hw.OnAC = onAC;
            hw.BatPct = pct;   // -1 = 没有电池或读不到（HwState.BatPct 是非空 int，表达不了 null）
        }

        /// <summary>开机时长，格式 "0 天 04:07"。</summary>
        void UpdateUptime(HwState hw)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(HwNative.TickCount64());
            hw.Uptime = string.Format(CultureInfo.InvariantCulture, "{0} 天 {1:00}:{2:00}",
                                      (int)ts.TotalDays, ts.Hours, ts.Minutes);
        }

        // ================================================================== GPU 后台线程

        /// <summary>后台采样循环：跑 nvidia-smi、解析、发布。主循环永不为它等待。</summary>
        void GpuLoop()
        {
            // 死循环是有意的：这是 IsBackground 线程，进程退出时会被系统直接结束，
            // 所以不需要额外的停止开关（对外只有 Initialize / Update 两个入口）。
            while (true)
            {
                try
                {
                    GpuSample s = ParseGpu(RunNvidiaSmi());
                    if (s != null) _gpu = s;   // 引用赋值是原子的，Update() 直接读就行
                }
                catch { }
                try { Thread.Sleep(GpuIntervalMs); } catch { }
            }
        }

        /// <summary>跑一次 nvidia-smi，返回 stdout；失败/超时返回 null。</summary>
        string RunNvidiaSmi()
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = _nvidia;
                psi.Arguments = GpuArgs;
                psi.UseShellExecute = false;        // 必须 false 才能重定向 stdout
                psi.CreateNoWindow = true;          // 否则会闪一下黑框
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;

                    // 先把 stdout 异步读出来再等退出：万一输出超过管道缓冲（4KB），
                    // 子进程会写满卡住、永远不退出，WaitForExit 就成了死等
                    System.Threading.Tasks.Task<string> read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(GpuTimeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    try { p.StandardError.ReadToEnd(); } catch { }   // 排空 stderr，别让它堵在管道里
                    try { if (read.Wait(1000)) return read.Result; } catch { }
                    return null;
                }
            }
            catch { return null; }
        }

        /// <summary>解析 nvidia-smi 的一行 CSV。字段可能是 N/A 或 [N/A]，一律当 null。</summary>
        static GpuSample ParseGpu(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;

            // 多卡机器只取第一块（和 PowerShell 版一致）
            string line = null;
            foreach (string l in output.Split('\n'))
            {
                if (l != null && l.Trim().Length > 0) { line = l; break; }
            }
            if (line == null) return null;

            string[] parts = line.Split(',');
            if (parts.Length < 2) return null;

            var s = new GpuSample();
            s.Name = Field(parts, 0);
            s.TempC = ToIntOrNull(Field(parts, 1));
            s.UtilPct = ToIntOrNull(Field(parts, 2));
            s.MemUsedMB = ToIntOrNull(Field(parts, 3));
            s.MemTotalMB = ToIntOrNull(Field(parts, 4));
            s.PowerW = ToDoubleOrNull(Field(parts, 5));
            s.PowerMaxW = ToIntOrNull(Field(parts, 6));
            s.ClockMHz = ToIntOrNull(Field(parts, 7));
            s.MemClockMHz = ToIntOrNull(Field(parts, 8));
            s.FanPct = ToIntOrNull(Field(parts, 9));
            s.Pstate = Field(parts, 10);

            // 显存占用百分比自己算（契约要 GpuMemPct，不依赖 nvidia-smi 的 utilization.memory）
            if (s.MemUsedMB.HasValue && s.MemTotalMB.HasValue && s.MemTotalMB.Value > 0)
                s.MemPct = (int)Math.Round(s.MemUsedMB.Value * 100.0 / s.MemTotalMB.Value);

            if (string.IsNullOrEmpty(s.Name)) return null;   // 型号都读不到，这次就当没采到
            return s;
        }

        // ================================================================== 工具

        /// <summary>重建 Up 网卡列表。keepBaseline=true 时保留老网卡的累计基准（新网卡从当前值起算）。</summary>
        void RefreshNicList(bool keepBaseline)
        {
            try
            {
                var list = new List<NetworkInterface>();
                var kept = new Dictionary<string, NicBytes>(StringComparer.OrdinalIgnoreCase);

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        NetworkInterfaceType t = ni.NetworkInterfaceType;
                        if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;

                        IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                        if (st == null) continue;
                        list.Add(ni);

                        NicBytes prev;
                        if (keepBaseline && _nicPrev.TryGetValue(ni.Id, out prev))
                        {
                            kept[ni.Id] = prev;      // 延续基准，避免刚重建就出现一次假的速率跳变
                        }
                        else
                        {
                            var nb = new NicBytes();
                            nb.Rx = st.BytesReceived;
                            nb.Tx = st.BytesSent;
                            kept[ni.Id] = nb;
                        }
                    }
                    catch { }
                }

                _nics = list;
                _nicPrev.Clear();
                foreach (KeyValuePair<string, NicBytes> kv in kept) _nicPrev[kv.Key] = kv.Value;
            }
            catch { }
        }

        /// <summary>把 0~100 之外的值夹回区间（计数器溢出/重启时会算出负数或 100+）。</summary>
        static double Clamp(double v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        /// <summary>WMI 关联类（Win32_*To*）的 Antecedent/Dependent 可能是字符串，也可能是对象，两种都认。</summary>
        static string RefText(object v)
        {
            if (v == null) return "";
            string s = v as string;
            if (s != null) return s;

            var mo = v as ManagementObject;   // Path 只声明在 ManagementObject 上，ManagementBaseObject 没有
            if (mo != null)
            {
                try { if (mo.Path != null && !string.IsNullOrEmpty(mo.Path.Path)) return mo.Path.Path; } catch { }
            }

            var bo = v as ManagementBaseObject;
            if (bo != null)
            {
                try { return Convert.ToString(bo["DeviceID"], CultureInfo.InvariantCulture); } catch { }
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>从 'Win32_LogicalDisk.DeviceID="C:"' 这类对象路径里抠出引号内的值。</summary>
        static string DeviceIdIn(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            Match m = DeviceIdValue.Match(path);
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>\\.\PHYSICALDRIVE0 → 0；拿不到返回 -1。</summary>
        static int PhysicalDriveNumber(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return -1;
            Match m = PhysicalDrive.Match(deviceId);
            if (!m.Success) return -1;
            int n;
            return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : -1;
        }

        static string Field(string[] parts, int i)
        {
            if (parts == null || i < 0 || i >= parts.Length) return null;
            string s = parts[i];
            return s == null ? null : s.Trim();
        }

        /// <summary>nvidia-smi 的字段 → double?；"N/A"、"[N/A]"、空、非数字一律 null。</summary>
        static double? ToDoubleOrNull(string text)
        {
            if (text == null) return null;
            string s = text.Trim();
            if (s.Length == 0) return null;
            if (s.IndexOf("N/A", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        static int? ToIntOrNull(string text)
        {
            double? d = ToDoubleOrNull(text);
            return d.HasValue ? (int?)Math.Round(d.Value) : null;
        }

        static string Str(object v)
        {
            if (v == null) return "";
            try { return Convert.ToString(v, CultureInfo.InvariantCulture).Trim(); }
            catch { return ""; }
        }

        static double ToDouble(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        static int Int(object v)
        {
            return (int)Math.Round(ToDouble(v));
        }

        /// <summary>写日志。日志本身出问题（文件被占用 / 磁盘满）绝不能影响采集。</summary>
        static void LogMsg(string msg)
        {
            try { Log.Write(msg); } catch { }
        }

        static void DisposeQuietly(IDisposable d)
        {
            if (d == null) return;
            try { d.Dispose(); } catch { }
        }

        // ================================================================== 内部小类型

        /// <summary>一块物理盘的读 / 写速率计数器。</summary>
        sealed class IoPair : IDisposable
        {
            public readonly PerformanceCounter Read;
            public readonly PerformanceCounter Write;

            public IoPair(PerformanceCounter read, PerformanceCounter write)
            {
                Read = read;
                Write = write;
            }

            public void Dispose()
            {
                DisposeQuietly(Read);
                DisposeQuietly(Write);
            }
        }

        /// <summary>盘符所属的物理磁盘。</summary>
        sealed class DiskOwner
        {
            public readonly int Pnum;
            public readonly string Phys;

            public DiskOwner(int pnum, string phys)
            {
                Pnum = pnum;
                Phys = phys ?? "";
            }
        }

        /// <summary>一块网卡的上次累计字节（用可变对象，避免每次采样都分配）。</summary>
        sealed class NicBytes
        {
            public long Rx;
            public long Tx;
        }

        /// <summary>一次 nvidia-smi 采样结果（后台线程填，Update 只读）。</summary>
        sealed class GpuSample
        {
            public string Name;
            public string Pstate;
            public int? TempC;
            public int? UtilPct;
            public int? MemPct;
            public int? MemUsedMB;
            public int? MemTotalMB;
            public double? PowerW;
            public int? PowerMaxW;
            public int? ClockMHz;
            public int? MemClockMHz;
            public int? FanPct;
        }
    }
}
