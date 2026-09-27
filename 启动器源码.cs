// ============================================================================
//  设备连接监控 —— 单文件启动器
//
//  静默提权的原理：
//    本程序编译成 winexe（GUI 子系统），本身没有控制台窗口。
//    需要管理员权限时，它用 runas 重新启动「自己」——
//    提权后的自己依然没有控制台窗口，然后再用 CreateNoWindow 去启动
//    powershell.exe，于是从头到尾不会创建任何窗口。
//
//    这比「先启动控制台程序、再想办法把窗口藏起来」干净得多：
//    那种做法既不可靠（窗口是提权机制创建的，脚本还没开始跑就已经出现了），
//    又会被安全软件判定为「隐藏执行 PowerShell」—— 那是恶意软件的行为特征。
//
//  命令行：
//    设备连接监控.exe              启动 + 打开网页
//    设备连接监控.exe silent       启动但不打开网页（开机自启用这个）
//    设备连接监控.exe stop         停止监控
//    设备连接监控.exe web          只打开监测网页
//    设备连接监控.exe log          打开日志文件夹
//    设备连接监控.exe autostart    安装开机自启（最高权限计划任务，开机不弹提示）
//    设备连接监控.exe noautostart  取消开机自启
//    设备连接监控.exe debug        带控制台窗口启动，排查问题用
//    设备连接监控.exe noelevate    不提权启动（读不到内存温度）
//    设备连接监控.exe inventory    生成设备清单（看每个设备的真实身份）
//    设备连接监控.exe help         显示这份帮助
//
//  运行范围（外设 / 硬件 / 全部）在网页上切换，不用重开程序。
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

static class Launcher
{
    static string _dir;
    static string _script;      // DeviceWatch.ps1
    static string _self;        // 本 exe 的完整路径

