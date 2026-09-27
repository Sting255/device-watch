// ============================================================================
//  Tray.cs —— 托盘图标 + 右键菜单
//
//  对应 DeviceWatch.ps1 里的 Initialize-Tray（第 820 行）/ Update-Tray（第 918 行）/
//  Show-Alert（第 749 行）。菜单项顺序、文案逐条对齐，用户升级后手感不变。
//
//  【为什么用 WinForms 而不用 Shell_NotifyIcon P/Invoke】
//  托盘本来就要一个 Windows 消息队列，自己 P/Invoke 还得手写 WndProc 和
//  TrackPopupMenu 定位；NotifyIcon 已经把双击、右键菜单做完了，源码更短。
//
//  【本程序没有 Application.Run 消息循环】
//  主循环自己跑，只是定期调 Application.DoEvents() 抽干消息队列。
//  所以：控件一律在 Initialize()（主循环启动前）创建，Update() 里只改文字，
//  绝不新建/销毁控件 —— 不在 UI 线程创建控件是 WinForms 最经典的随机崩溃源。
//
//  【Update() 里绝不做耗时操作】
//  PowerShell 版踩过这个坑：Update-Tray 里带了一次计划任务查询，每 5 秒卡 500ms，
//  界面明显一顿。这里 Update() 只读内存里的计数器，保证微秒级返回。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// 必须写别名：WinForms 项目里编译器的默认引用集会带上 Microsoft.VisualBasic，
// 里面的 Microsoft.VisualBasic.Log 会和本项目的 Log 撞名（TryParse 之外一律二义），
// 不加别名会报一片 CS0104。
using Log = DeviceWatch.Log;

namespace DeviceWatch
{
    public sealed class Tray : IDisposable
    {
        // Windows 的托盘 Tooltip 有 63 字符硬上限（NOTIFYICONDATA.szTip = 128 字节，
        // 一个汉字 2 字节），超了会直接不显示。PowerShell 版截到 63，这里保持一致。
        private const int TipMaxLen = 63;

        /// <summary>
        /// 菜单「退出监控」置位。主循环每 50ms 读一次，读到就优雅退出
        /// （写 summary、删 monitor.pid）。用 volatile：写方是 UI 消息线程，
        /// 读方是主循环线程，必须保证可见性。
        /// </summary>
        public static volatile bool ExitRequested;

        private NotifyIcon _ni;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _status;
        private ToolStripMenuItem _pause;
        private ToolStripMenuItem _learn;
        private bool _disposed;

        /// <summary>Initialize() 是否成功。失败时 Update() 直接返回，不影响监控主流程。</summary>
        public bool Ready { get { return _ni != null; } }

