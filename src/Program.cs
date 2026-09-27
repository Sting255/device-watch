// ============================================================================
//  Program.cs —— 程序入口、提权、主循环
//
//  静默提权原理：本程序编译成 winexe（GUI 子系统），本身没有控制台窗口。
//  需要管理员时用 runas 重新启动「自己」，提权后依然没有窗口 —— 所以全程无窗口。
//
//  不用「先启动控制台程序再隐藏窗口」那种做法：窗口是 UAC 提权机制创建的，
//  程序还没开始跑就已经出现，藏不掉；而且主动隐藏会被安全软件判定为
//  「隐藏执行」，那正是恶意软件的行为特征。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace DeviceWatch
{
    internal static class Program
    {
        // ---- 命令行 ----
        private static bool _console;
        private static bool _noTray;
        private static bool _noWeb;
        private static bool _noElevate;
        private static string _startMode;
        private static string _cmd = "";

        private const string ElevatedFlag = "--elevated";
        private const string MutexName = @"Local\DeviceWatch_SingleInstance";

        private static Mutex _mutex;
        private static Tray _tray;
        private static WebServer _web;
        private static LhmSensors _lhm;
        private static HardwareMonitor _hw;
        private static DeviceMonitor _dev;
        private static volatile bool _running = true;

        // ---- 看门狗 ----
        private static DateTime _lastPumpAt = DateTime.MinValue;
        private static double _maxBlockMs;
        private static string _maxBlockWhere = "";
        private static DateTime _lastBlockReport = DateTime.MinValue;
        // 阶段名统一放在 AppState.CurrentPhase 里（硬件模块也会写它），

        // ---- 周期计时器（用 Environment.TickCount）----
        private static int _tEvt, _tScan, _tExt, _tHw, _tTray, _tAuto;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Settings.I.Load();
            ParseArgs(args);

            // ---- 不跑监控的命令，先处理掉 ----
            switch (_cmd)
            {
                case "help": case "?": case "-h": case "--help": ShowHelp(); return 0;
                case "web": OpenUrl("http://127.0.0.1:" + Settings.I.WebPort + "/"); return 0;
                case "log": OpenLogFolder(); return 0;
                case "stop": return StopRunning();
                case "autostart": return RunAutostart(true);
                case "noautostart": return RunAutostart(false);
                case "inventory": return RunInventory();
            }

            // ---- 单实例 ----
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // 已经在跑了：双击就只是开网页（silent 时什么都不做）
                if (_cmd != "silent") OpenUrl("http://127.0.0.1:" + Settings.I.WebPort + "/");
                return 0;
            }

            // ---- 需要管理员：静默提权 ----
            if (!_noElevate && NeedsAdmin(_cmd) && !IsAdmin())
            {
                if (RelaunchElevated(args)) return 0;
                _noElevate = true;   // 用户拒绝了 UAC，降级继续跑
            }

            int rc;
            try { rc = RunMonitor(); }
            catch (Exception ex)
            {
                try { Log.Write("主循环异常退出：" + ex, "Error"); } catch { }
                if (_console) Console.WriteLine(ex);
                rc = 1;
            }
            finally { Shutdown(); }
            return rc;
        }

        // ====================================================================
        //  参数
        // ====================================================================
        private static void ParseArgs(string[] args)
        {
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i].Trim();
                if (string.Equals(s, ElevatedFlag, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(s, "-Console", StringComparison.OrdinalIgnoreCase)) { _console = true; continue; }
                if (string.Equals(s, "-NoTray", StringComparison.OrdinalIgnoreCase)) { _noTray = true; continue; }
                if (string.Equals(s, "-NoWeb", StringComparison.OrdinalIgnoreCase)) { _noWeb = true; continue; }
                if (string.Equals(s, "-NoElevate", StringComparison.OrdinalIgnoreCase)) { _noElevate = true; continue; }
                if (string.Equals(s, "-StartMode", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) { _startMode = args[++i].Trim(); }
                    continue;
                }
                rest.Add(s);
            }
            _cmd = rest.Count > 0 ? rest[0].ToLowerInvariant() : "";
        }

        private static bool NeedsAdmin(string cmd)
        {
            switch (cmd)
            {
                case "web": case "log": case "help": return false;
                default: return true;
            }
        }

        private static bool IsAdmin()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>用 runas 重启自己（GUI 程序，提权后依然无窗口）。成功返回 true。</summary>
        private static bool RelaunchElevated(string[] args)
        {
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                var sb = new System.Text.StringBuilder();
                sb.Append(ElevatedFlag);
                foreach (string a in args)
                {
                    string s = a.Trim();
                    if (string.Equals(s, ElevatedFlag, StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(' ').Append(s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s);
                }
                Process.Start(new ProcessStartInfo(self, sb.ToString())
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDir
                });
                return true;
            }
            catch { return false; }
        }

        // ====================================================================
        //  主流程
        // ====================================================================
        private static int RunMonitor()
        {
            Log.Init();
            Toast.Initialize();

            AppState S = AppState.I;
            S.StartTime = DateTime.Now;

            // 模式优先级：命令行 > settings.json > All
            if (!string.IsNullOrEmpty(_startMode) && IsValidMode(_startMode)) S.Mode = _startMode;
            else if (IsValidMode(Settings.I.RunMode)) S.Mode = Settings.I.RunMode;
            else S.Mode = "All";
            S.HwMetric = Settings.I.HwMetric;
            S.NotifyEnabled = Settings.I.Notify;

            Log.Write("设备连接监控 v" + Version + " 启动");
            Log.Write("程序目录：" + AppDir);

            if (Settings.I.ShowTray && !_noTray)
            {
                try
                {
                    _tray = new Tray();
                    _tray.Initialize();
                    // 老系统上 WinRT 通知可能不可用，接一个托盘气泡兜底
                    Toast.FallbackBalloon = _tray.ShowBalloon;
                }
                catch (Exception ex) { Log.Write("托盘初始化失败：" + ex.Message, "Warn"); _tray = null; }
            }

            if (Settings.I.WebEnabled && !_noWeb)
            {
                try
                {
                    _web = new WebServer();
                    // 成功时 WebServer 内部已经记过日志了，这里只管失败
                    if (!_web.Start(Settings.I.WebPort)) { Log.Write("网页面板启动失败（端口可能被占用）", "Warn"); _web = null; }
                }
                catch (Exception ex) { Log.Write("网页面板启动失败：" + ex.Message, "Warn"); _web = null; }
            }

            S.AutostartInstalled = SafeAutostart();

            // 外网探测走独立后台线程：Ping 一个目标最多要等 PingTimeoutMs（默认 1500ms），
            // 放进主循环会把设备轮询和界面全卡住。
            try { if (Settings.I.WatchInternet) NetProbe.Start(); } catch { }

            // 硬件子系统：初始化很快，可以在启动阶段做。
            // 低层传感器（LHM）要 5~6 秒，放到主循环里延迟加载 ——
            // 否则网页端口虽然在监听、页面却一直空白，用户要白等 8 秒。
            if (S.Mode != "Device") EnsureHardware();

            _dev = new DeviceMonitor();
            // 去抖等待的那 200ms 里也要处理网页请求，否则插拔瞬间面板会顿一下
            DeviceMonitor.PumpHook = Pump;
            try { _dev.Initialize(); } catch (Exception ex) { Log.Write("设备基线建立失败：" + ex.Message, "Warn"); }
            Log.Write(string.Format("基线建立完成：当前在线设备 {0} 个", S.DeviceCount));
            ScanProblems();
            Log.Write(string.Format("设备状态检查：发现 {0} 个异常设备", S.ProblemCount));

            // 启动阶段的耗时不算「运行期卡顿」
            ResetWatchdog();

            Log.Write("监控已启动，发现设备接入/断开会立即通知。");

            // ================================================================
            //  主循环
            // ================================================================
            while (_running)
            {
                Pump();
                if (_tray != null && Tray.ExitRequested) break;

                // ---- 分片休眠：粒度必须 ≤50ms ----
                // 托盘菜单响应全靠这里的 DoEvents 泵消息：改成 100ms 后菜单明显发卡，
                // 而 DoEvents 单次只要 0.008ms —— 为省这点开销牺牲响应是笔亏本账。
                int sliceTotal = Math.Max(100, Settings.I.PollMs);
                for (int done = 0; done < sliceTotal && _running; done += 50)
                {
                    Pump();
                    if (_tray != null && Tray.ExitRequested) { _running = false; break; }
                    _dev.FlushPending();
                    Thread.Sleep(50);
                }
                if (!_running) break;

                // ---- 外设监测 ----
                if (S.Mode != "Hardware")
                {
                    AppState.I.CurrentPhase = "外设-设备比对";
                    try { _dev.Poll(); } catch (Exception ex) { Log.Write("设备轮询异常：" + ex.Message, "Error"); }
                    Pump();   // 各分节之间也泵一次消息：既让界面及时响应，也让看门狗能准确定位到具体哪一段

                    if (Settings.I.WatchEventLog && Elapsed(ref _tEvt, Settings.I.EventLogCheckSec * 1000))
                    {
                        AppState.I.CurrentPhase = "外设-系统日志";
                        try { _dev.CheckEventLog(); } catch { }
                        Pump();
                    }

                    if (DeviceMonitor.ForceFullScan || Elapsed(ref _tScan, Settings.I.FullScanSec * 1000))
                    {
                        AppState.I.CurrentPhase = "外设-完整扫描";
                        DeviceMonitor.ForceFullScan = false;
                        Pump();
                        ScanProblems();
                        Pump();
                        Pump();
                    }
                }

                // ---- 硬件监测 ----
                if (S.Mode != "Device")
                {
                    // 懒初始化：启动时若只跑外设，这里才第一次建硬件子系统（省内存）
                    if (_hw == null) EnsureHardware();

                    // 低层传感器：延迟 2 秒加载，先让面板把设备列表显示出来
                    if (_lhm == null && (DateTime.Now - S.StartTime).TotalSeconds >= 2)
                    {
                        AppState.I.CurrentPhase = "硬件-低层传感器";
                        try
                        {
                            var l = new LhmSensors();
                            if (l.TryOpen()) _lhm = l;
                        }
                        catch { }
                        ResetWatchdog();   // 这是启动期的一次性成本
                    }

                    if (_lhm != null && Elapsed(ref _tExt, Settings.I.LhmSampleSec * 1000))
                    {
                        AppState.I.CurrentPhase = "硬件-低层传感器";
                        try { _lhm.Read(); } catch { }
                        Pump();
                    }

                    if (_hw != null && Elapsed(ref _tHw, Settings.I.HwSampleMs))
                    {
                        AppState.I.CurrentPhase = "硬件-采样";
                        try { _hw.Update(); } catch (Exception ex) { Log.Write("硬件采样异常：" + ex.Message, "Warn"); }
                        Pump();
                    }
                }

                // ---- 通知合并 ----
                AppState.I.CurrentPhase = "通知";
                _dev.FlushPending();

                // ---- 托盘 / 自启缓存 ----
                if (_tray != null && Elapsed(ref _tTray, 5000))
                {
                    AppState.I.CurrentPhase = "托盘";
                    try { _tray.Update(); } catch { }
                }
                if (Elapsed(ref _tAuto, 300000))
                {
                    // 开机自启状态每 5 分钟才查一次：schtasks 一次要几百毫秒，
                    // 绝不能放进 /api/state 那种每秒被调用一次的高频路径。
                    S.AutostartInstalled = SafeAutostart();

            // 外网探测走独立后台线程：Ping 一个目标最多要等 PingTimeoutMs（默认 1500ms），
            // 放进主循环会把设备轮询和界面全卡住。
            try { if (Settings.I.WatchInternet) NetProbe.Start(); } catch { }
                }

                ReportBlock();
            }

            Log.Write("监控已停止。");
            return 0;
        }

        private static void EnsureHardware()
        {
            if (_hw != null) return;
            try { _hw = new HardwareMonitor(); _hw.Initialize(); }
            catch (Exception ex) { Log.Write("硬件采集初始化失败：" + ex.Message, "Warn"); _hw = null; }
        }

        // ====================================================================
        //  消息泵 + 网页
        // ====================================================================
        private static void Pump()
        {
            DateTime now = DateTime.Now;
            if (_lastPumpAt != DateTime.MinValue)
            {
                double gap = (now - _lastPumpAt).TotalMilliseconds;
                if (gap > _maxBlockMs) { _maxBlockMs = gap; _maxBlockWhere = AppState.I.CurrentPhase; }
            }
            _lastPumpAt = now;

            try { Application.DoEvents(); } catch { }
            if (_web != null) { try { _web.Pump(); } catch { } }
        }

        private static void ResetWatchdog()
        {
            _maxBlockMs = 0; _maxBlockWhere = "";
            _lastPumpAt = DateTime.MinValue; _lastBlockReport = DateTime.MinValue;
        }

        private static void ReportBlock()
        {
            if (_lastBlockReport == DateTime.MinValue) { _lastBlockReport = DateTime.Now; return; }
            if ((DateTime.Now - _lastBlockReport).TotalSeconds < 30) return;
            AppState.I.UiLagMs = (int)Math.Round(_maxBlockMs);
            if (_maxBlockMs > 250)
            {
                Log.Write(string.Format("界面卡顿看门狗：过去 30 秒内最长阻塞 {0:N0} ms（发生在：{1}）",
                    _maxBlockMs, _maxBlockWhere), "Warn");
            }
            _maxBlockMs = 0; _maxBlockWhere = ""; _lastBlockReport = DateTime.Now;
        }

        // ====================================================================
        //  工具
        // ====================================================================
        private static bool Elapsed(ref int last, int interval)
        {
            int now = Environment.TickCount;
            if (last == 0) { last = now; return true; }
            if (now - last >= interval) { last = now; return true; }
            return false;
        }

        private static void ScanProblems()
        {
            try
            {
                var rows = DevNative.Details();
                var bad = new List<ProblemDevice>();
                var benign = new List<ProblemDevice>();
                foreach (var r in rows)
                {
                    int prob;
                    if (!int.TryParse(r[4], out prob) || prob == 0) continue;
                    var pd = new ProblemDevice { Id = r[0], Name = r[1], Class = r[2], Problem = prob };
                    // 这些故障码属于「正常但显示为异常」的情况，不算真故障：
                    //   21 = 设备正在等待删除   22 = 设备已被禁用   45 = 设备当前未连接
                    //   51/52/53 = 被策略禁用 / 其它策略状态
                    if (prob == 21 || prob == 22 || prob == 45 || prob == 51 || prob == 52 || prob == 53) benign.Add(pd);
                    else bad.Add(pd);
                }
                lock (AppState.I.Sync)
                {
                    AppState.I.ProblemList = bad;
                    AppState.I.BenignList = benign;
                }
            }
            catch { }
        }

        private static bool SafeAutostart()
        {
            try { return Autostart.IsInstalled(); } catch { return false; }
        }

        public static string AppDir
        {
            get
            {
                try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
                catch { return AppDomain.CurrentDomain.BaseDirectory; }
            }
        }

        public static string Version { get { return "2.0"; } }

        private static bool IsValidMode(string m)
        {
            return m == "All" || m == "Device" || m == "Hardware";
        }

        private static void Shutdown()
        {
            try { if (_lhm != null) _lhm.Close(); } catch { }
            try { if (_tray != null) _tray.Dispose(); } catch { }
            try { if (_web != null) _web.Stop(); } catch { }
            try { WriteSummary(); } catch { }
            try { Log.Write("已退出。"); } catch { }
            try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }
        }

        private static void WriteSummary()
        {
            var S = AppState.I;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("设备断联统计（本次运行）");
            sb.AppendLine(string.Format("启动时间：{0:yyyy-MM-dd HH:mm:ss}    现在：{1:HH:mm:ss}", S.StartTime, DateTime.Now));
            sb.AppendLine(string.Format("在线设备：{0}    记录事件：{1} 条", S.DeviceCount, S.EventCount));
            sb.AppendLine(new string('-', 78));
            var rows = new List<DeviceStat>(S.Stats.Values);
            rows.Sort((a, b) => b.Off.CompareTo(a.Off));
            if (rows.Count == 0) sb.AppendLine("本次运行期间还没有任何设备断开过。");
            else
            {
                sb.AppendLine(string.Format("{0,-38} {1,8} {2,8}", "设备名称", "断联", "接入"));
                foreach (var r in rows)
                {
                    string n = r.Name ?? "";
                    if (n.Length > 36) n = n.Substring(0, 35) + "…";
                    sb.AppendLine(string.Format("{0,-38} {1,8} {2,8}", n, r.Off, r.On));
                }
                sb.AppendLine();
                sb.AppendLine("断联次数最多的设备就是最可疑的。");
            }
            Log.WriteStats(sb.ToString());
        }

        // ====================================================================
        //  辅助命令
        // ====================================================================
        private static int StopRunning()
        {
            try
            {
                string flag = Path.Combine(AppDir, ".stop-request");
                File.WriteAllText(flag, "stop");
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(500);
                    if (!PortInUse(Settings.I.WebPort)) break;
                }
                try { File.Delete(flag); } catch { }
                try { File.Delete(Path.Combine(Log.LogDir, "monitor.pid")); } catch { }
                return 0;
            }
            catch { return 1; }
        }

        private static bool PortInUse(int port)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                l.Start(); l.Stop(); return false;
            }
            catch { return true; }
        }

        private static int RunAutostart(bool install)
        {
            if (!IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                        install ? "autostart" : "noautostart")
                    { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDir });
                    return 0;
                }
                catch { return 1; }
            }
            bool ok = install ? Autostart.Install() : Autostart.Uninstall();
            try
            {
                MessageBox.Show(ok ? (install ? "已安装开机自启。" : "已取消开机自启。") : "操作失败。",
                    "设备连接监控", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch { }
            return ok ? 0 : 1;
        }

        private static int RunInventory()
        {
            try
            {
                Log.Init();
                var r = Inventory.Run(false);
                Log.Write(string.Format("设备清单已生成：{0}（{1} 个设备，{2} 毫秒）", r.File, r.Count, r.Ms));
                try { Process.Start("notepad.exe", "\"" + r.File + "\""); } catch { }
                return 0;
            }
            catch (Exception ex)
            {
                try { MessageBox.Show("生成失败：" + ex.Message, "设备连接监控"); } catch { }
                return 1;
            }
        }

        private static void OpenLogFolder()
        {
            try
            {
                if (!Directory.Exists(Log.LogDir)) Directory.CreateDirectory(Log.LogDir);
                Process.Start(new ProcessStartInfo(Log.LogDir) { UseShellExecute = true });
            }
            catch { }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private static void ShowHelp()
        {
            string txt =
                "设备连接监控 v" + Version + " —— 命令行说明\r\n\r\n" +
                "  设备连接监控.exe              启动 + 打开监测网页\r\n" +
                "  设备连接监控.exe silent       启动但不打开网页（开机自启用）\r\n" +
                "  设备连接监控.exe stop         停止监控\r\n" +
                "  设备连接监控.exe web          只打开监测网页\r\n" +
                "  设备连接监控.exe log          打开日志文件夹\r\n" +
                "  设备连接监控.exe autostart    安装开机自启\r\n" +
                "  设备连接监控.exe noautostart  取消开机自启\r\n" +
                "  设备连接监控.exe debug        带控制台窗口启动，排查问题用\r\n" +
                "  设备连接监控.exe noelevate    不提权启动（读不到内存温度）\r\n" +
                "  设备连接监控.exe inventory    生成设备清单\r\n" +
                "  设备连接监控.exe help         显示这份帮助\r\n\r\n" +
                "平时直接双击就行，不需要任何参数。\r\n" +
                "监测范围（外设 / 硬件 / 全部）在网页上切换。";
            try { Console.WriteLine(txt); } catch { }
            try { MessageBox.Show(txt, "设备连接监控 - 帮助", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
        }
    }
}