    // 提权标记：重新启动自己时加在参数最前面
    const string ELEVATED_FLAG = "--elevated";

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        try { _self = Assembly.GetExecutingAssembly().Location; }
        catch { _self = null; }
        try { _dir = Path.GetDirectoryName(_self); }
        catch { _dir = null; }
        if (string.IsNullOrEmpty(_dir)) _dir = AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrEmpty(_self)) _self = Path.Combine(_dir, "设备连接监控.exe");

        _script = Path.Combine(_dir, "DeviceWatch.ps1");
        if (!File.Exists(_script))
        {
            MessageBox.Show(
                "找不到 DeviceWatch.ps1。\r\n\r\n请把本程序放在「设备连接监控」文件夹里再运行。\r\n当前目录：" + _dir,
                "设备连接监控", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // ---- 剥掉提权标记 ----
        bool alreadyElevated = false;
        string[] rest = args;
        if (args.Length > 0 && string.Equals(args[0], ELEVATED_FLAG, StringComparison.OrdinalIgnoreCase))
        {
            alreadyElevated = true;
            rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
        }

        string cmd = (rest.Length > 0 ? rest[0] : "").Trim().ToLowerInvariant();

        // ---- 需要管理员权限的操作：先静默提权，再干正事 ----
        // 重新启动的是「自己」这个 GUI 程序，所以不会闪控制台窗口。
        if (!alreadyElevated && NeedsAdmin(cmd) && !IsAdmin())
        {
            return RelaunchElevated(rest);
        }

        switch (cmd)
        {
            case "help":
            case "?":
            case "-h":
            case "--help":
                ShowHelp();
                return 0;

            case "stop":
            case "exit":
            case "quit":
                return DoStop();

            case "web":
            case "open":
                OpenUrl("http://127.0.0.1:" + ReadPort() + "/");
                return 0;

            case "log":
            case "logs":
                return OpenLogFolder();

            case "autostart":
            case "install":
                return RunScript(new[] { "-InstallStartup" }, true);

            case "noautostart":
            case "uninstall":
                return RunScript(new[] { "-UninstallStartup" }, true);

            case "inventory":
            case "list":
                // 设备清单功能已经并进主程序，不再需要单独的脚本
                return RunScript(new[] { "-Inventory", "-OpenInventory" }, false);

            default:
                return DoStart(cmd);
        }
    }

    // ------------------------------------------------------------ 哪些操作要提权
    static bool NeedsAdmin(string cmd)
    {
        switch (cmd)
        {
            case "web":
            case "open":
            case "log":
            case "logs":
            case "help":
            case "?":
            case "-h":
            case "--help":
            case "noelevate":      // 用户明确要求不提权
                return false;
            default:
                // 启动监控、停止、装/卸自启、设备清单都需要（或可能需要）管理员
                return true;
        }
    }

    static bool IsAdmin()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // 用 runas 重新启动自己。本程序是 winexe，提权后同样没有控制台窗口。
    static int RelaunchElevated(string[] rest)
    {
        try
        {
            var psi = new ProcessStartInfo();
            psi.FileName = _self;
            var sb = new StringBuilder();
            sb.Append(ELEVATED_FLAG);
            foreach (string a in rest)
            {
                sb.Append(' ');
                if (a.IndexOf(' ') >= 0) { sb.Append('"').Append(a).Append('"'); }
                else sb.Append(a);
            }
            psi.Arguments = sb.ToString();
            psi.WorkingDirectory = _dir;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            // 用户点了「否」，或者 UAC 拒绝了 —— 退回普通权限继续
            try
            {
                MessageBox.Show(
                    "没有取得管理员权限，将以普通权限继续运行。\r\n\r\n" +
                    "内存温度、CPU 封装功耗、GPU 热点这几项会读不到，其余功能正常。\r\n\r\n" +
                    "（" + ex.Message + "）",
                    "设备连接监控", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            return RunScriptFallback(rest);
        }
    }

    static int RunScriptFallback(string[] rest)
    {
        string cmd = (rest.Length > 0 ? rest[0] : "").Trim().ToLowerInvariant();
        if (cmd == "stop" || cmd == "exit" || cmd == "quit") return RunScript(new[] { "-StopMonitor" }, true);
        if (cmd == "inventory" || cmd == "list") return RunScript(new[] { "-Inventory", "-OpenInventory" }, false);
        return RunScript(new[] { "-NoElevate" }, true);
    }

    // ---------------------------------------------------------------- 启动监控
    static int DoStart(string cmd)
    {
        bool wantConsole = cmd == "console" || cmd == "debug" || cmd == "c";
        bool noElevate = cmd == "noelevate";
        bool silent = cmd == "silent";          // 启动但不打开网页

        int port = ReadPort();
        bool running = IsAlive(ReadRunPid()) || !PortFree(port);

        if (running && !wantConsole && !noElevate)
        {
            if (!silent) OpenUrl("http://127.0.0.1:" + port + "/");
            return 0;
        }

        var list = new System.Collections.Generic.List<string>();
        if (noElevate) list.Add("-NoElevate");
        if (wantConsole) list.Add("-Console");

        int code = RunScript(list.ToArray(), !wantConsole, wantConsole);
        if (code != 0 && !wantConsole)
        {
            MessageBox.Show(
                "启动失败（退出码 " + code + "）。\r\n\r\n" +
                "可以用「设备连接监控.exe debug」看详细报错。",
                "设备连接监控", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return code;
        }

        if (!wantConsole && !noElevate && !silent)
        {
            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (!PortFree(port)) { OpenUrl("http://127.0.0.1:" + port + "/"); break; }
            }
        }
        return 0;
    }

    static int DoStop()
    {
        int code = RunScript(new[] { "-StopMonitor" }, true);

        int port = ReadPort();
        for (int i = 0; i < 20; i++)
        {
            System.Threading.Thread.Sleep(500);
            if (PortFree(port)) break;
        }

        try
        {
            string pf = Path.Combine(_dir, @"logs\monitor.pid");
            if (File.Exists(pf)) File.Delete(pf);
            string sf = Path.Combine(_dir, ".stop-request");
            if (File.Exists(sf)) File.Delete(sf);
        }
        catch { }
        return code;
    }

    static int OpenLogFolder()
    {
        string d = Path.Combine(_dir, "logs");
        try
        {
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            Process.Start(new ProcessStartInfo(d) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开日志文件夹：" + ex.Message, "设备连接监控",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    static void ShowHelp()
    {
        string txt =
            "设备连接监控 —— 命令行说明\r\n" +
            "\r\n" +
            "  设备连接监控.exe              启动 + 打开监测网页\r\n" +
            "  设备连接监控.exe silent       启动但不打开网页（开机自启用）\r\n" +
            "  设备连接监控.exe stop         停止监控\r\n" +
            "  设备连接监控.exe web          只打开监测网页\r\n" +
            "  设备连接监控.exe log          打开日志文件夹\r\n" +
            "  设备连接监控.exe autostart    安装开机自启（开机不弹提示）\r\n" +
            "  设备连接监控.exe noautostart  取消开机自启\r\n" +
            "  设备连接监控.exe debug        带控制台窗口启动，排查问题用\r\n" +
            "  设备连接监控.exe noelevate    不提权启动（读不到内存温度）\r\n" +
            "  设备连接监控.exe inventory    生成设备清单\r\n" +
            "  设备连接监控.exe help         显示这份帮助\r\n" +
            "\r\n" +
            "平时直接双击就行，不需要任何参数。\r\n" +
            "监测范围（外设 / 硬件 / 全部）在网页上切换。";
        try { Console.WriteLine(txt); } catch { }
        MessageBox.Show(txt, "设备连接监控 - 帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------------------------------------------------------- 跑 PowerShell
    // 关键：UseShellExecute=false + CreateNoWindow=true
    //   -> powershell.exe 以 CREATE_NO_WINDOW 启动，控制台窗口根本不会被创建。
    //
    // 不用 -Verb runas（提权已经在 RelaunchElevated 里由本 exe 完成了），
    // 也不用 -WindowStyle Hidden（那是「先创建窗口再隐藏」，既不可靠又容易被安全软件误判）。
    static int RunScript(string[] extra, bool wait)
    {
        return RunScript(extra, wait, false);
    }

    static int RunScript(string[] extra, bool wait, bool showConsole)
    {
        var psi = new ProcessStartInfo();
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 @"WindowsPowerShell\v1.0\powershell.exe");
        psi.FileName = File.Exists(ps) ? ps : "powershell.exe";

        var all = new System.Collections.Generic.List<string>();
        all.Add("-NoProfile");
        all.Add("-NonInteractive");
        all.Add("-ExecutionPolicy");
        all.Add("Bypass");
        all.Add("-File");
        all.Add("\"" + _script + "\"");
        if (extra != null) all.AddRange(extra);
        psi.Arguments = string.Join(" ", all.ToArray());

        psi.WorkingDirectory = _dir;
        if (showConsole)
        {
            // debug / console 模式：用户就是要看那个窗口，正常创建控制台
            psi.UseShellExecute = false;
            psi.CreateNoWindow = false;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
        }

        try
        {
            Process p = Process.Start(psi);
            if (p == null) return 0;
            if (wait) { p.WaitForExit(); return p.ExitCode; }
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.Show("执行失败：" + ex.Message, "设备连接监控",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            return 1;
        }
    }

    // ---------------------------------------------------------------- 辅助
    static int ReadPort()
    {
        try
        {
            string cfg = Path.Combine(_dir, "settings.json");
            if (!File.Exists(cfg)) return 8787;
            string txt = File.ReadAllText(cfg);
            int i = txt.IndexOf("\"WebPort\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 8787;
            int c = txt.IndexOf(':', i);
            if (c < 0) return 8787;
            int j = c + 1;
            while (j < txt.Length && (txt[j] == ' ' || txt[j] == '\t')) j++;
            int k = j;
            while (k < txt.Length && char.IsDigit(txt[k])) k++;
            int v;
            if (k > j && int.TryParse(txt.Substring(j, k - j), out v) && v > 0 && v < 65536) return v;
        }
        catch { }
        return 8787;
    }

    static long ReadRunPid()
    {
        try
        {
            string f = Path.Combine(_dir, @"logs\monitor.pid");
            if (!File.Exists(f)) return 0;
            long v;
            if (long.TryParse(File.ReadAllText(f).Trim(), out v)) return v;
        }
        catch { }
        return 0;
    }

    static bool IsAlive(long pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (Process p = Process.GetProcessById((int)pid)) { return !p.HasExited; }
        }
        catch { return false; }
    }

    static bool PortFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd", "/c start " + url)
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
        }
    }
}
