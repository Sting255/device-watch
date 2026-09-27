// ============================================================================
//  Log.cs —— 日志：文本日志 / events.csv / summary.txt
//
//  为什么这么写：
//   1. 追加写 + 立即 Flush。监控工具最怕的就是"程序被任务管理器强杀，最后几条
//      关键日志没落盘"——那几条恰恰是崩溃线索，所以不攒缓冲区。
//   2. 每次写之前查一次文件大小，超过 MaxLogFileMB 就切成 .1.log。
//      比常驻一个 timer 简单，也不会在高频写日志时反复 stat 之外多花什么。
//   3. lock 串住"查大小 → 打开 → 写 → Flush"整段：采集线程、网页线程、
//      看门狗线程会同时写日志，不锁会写出半行或者把切分搞乱。
//   4. UTF-8 with BOM：记事本、Excel 双击打开都不乱码（后端契约明确要求）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeviceWatch
{
    public static class Log
    {
        /// <summary>所有写文件的操作都在这把锁里做，保证不会写出半行日志。</summary>
        private static readonly object _lock = new object();

        /// <summary>日志文件名前缀，切分时也用它。</summary>
        private const string FilePrefix = "DeviceWatch-";

        /// <summary>UTF-8 带 BOM。记事本打开不乱码；events.csv 用 Excel 打开也不乱码。</summary>
        private static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);

        /// <summary>日志超过这个体积就切分（MB）。0 = 不切分。</summary>
        private const int DefaultMaxLogFileMB = 20;

        private static string _dir;          // 缓存目录，避免每次都拼字符串
        private static string _currentFile;  // 当前正在写的日志文件（切分后会变）
        private static long _bytes;          // 当前文件已写字节数（启动时数一次，之后自己累加）
        private static bool _inited;
        private static bool _consoleOut;     // debug 模式：日志同时打到控制台

        // ============================================================ 路径
        /// <summary>&lt;exe目录&gt;\logs</summary>
        public static string LogDir
        {
            get
            {
                if (_dir == null) _dir = Path.Combine(Settings.Root, "logs");
                return _dir;
            }
        }

        /// <summary>&lt;exe目录&gt;\logs\DeviceWatch-YYYY-MM-DD.log（每次取都是当天的文件）</summary>
        public static string TodayFile
        {
            get { return Path.Combine(LogDir, FilePrefix + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"); }
        }

        /// <summary>&lt;exe目录&gt;\logs\summary.txt（退出时写的统计）</summary>
        public static string StatsFile
        {
            get { return Path.Combine(LogDir, "summary.txt"); }
        }

        /// <summary>&lt;exe目录&gt;\logs\events.csv（所有设备变动，Excel 能打开）</summary>
        public static string CsvFile
        {
            get { return Path.Combine(LogDir, "events.csv"); }
        }

        /// <summary>debug 模式：日志同时打到控制台，方便当场看。</summary>
        public static void SetConsole(bool on) { _consoleOut = on; }

        // ============================================================ 初始化
        /// <summary>
        /// 建日志目录 + 按 LogRetentionDays 清理过期日志。
        /// 必须在 Settings.Load() 之后调用；多调几次没关系（幂等）。
        /// </summary>
        public static void Init()
        {
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
                }
                catch { /* 目录建不出来（只读盘 / 权限）：后面每次写都会失败被吞掉，程序照常跑 */ }

                _currentFile = TodayFile;
                _bytes = FileSize(_currentFile);
                _inited = true;
            }
            CleanOldLogs();
        }

        /// <summary>
        /// 立即切分当前日志（超出上限时自动调用；把 MaxLogFileMB 调小之后也可以调一次让它马上生效）。
        /// 把今天的日志改名成 DeviceWatch-日期.1.log，再开一个空文件继续写。
        /// </summary>
        public static void RotateNow()
        {
            lock (_lock)
            {
                if (!_inited) { _currentFile = TodayFile; _bytes = FileSize(_currentFile); _inited = true; }
                RotateCurrent();
            }
        }

        /// <summary>
        /// 删除超过 LogRetentionDays 天的日志。LogRetentionDays = 0 表示永久保留。
        /// 按"文件名里的日期"判断而不是文件修改时间：日志文件被复制 / 备份还原后
        /// 修改时间会变，但名字里的日期不会。
        /// </summary>
        public static void CleanOldLogs()
        {
            try
            {
                int keep = Settings.I.LogRetentionDays;
                if (keep <= 0) return;
                if (!Directory.Exists(LogDir)) return;

                DateTime limit = DateTime.Now.Date.AddDays(-keep);
                foreach (string f in Directory.GetFiles(LogDir, FilePrefix + "*.log"))
                {
                    DateTime d;
                    if (ParseFileDate(Path.GetFileName(f), out d) && d < limit)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>从 DeviceWatch-2026-09-27.log / DeviceWatch-2026-09-27.1.log 里取日期。</summary>
        private static bool ParseFileDate(string name, out DateTime d)
        {
            d = DateTime.MinValue;
            if (name == null || !name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)) return false;
            string s = name.Substring(FilePrefix.Length);
            if (s.Length < 10) return false;
            return DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }

        // ============================================================ 写日志
        /// <summary>写一行日志（Info 级）。格式：yyyy-MM-dd HH:mm:ss.fff + 两个空格 + 正文</summary>
        public static void Write(string msg) { Write(msg, "Info"); }

        /// <summary>
        /// 写一行日志。kind：Info / Warn / Error（其余值按 Info 处理）。
        /// Warn / Error 会在正文前加 [警告] / [错误]，便于在日志里一眼搜出来。
        /// </summary>
        public static void Write(string msg, string kind)
        {
            if (msg == null) msg = "";
            if (kind == null) kind = "Info";

            string tag = "";
            if (string.Equals(kind, "Warn", StringComparison.OrdinalIgnoreCase)) tag = "[警告] ";
            else if (string.Equals(kind, "Error", StringComparison.OrdinalIgnoreCase)) tag = "[错误] ";

            string line = Stamp() + "  " + tag + msg;

            lock (_lock)
            {
                AppendLine(line);
            }

            if (_consoleOut)
            {
                try
                {
                    ConsoleColor old = Console.ForegroundColor;
                    Console.ForegroundColor = ColorOf(kind);
                    Console.WriteLine(line);
                    Console.ForegroundColor = old;
                }
                catch { }
            }
        }

        private static ConsoleColor ColorOf(string kind)
        {
            if (string.Equals(kind, "Warn", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.Yellow;
            if (string.Equals(kind, "Error", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.Magenta;
            if (string.Equals(kind, "On", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.Green;
            if (string.Equals(kind, "Off", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.Red;
            if (string.Equals(kind, "Net", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.Cyan;
            return ConsoleColor.Gray;
        }

        private static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture); }

        /// <summary>把一行文本追加到当前日志文件（调用方必须已经持有 _lock）。</summary>
        private static void AppendLine(string line)
        {
            try
            {
                if (!_inited) { _currentFile = TodayFile; _bytes = FileSize(_currentFile); _inited = true; }
                if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);

                // 跨天：换到今天的文件，并且重新量一次体积
                string today = TodayFile;
                if (!string.Equals(_currentFile, today, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFile = today;
                    _bytes = FileSize(_currentFile);
                }

                // 自己累加字节数，不用每次去 stat：日志行普遍很短，按行数估会严重低估体积
                int add = Utf8Bom.GetByteCount(line) + 2;   // +2 = "\r\n"
                RotateIfTooBig(add);

                // AppendAllText 内部会写完就关流，等于立刻 Flush；
                // 程序被强杀时最多丢正在写的这一行，不会丢掉一整个缓冲区。
                File.AppendAllText(_currentFile, line + "\r\n", Utf8Bom);
                _bytes += add;
            }
            catch { /* 磁盘满 / 文件被占用：宁可丢日志也不能让监控主流程挂掉 */ }
        }

        /// <summary>写下一行后会不会超上限；会超就先把当前文件切走。</summary>
        private static void RotateIfTooBig(int nextLen)
        {
            int maxMB = Settings.I.IsLoaded ? Settings.I.MaxLogFileMB : DefaultMaxLogFileMB;
            if (maxMB <= 0) return;
            if (_bytes + nextLen < (long)maxMB * 1024 * 1024) return;
            RotateCurrent();
        }

        private static void RotateCurrent()
        {
            try
            {
                if (!File.Exists(_currentFile)) return;

                string stem = _currentFile.Substring(0, _currentFile.Length - 4);   // 去掉 .log
                string rotated = stem + ".1.log";
                if (File.Exists(rotated)) File.Delete(rotated);   // 只留最近一次切分，避免无限堆积
                File.Move(_currentFile, rotated);
                _bytes = 0;
            }
            catch { }
        }

        private static long FileSize(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        // ============================================================ events.csv
        /// <summary>
        /// 追加一条设备变动到 events.csv（第一次写时自动补表头）。
        /// 时间,类型,设备名称,类别,设备实例ID,详情
        /// </summary>
        public static void WriteEvent(string kind, string name, string id, string detail)
        {
            WriteEvent(kind, name, "", id, detail);
        }

        /// <summary>完整版：多一个"类别"列（USB / HID / 网卡 …）。</summary>
        public static void WriteEvent(string kind, string name, string cls, string id, string detail)
        {
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
                    bool needHeader = !File.Exists(CsvFile) || new FileInfo(CsvFile).Length == 0;

                    var sb = new StringBuilder();
                    if (needHeader) sb.Append("时间,类型,设备名称,类别,设备实例ID,详情\r\n");

                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                      .Append(Csv(kind)).Append(',')
                      .Append(Csv(name)).Append(',')
                      .Append(Csv(cls)).Append(',')
                      .Append(Csv(id)).Append(',')
                      .Append(Csv(detail)).Append("\r\n");

                    File.AppendAllText(CsvFile, sb.ToString(), Utf8Bom);
                }
            }
            catch { }
        }

        /// <summary>
        /// CSV 单元格：始终加引号（和 PowerShell 版一致），内部引号换成单引号，
        /// 顺手去掉换行（否则一行记录会被 Excel 拆成多行）。
        /// 以 = + - @ 开头的值前面补一个单引号，防止 Excel 当成公式执行。
        /// </summary>
        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            s = s.Replace('"', '\'').Replace("\r", " ").Replace("\n", " ");
            if (s.Length > 0)
            {
                char c = s[0];
                if (c == '=' || c == '+' || c == '-' || c == '@') s = "'" + s;
            }
            return "\"" + s + "\"";
        }

        // ============================================================ summary.txt
        /// <summary>把一段统计文本追加到 summary.txt（退出时调用）。</summary>
        public static void WriteStats(string text)
        {
            if (text == null) text = "";
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
                    File.AppendAllText(StatsFile, text + "\r\n", Utf8Bom);
                }
            }
            catch { }
        }

        /// <summary>取最近 n 行日志（网页"最近日志"用；读不到就返回空表）。</summary>
        public static List<string> Tail(int n)
        {
            var outp = new List<string>();
            if (n <= 0) return outp;
            try
            {
                string path = TodayFile;
                if (!File.Exists(path)) return outp;
                var all = new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string l;
                    while ((l = sr.ReadLine()) != null) all.Add(l);
                }
                int start = all.Count > n ? all.Count - n : 0;
                for (int i = start; i < all.Count; i++) outp.Add(all[i]);
            }
            catch { }
            return outp;
        }
    }
}