        /// <summary>
        /// 用托盘气泡弹一条通知。这是 WinRT Toast 走不通时的兜底通道
        /// （老系统、通知被组策略禁用、explorer 没起来都会走这里）。
        ///
        /// 【怎么接上】主程序 Initialize() 之后加一行即可：
        ///     Toast.FallbackBalloon = _tray.ShowBalloon;
        /// 不接也不出错，只是那种情况下用户看不到通知、只剩日志。
        /// </summary>
        public bool ShowBalloon(string title, string body, string kind)
        {
            var ni = _ni;
            if (ni == null) return false;
            try
            {
                ToolTipIcon ico = ToolTipIcon.Info;
                if (string.Equals(kind, "Off", StringComparison.OrdinalIgnoreCase)) ico = ToolTipIcon.Error;
                else if (string.Equals(kind, "Warn", StringComparison.OrdinalIgnoreCase)) ico = ToolTipIcon.Warning;

                // 8000ms 对齐 PowerShell 版的 ShowBalloonTip(8000, ...)
                ni.ShowBalloonTip(8000, title, body, ico);
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        //  创建
        // ------------------------------------------------------------------
        /// <summary>创建托盘图标与菜单。必须在主循环启动前、由主线程调用一次。</summary>
        public void Initialize()
        {
            try
            {
                // 视觉样式要在第一个控件创建前打开，否则菜单是 Win95 灰方块
                try { Application.EnableVisualStyles(); } catch { }

                _ni = new NotifyIcon();
                // 用系统自带图标 → 不需要外部 .ico，保持"单个 exe 免安装"
                _ni.Icon = SystemIcons.Information;
                _ni.Text = "设备监控：启动中...";
                _ni.Visible = true;

                // 双击托盘图标 = 打开今天的日志（与 PowerShell 版一致）
                _ni.MouseDoubleClick += delegate { OpenTodayLog(); };

                _menu = new ContextMenuStrip();

                // ---- 状态行（不可点击）----
                _status = new ToolStripMenuItem("设备监控 启动中...");
                _status.Enabled = false;
                _menu.Items.Add(_status);
                _menu.Items.Add(new ToolStripSeparator());

                // ---- 打开类 ----
                AddItem("打开今天的日志 (&L)", delegate { OpenTodayLog(); });
                AddItem("打开日志文件夹 (&F)", delegate { OpenFolder(Log.LogDir); });
                AddItem("打开监测网页 (&W)", delegate { OpenWeb(); });
                AddItem("打开断联统计文件 (&S)", delegate { OpenStats(); });
                _menu.Items.Add(new ToolStripSeparator());

                // ---- 开关类 ----
                _pause = AddItem("暂停弹窗通知", delegate { TogglePause(); SyncToggleText(); });

                _learn = AddItem("学习模式 10 分钟（只记录不弹窗）", delegate { StartLearning(); SyncToggleText(); });

                AddItem("忽略最近断联的设备", delegate { IgnoreLatest(); });

                AddItem("立即完整扫描 (&R)", delegate { RequestFullScan(); });

                _menu.Items.Add(new ToolStripSeparator());

                AddItem("发送测试通知", delegate { TestToast(); });

                _menu.Items.Add(new ToolStripSeparator());

                AddItem("退出监控 (&X)", delegate
                {
                    ExitRequested = true;
                    Log.Write("托盘：收到退出请求");
                });

                _ni.ContextMenuStrip = _menu;

                // 首帧就把真实数字填上，避免用户看到"启动中..."要等 5 秒
                Update();
            }
            catch (Exception ex)
            {
                // 托盘挂了不能连累监控：置空后 Update() 会直接返回
                try { if (_ni != null) { _ni.Visible = false; _ni.Dispose(); } } catch { }
                _ni = null;
                Log.Write("托盘初始化失败（不影响监控）：" + ex.Message, "Warn");
            }
        }

        /// <summary>建一个菜单项并挂上点击处理。所有处理内部都会再 try-catch 一次。</summary>
        private ToolStripMenuItem AddItem(string text, EventHandler onClick)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Click += onClick;
            _menu.Items.Add(mi);
            return mi;
        }

        // ------------------------------------------------------------------
        //  刷新（每 5 秒由主循环调用）
        // ------------------------------------------------------------------
        /// <summary>
        /// 刷新提示文字和状态行。只读内存里的计数，不做任何查询/IO。
        /// 整体 try-catch：托盘刷新失败绝不能影响监控。
        /// </summary>
        public void Update()
        {
            var ni = _ni;
            if (ni == null) return;
            try
            {
                int devices;
                int problems;
                try { devices = AppState.I.DeviceCount; } catch { devices = 0; }
                try { problems = AppState.I.ProblemCount; } catch { problems = 0; }

                bool netDown = false;
                try { netDown = AppState.I.NetDown; } catch { }
                bool learning = false;
                try { learning = AppState.I.Learning; } catch { }
                bool notifyOn = true;
                try { notifyOn = AppState.I.NotifyEnabled; } catch { }

                // 事件条数只算今天的：EventCount 是从进程启动累加的，
                // 挂机几天后显示"今日事件 3000 条"会误导。
                int eventsToday = CountTodayEvents();
                DateTime? last = null;
                try { last = AppState.I.LastEvent; } catch { }

                // ---- 状态判定（顺序与 PowerShell 版一致：断网 > 故障 > 刚变动）----
                string state = "正常";
                if (netDown) state = "外网断开";
                else if (problems > 0)
                {
                    // 只有一个故障设备时直接把名字报出来，比"有 1 个设备故障"有用
                    string only = null;
                    try
                    {
                        var list = AppState.I.ProblemList;
                        if (problems == 1 && list != null && list.Count == 1) only = list[0].Name;
                    }
                    catch { }
                    state = string.IsNullOrEmpty(only) ? ("有 " + problems + " 个设备故障") : ("故障: " + only);
                }
                else if (last.HasValue && (DateTime.Now - last.Value).TotalSeconds < 20)
                {
                    state = "刚刚有变动";
                }

                // ---- Tooltip 的三种口径 ----
                string tip;
                if (learning) tip = string.Format("设备监控：学习模式中 | 在线 {0} 个", devices);
                else if (!notifyOn) tip = string.Format("设备监控：通知已暂停 | 在线 {0} 个", devices);
                else tip = string.Format("设备监控：{0} | 在线 {1} 个", state, devices);
                if (tip.Length > TipMaxLen) tip = tip.Substring(0, TipMaxLen);

                // ---- 图标：绿色信息正常 / 警告异常 ----
                // （系统自带图标里没有彩色圆点，用信息/警告区分"正常"和"要你看一眼"）
                Icon icon = SystemIcons.Information;
                if (netDown || problems > 0 || state == "刚刚有变动") icon = SystemIcons.Warning;

                ni.Text = tip;
                ni.Icon = icon;

                if (_status != null)
                {
                    _status.Text = string.Format("在线设备 {0} 个 | 今日事件 {1} 条 | 状态：{2}",
                                                 devices, eventsToday, state);
                }
            }
            catch { /* 托盘刷新失败静默处理 */ }

            // 菜单勾选/文字也要跟着刷新：网页上的 /api/notify 也能改同一个开关，
            // 只靠菜单点击时同步会让两处显示不一致。
            SyncToggleText();
        }

        /// <summary>今日事件条数。Recent 最多留 100 条，遍历开销可忽略。</summary>
        private static int CountTodayEvents()
        {
            try
            {
                var recent = AppState.I.Recent;
                if (recent == null) return 0;
                int today = DateTime.Today.DayOfYear;
                int n = 0;
                lock (AppState.I.Sync)
                {
                    for (int i = 0; i < recent.Count; i++)
                    {
                        if (recent[i].Time.DayOfYear == today) n++;
                    }
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>学习模式/暂停状态的文字同步。菜单点完、Update() 里都会调。</summary>
        private void SyncToggleText()
        {
            try
            {
                bool notifyOn = true, learning = false;
                try { notifyOn = AppState.I.NotifyEnabled; } catch { }
                try { learning = AppState.I.Learning; } catch { }
                if (_pause != null)
                {
                    _pause.Text = notifyOn ? "暂停弹窗通知" : "恢复弹窗通知";
                    _pause.Checked = !notifyOn;
                }
                if (_learn != null) _learn.Checked = learning;
            }
            catch { }
        }

        // ------------------------------------------------------------------
        //  菜单动作
        // ------------------------------------------------------------------

        /// <summary>
        /// 「立即完整扫描」。只置标志，真正的全量扫描由 DeviceMonitor 主循环在
        /// 它自己的节奏里做；这里绝不同步跑一次扫描 —— 会卡住 UI 消息泵，
        /// 用户看到的就是"点了菜单整个程序卡死"。
        /// </summary>
        private static void RequestFullScan()
        {
            try
            {
                DeviceMonitor.ForceFullScan = true;
                Log.Write("托盘：已请求立即完整扫描");
            }
            catch (Exception ex) { Log.Write("请求完整扫描失败：" + ex.Message, "Warn"); }
        }

        private static void OpenTodayLog()
        {
            try { Process.Start("notepad.exe", Quote(Log.TodayFile)); }
            catch (Exception ex) { Log.Write("打开日志失败：" + ex.Message, "Warn"); }
        }

        private static void OpenFolder(string dir)
        {
            try { Process.Start("explorer.exe", Quote(dir)); }
            catch (Exception ex) { Log.Write("打开文件夹失败：" + ex.Message, "Warn"); }
        }

        private static void OpenWeb()
        {
            try
            {
                int port = 8787;
                try { port = Settings.I.WebPort; } catch { }
                Process.Start("http://127.0.0.1:" + port + "/");
            }
            catch (Exception ex) { Log.Write("打开网页失败：" + ex.Message, "Warn"); }
        }

        /// <summary>
        /// 打开断联统计文件。统计文本由主程序在退出时写入（DeviceMonitor.WriteSummary），
        /// 所以这里只负责用记事本打开 —— 文件可能还是上一次退出时的内容，够用。
        /// 连文件都不存在（从没正常退出过）就报一句，别让记事本弹"找不到文件"。
        /// </summary>
        private static void OpenStats()
        {
            try
            {
                string file = Log.StatsFile;
                if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
                {
                    Process.Start("notepad.exe", Quote(file));
                }
                else
                {
                    Log.Write("断联统计文件还没生成（程序正常退出时才写）：" + file, "Warn");
                }
            }
            catch (Exception ex) { Log.Write("打开统计文件失败：" + ex.Message, "Warn"); }
        }

        private static void TogglePause()
        {
            try
            {
                AppState.I.NotifyEnabled = !AppState.I.NotifyEnabled;
                Log.Write(AppState.I.NotifyEnabled ? "通知已恢复（托盘）" : "通知已暂停（托盘）");
            }
            catch (Exception ex) { Log.Write("切换通知开关失败：" + ex.Message, "Warn"); }
        }

        private static void StartLearning()
        {
            try
            {
                AppState.I.Learning = true;
                AppState.I.LearnUntil = DateTime.Now.AddMinutes(10);
                Log.Write("进入学习模式 10 分钟（托盘）");
            }
            catch (Exception ex) { Log.Write("进入学习模式失败：" + ex.Message, "Warn"); }
        }

        /// <summary>
        /// 忽略最近断联的设备。PowerShell 版只加进程内的 Muted 表（重启就失效），
        /// 这里按需求写进 settings.json 的 IgnorePatterns，重启后依然有效。
        /// </summary>
        private static void IgnoreLatest()
        {
            try
            {
                DeviceEvent last = null;
                lock (AppState.I.Sync)
                {
                    var recent = AppState.I.Recent;
                    if (recent != null && recent.Count > 0) last = recent[recent.Count - 1];
                }
                if (last == null || string.IsNullOrEmpty(last.Id))
                {
                    Log.Write("忽略设备：最近没有断联记录，跳过", "Warn");
                    return;
                }

                // 写成"实例 ID 前缀 + 通配"：USB 设备重插后实例 ID 后半段会变，
                // 但 USB\VID_xxxx&PID_xxxx 这一段是稳定的（与 Test-Ignored 的 -like 语义一致）。
                string pattern = last.Id;
                int cut = pattern.LastIndexOf('\\');
                if (cut > 0) pattern = pattern.Substring(0, cut) + "*";

                var st = Settings.I;
                if (st.IgnorePatterns == null) st.IgnorePatterns = new List<string>();
                if (!st.IgnorePatterns.Contains(pattern))
                {
                    st.IgnorePatterns.Add(pattern);
                    st.Save();
                }
                Log.Write(string.Format("已忽略设备：{0}  ({1})", last.Name, pattern));
            }
            catch (Exception ex) { Log.Write("忽略最近设备失败：" + ex.Message, "Warn"); }
        }

        private static void TestToast()
        {
            try
            {
                int n = 0;
                try { n = AppState.I.DeviceCount; } catch { }
                Toast.Show("断联哨兵 - 测试",
                           string.Format("通知通道正常。当前在线设备 {0} 个。", n), "Warn");
            }
            catch (Exception ex) { Log.Write("测试通知失败：" + ex.Message, "Warn"); }
        }

        // ------------------------------------------------------------------
        //  杂项
        // ------------------------------------------------------------------
        /// <summary>给 Process.Start 的参数加引号，路径带空格也不会被拆开。</summary>
        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s + "\"";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_ni != null)
                {
                    _ni.Visible = false;   // 先隐藏：进程直接退出时图标不会残留在托盘区
                    _ni.Dispose();
                    _ni = null;
                }
                if (_menu != null) { _menu.Dispose(); _menu = null; }
            }
            catch { }
        }
    }
}
