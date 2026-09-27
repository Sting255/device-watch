// ============================================================================
//  Autostart.cs —— 开机自启（Windows 计划任务，以最高权限运行）
//
//  为什么用计划任务而不是启动文件夹快捷方式：
//    读内存温度 / CPU 功耗要走驱动，需要管理员权限。快捷方式每次开机都会弹
//    UAC 确认框，用户得点一下才启动；计划任务可以预先声明"以最高权限运行"
//    （/RL HIGHEST），开机时静默启动，不弹任何窗口。
//
//  为什么直接用 schtasks.exe 而不是 TaskScheduler COM：
//    1. COM 要引 TaskScheduler 互操作程序集，单 exe 免安装的约束下不想拖依赖；
//    2. COM 创建任务失败时的异常信息比 schtasks 的返回码更难解释；
//    3. schtasks 所有 Windows 版本都有，行为稳定。
//
//  为什么 IsInstalled 用 schtasks /Query 而不是 PowerShell 的 Get-ScheduledTask：
//    Get-ScheduledTask 每次要 0.5 秒左右（要起 PowerShell），而这个是状态接口
//    的高频调用路径。schtasks /Query 一次约 20 毫秒，再配一层短缓存足够。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace DeviceWatch
{
    public static class Autostart
    {
        /// <summary>计划任务名。改这个字符串等于换一个自启项，历史任务不会被覆盖。</summary>
        public const string TaskName = "DeviceWatch";

        /// <summary>安装到计划任务里的启动参数：silent = 启动监控但不弹浏览器。</summary>
        public const string SilentArg = "silent";

        /// <summary>schtasks 超时（毫秒）。正常 20~80ms 就返回，2 秒还没回来基本是卡住了。</summary>
        private const int TimeoutMs = 2000;

        // ------------------------------------------------------------ 查询缓存
        // 状态接口轮询很频繁（网页每秒一次），每次都起一个 schtasks 进程太浪费。
        // 缓存 2 秒：用户点了"开机自启"之后 force 刷新一次，界面立刻能变。
        private const int CacheMs = 2000;

        private static readonly object _lock = new object();
        private static bool _cached;
        private static bool _cachedValue;
        private static DateTime _cachedAt = DateTime.MinValue;

        // ============================================================ 对外接口
        /// <summary>
        /// 计划任务是否存在。任何异常（schtasks 缺失 / 超时 / 权限）都按"未安装"处理，
        /// 不抛异常——调用方是网页线程和托盘菜单，抛出去只会变成 500。
        /// </summary>
        public static bool IsInstalled()
        {
            lock (_lock)
            {
                if (_cached && (DateTime.UtcNow - _cachedAt).TotalMilliseconds < CacheMs) return _cachedValue;
            }
            bool v = QueryTask();
            lock (_lock)
            {
                _cached = true;
                _cachedValue = v;
                _cachedAt = DateTime.UtcNow;
            }
            return v;
        }

        /// <summary>丢弃缓存，下一次 IsInstalled() 真的去问系统。</summary>
        public static void Invalidate() { lock (_lock) { _cached = false; } }

        /// <summary>
        /// 安装开机自启：创建计划任务，登录时触发、以最高权限运行。
        /// 需要管理员权限——没有权限时返回 false（不抛异常，由调用方给提示）。
        /// </summary>
        public static bool Install()
        {
            string exe = ExePath();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Log.Write("开机自启安装失败：找不到程序自身路径（" + exe + "）", "Warn");
                return false;
            }

            // /TR 的值内部要带引号（路径可能有空格），外面再包一层给 schtasks 自己解析，
            // 所以是 "\"C:\...\断联哨兵.exe\" silent"
            string tr = "\\\"" + exe + "\\\" " + SilentArg;

            var args = new List<string>
            {
                "/Create",
                "/TN", TaskName,
                "/TR", tr,
                "/SC", "ONLOGON",     // 当前用户登录时触发（不带 /RU，默认交互式当前用户）
                "/RL", "HIGHEST",     // 最高权限运行 → 开机不弹 UAC
                "/F"                  // 已存在就覆盖，方便升级后重装指向新路径
            };

            // /SC ONLOGON 和 /RL HIGHEST 都要管理员；先查权限，能给出比退出码清楚得多的原因
            if (!IsElevated())
            {
                Log.Write("开机自启安装失败：创建「最高权限」计划任务需要管理员权限，请右键以管理员身份运行本程序后再试。", "Warn");
                return false;
            }

            int code;
            string outp;
            bool ok = Run(args, out code, out outp);

            // 0 = 成功；schtasks 偶尔在任务已存在时也返回 0
            if (ok && code == 0)
            {
                Invalidate();
                Log.Write("已安装开机自启：计划任务 " + TaskName + "（最高权限，开机不弹 UAC）");
                return true;
            }

            Log.Write("开机自启安装失败：schtasks 退出码 " + code + (string.IsNullOrEmpty(outp) ? "" : "，" + outp), "Warn");

            Invalidate();
            return false;
        }

        /// <summary>取消开机自启：删除计划任务。需要管理员权限，失败返回 false。</summary>
        public static bool Uninstall()
        {
            if (!QueryTask())
            {
                // 本来就没有：算成功，调用方不用区分"删掉了"和"本来没有"
                Invalidate();
                Log.Write("未发现开机自启项（计划任务 " + TaskName + " 不存在）");
                return true;
            }

            if (!IsElevated())
            {
                Log.Write("取消开机自启失败：删除计划任务需要管理员权限，请右键以管理员身份运行本程序后再试。", "Warn");
                return false;
            }

            int code;
            string outp;
            bool ok = Run(new List<string> { "/Delete", "/TN", TaskName, "/F" }, out code, out outp);

            Invalidate();

            if (ok && code == 0)
            {
                Log.Write("已取消开机自启：计划任务 " + TaskName + " 已删除");
                return true;
            }

            Log.Write("取消开机自启失败：schtasks 退出码 " + code + (string.IsNullOrEmpty(outp) ? "" : "，" + outp), "Warn");
            return false;
        }

        /// <summary>当前进程是否以管理员身份运行（用来决定要不要提示"请以管理员运行"）。</summary>
        public static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    var p = new WindowsPrincipal(id);
                    return p.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // ============================================================ 内部实现
        /// <summary>exe 完整路径（不是当前工作目录 —— 计划任务里工作目录通常是 System32）。</summary>
        private static string ExePath()
        {
            try
            {
                string p = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try
            {
                // MainModule 拿不到时退回到程序集位置（单 exe 部署下两者一致）
                string p = System.Reflection.Assembly.GetEntryAssembly() != null
                    ? System.Reflection.Assembly.GetEntryAssembly().Location
                    : null;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 计划任务是否存在。用 /Query 的退出码判断：0 = 存在，非 0（通常 1）= 不存在。
        /// schtasks 不存在时（极端裁剪的系统）也返回 false。
        /// </summary>
        private static bool QueryTask()
        {
            int code;
            string outp;
            if (!Run(new List<string> { "/Query", "/TN", TaskName }, out code, out outp)) return false;
            return code == 0;
        }

        /// <summary>
        /// 跑一次 schtasks。返回值表示"进程是否正常跑完"，code 是它的退出码。
        /// 全程隐藏窗口：winexe + CreateNoWindow + 不重定向就不该闪黑框。
        /// </summary>
        private static bool Run(List<string> args, out int code, out string output)
        {
            code = -1;
            output = null;

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = JoinArgs(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                // 必须开重定向，否则设置 StandardOutputEncoding 会直接抛异常：
                // 「只有在重定向标准输出时才支持 StandardOutputEncoding」
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,   // 避免继承一个已被删掉的当前目录
                // schtasks 的错误信息跟着系统 OEM 代码页走。不指定的话 .NET 按 UTF-8 解，
                // 中文 Windows 上会变成乱码（"错误: 拒绝访问。" → "����: �ܾ����ʡ�"），
                // 日志里就完全没法排查了。取 OEM 代码页解回来。
                StandardOutputEncoding = OemEncoding(),
                StandardErrorEncoding = OemEncoding()
            };

            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;

                    // 两个流都用异步事件读：先 ReadToEnd(stdout) 再读 stderr 在管道写满时
                    // 会互相等死锁（子进程阻塞在 stderr 上，父进程阻塞在 stdout 上）。
                    var so = new StringBuilder();
                    var se = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) se.AppendLine(e.Data); };

                    if (!p.Start()) return false;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    bool exited = p.WaitForExit(TimeoutMs);
                    if (exited)
                    {
                        // 再等一次：确保两个异步读取回调把剩下的数据派发完
                        if (!p.WaitForExit(500)) { /* 回调还没结束也不影响：数据通常已经到齐 */ }
                        code = p.ExitCode;
                        output = Compact(so.ToString(), se.ToString());
                        return true;
                    }

                    try { p.Kill(); } catch { }
                    output = "schtasks 超时未返回（" + TimeoutMs + " 毫秒）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Write("调用 schtasks 失败：" + ex.Message, "Warn");
                return false;
            }
        }

        /// <summary>把参数拼成命令行。含空格的参数用引号包起来。</summary>
        private static string JoinArgs(List<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.IndexOf(' ') >= 0) sb.Append('"').Append(a).Append('"');
                else sb.Append(a);
            }
            return sb.ToString();
        }

        /// <summary>把 schtasks 的输出压成一行，便于写进日志。</summary>
        private static string Compact(string stdout, string stderr)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(stdout)) sb.Append(stdout.Trim());
            if (!string.IsNullOrEmpty(stderr))
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(stderr.Trim());
            }
            string s = sb.ToString().Replace("\r", " ").Replace("\n", " ");
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            if (s.Length > 300) s = s.Substring(0, 300) + "…";
            return s.Trim();
        }

        /// <summary>命令行程序的控制台代码页（中文系统是 936）。取不到就退回默认编码。</summary>
        private static Encoding OemEncoding()
        {
            try
            {
                int cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                return Encoding.GetEncoding(cp);
            }
            catch { return Encoding.Default; }
        }
    }
}
