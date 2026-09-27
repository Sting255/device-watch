// ============================================================================
//  Inventory.cs —— 设备清单生成（网页「工具」卡片 / 命令行 inventory）
//
//  对应 DeviceWatch.ps1 里的 Invoke-DeviceInventory（第 5052 行），
//  其中包括原来独立的 设备清单.ps1 —— 它已经并进主程序了。
//
//  【这个文件要解决什么问题】
//  设备管理器里同一只鼠标会显示成 4~5 行：「USB 输入设备」「符合 HID 标准的
//  系统控制器」「HID-compliant mouse」「USB 复合设备」…… 用户根本认不出哪个是哪个。
//  所以报告里的名称列优先用【设备自己上报的型号】（USB 描述符里的 iProduct），
//  读不到才退回 Windows 的通用名。
//
//  【为什么不在这里写 SetupAPI P/Invoke】
//  Device\DevNative.cs 已经有 Details()（内部做了 RAW_DEVPROPKEY 读取和属性缓存），
//  再写一份就等于两个地方的 P/Invoke 要同步维护。这里只消费它的结果。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// 见 Tray.cs 的说明：避开 Microsoft.VisualBasic.Log 的撞名。
using Log = DeviceWatch.Log;

namespace DeviceWatch
{
    /// <summary>清单生成结果。/api/inventory 的返回体就是它的四个字段。</summary>
    public sealed class InventoryResult
    {
        public string File = "";
        public int Count;
        public int Ms;
        public string Text = "";
    }

    public static class Inventory
    {
        /// <summary>报告文件名（与 PowerShell 版一致，放在 exe 同目录）。</summary>
        public const string FileName = "设备清单.txt";

        /// <summary>分隔线宽度，和 PowerShell 版的 ("-" * 100) 对齐。</summary>
        private const int Line = 100;

        // ------------------------------------------------------------------
        //  USB 厂商号对照表
        //  PID 不认识没关系，VID 认出来用户就知道"这是罗技的"，够用了。
        // ------------------------------------------------------------------
        private static readonly Dictionary<string, string> VendorMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "04A5", "明基 BenQ / Acer" }, { "05E3", "Genesys Logic" },
            { "8087", "Intel" },            { "8086", "Intel" },
            { "1BCF", "SunplusIT" },        { "048D", "ITE Tech" },
            { "046D", "罗技 Logitech" },    { "0781", "SanDisk" },
            { "0951", "金士顿 Kingston" },  { "0BC2", "希捷 Seagate" },
            { "1058", "西部数据 WD" },      { "090C", "Silicon Motion" },
            { "13FE", "群联 Phison" },      { "174C", "ASMedia" },
            { "152D", "JMicron" },          { "0BDA", "瑞昱 Realtek" },
            { "0CF3", "高通 Atheros" },     { "04F2", "群光 Chicony" },
            { "5986", "Bison" },            { "0C45", "松翰 Sonix" },
            { "0461", "Primax" },           { "045E", "微软 Microsoft" },
            { "0458", "KYE / Genius" },     { "093A", "PixArt" },
            { "1B1C", "海盗船 Corsair" },   { "1532", "雷蛇 Razer" },
            { "258A", "SINO WEALTH" },      { "0483", "ST" },
            { "0480", "东芝 Toshiba" },     { "04E8", "三星 Samsung" },
            { "0B05", "华硕 ASUS" },        { "1D6B", "根集线器" },
            { "1A40", "Terminus 集线器" },  { "2109", "VIA Labs 集线器" },
            { "214B", "集线器" },           { "0438", "AMD" },
            { "1022", "AMD" },              { "05AC", "Apple" },
            { "413C", "Dell" },             { "03F0", "HP" },
            { "04CA", "Lite-On" },          { "8564", "创见 Transcend" },
            { "058F", "Alcor" },            { "1908", "创见" },
            { "3142", "Fifine / 音频设备" }
        };

        // Windows 的兜底通用名，信息量太低，要让位给更具体的名字。
        // 与 PowerShell 版的 $GenericName 逐条对齐（$ 结尾丢弃，C# 用 IsMatch 判包含即可）。
        private static readonly Regex GenericName = new Regex(
            "^(USB 输入设备|USB 复合设备|USB Composite Device|符合 HID 标准的.*|HID-compliant .*" +
            "|网络控制器|以太网控制器|视频控制器.*|多媒体控制器|SM 总线控制器|PCI 主桥" +
            "|通用串行总线.*控制器|蓝牙外围设备|未知设备|Generic .*|Standard .*|.*控制器" +
            "|卷|磁盘驱动器|基本系统设备)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private const string GenericHint =
            "「USB 输入设备」「符合 HID 标准的供应商定义设备」这类是 Windows 的通用叫法，";

        // ------------------------------------------------------------------
        //  入口
        // ------------------------------------------------------------------
        /// <summary>
        /// 生成设备清单，写到 exe 同目录的「设备清单.txt」（UTF-8 with BOM）。
        /// </summary>
        /// <param name="quiet">true 时不往控制台输出（网页调用走这个分支）。</param>
        public static InventoryResult Run(bool quiet)
        {
            var res = new InventoryResult();
            DateTime t0 = DateTime.Now;

            // Program 可能先调 Inventory.Run() 再调 Log.Init()，所以自己保证日志可用，
            // 免得 Log.Write 撞上还没建好的 logs 目录。
            EnsureLogReady();

            List<string[]> raw;
            try { raw = DevNative.Details(); }
            catch (Exception ex)
            {
                Log.Write("读取设备信息失败：" + ex.Message, "Warn");
                raw = new List<string[]>();
            }
            if (raw == null) raw = new List<string[]>();

            // ---- 列一个稳定的视图，后面排序/分组都基于它，避免反复碰原始数组 ----
            var rows = new List<Row>(raw.Count);
            int reportedCount = 0;
            foreach (string[] x in raw)
            {
                if (x == null || x.Length == 0) continue;
                var r = new Row();
                r.Id = Get(x, 0);
                r.Friendly = Get(x, 1);
                r.Class = Get(x, 2);
                r.Enum = Get(x, 3);
                r.Problem = ParseInt(Get(x, 4));
                r.Reported = Get(x, 6);
                r.Name = BestName(r.Reported, r.Friendly, r.Id);
                if (!string.IsNullOrEmpty(r.Reported)) reportedCount++;
                rows.Add(r);
            }

            string text = Build(rows, reportedCount);
            res.Text = text;
            res.Count = rows.Count;

            // ---- 落盘：UTF-8 with BOM，记事本打开中文才不会乱码 ----
            try
            {
                res.File = Path.Combine(AppRoot(), FileName);
                System.IO.File.WriteAllText(res.File, text, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                res.File = "";
                Log.Write("写设备清单失败：" + ex.Message, "Warn");
            }

            res.Ms = (int)(DateTime.Now - t0).TotalMilliseconds;

            if (!quiet) PrintConsole(rows, res);
            return res;
        }

        // ------------------------------------------------------------------
        //  报告正文
        // ------------------------------------------------------------------
        private static string Build(List<Row> rows, int reportedCount)
        {
            var sb = new StringBuilder(64 * 1024);
            string bar = new string('=', Line);
            string dash = new string('-', Line);

            sb.AppendLine("设备清单  ——  生成于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(string.Format("在线设备：{0} 个      能读到设备自报型号的：{1} 个", rows.Count, reportedCount));
            sb.AppendLine(bar);
            sb.AppendLine();
            sb.AppendLine("说明：名称列优先使用【设备自己上报的型号】，读不到时才退回系统通用名。");
            sb.AppendLine("      " + GenericHint);
            sb.AppendLine("      它们其实是同一个物理设备的不同接口节点。");
            sb.AppendLine();

            // ================= 【一】USB 设备（含集线器）=================
            sb.AppendLine("【一】USB 设备（含集线器）—— 按 VID/PID 分组");
            sb.AppendLine(dash);

            var usb = rows.FindAll(r => string.Equals(r.Enum, "USB", StringComparison.OrdinalIgnoreCase));
            var groups = new SortedDictionary<string, List<Row>>(StringComparer.OrdinalIgnoreCase);
            foreach (Row r in usb)
            {
                string key = UsbNodeKey(r.Id);
                List<Row> list;
                if (!groups.TryGetValue(key, out list)) { list = new List<Row>(); groups[key] = list; }
                list.Add(r);
            }

            foreach (var kv in groups)
            {
                string vid = ExtractVid(kv.Key);
                string vendor = string.IsNullOrEmpty(vid) || !VendorMap.ContainsKey(vid)
                                ? "未收录厂商"
                                : VendorMap[vid];
                sb.AppendLine();
                sb.AppendLine(string.Format("  {0}    [{1}]    {2} 个节点", kv.Key, vendor, kv.Value.Count));

                var list = kv.Value;
                list.Sort(delegate (Row a, Row b) { return string.CompareOrdinal(a.Id, b.Id); });
                foreach (Row r in list)
                {
                    sb.AppendLine("      - " + r.Name.PadRight(46) + "  " + r.Class.PadRight(12)
                                  + (r.Problem != 0 ? "  <故障码 " + r.Problem + ">" : ""));
                }
            }
            if (groups.Count == 0) sb.AppendLine("  （没有 USB 设备）");

            // ================= 【二】有故障码的设备 =================
            sb.AppendLine();
            sb.AppendLine("【二】有故障码的设备");
            sb.AppendLine(dash);
            var bad = rows.FindAll(r => r.Problem != 0);
            bad.Sort(delegate (Row a, Row b) { return a.Problem.CompareTo(b.Problem); });
            if (bad.Count == 0)
            {
                sb.AppendLine("  无 —— 所有设备都工作正常。");
            }
            else
            {
                sb.AppendLine(string.Format("  {0} 个设备带故障码（设备管理器里显示为黄色感叹号）：", bad.Count));
                sb.AppendLine();
                foreach (Row r in bad)
                {
                    sb.AppendLine(string.Format("  故障码 {0,-4} {1}", r.Problem, r.Name));
                    sb.AppendLine("            " + r.Id);
                }
            }

            // ================= 【三】全部设备明细 =================
            sb.AppendLine();
            sb.AppendLine("【三】全部设备明细（按类别排序）");
            sb.AppendLine(dash);
            var all = new List<Row>(rows);
            all.Sort(delegate (Row a, Row b)
            {
                // 没有类别的（HTREE\ROOT\0、BTHENUM 的匿名枚举项等）排到最后：
                // 它们只是几条匿名虚拟节点，放在开头会把真正要看的东西顶下去。
                bool ea = string.IsNullOrEmpty(a.Class), eb = string.IsNullOrEmpty(b.Class);
                if (ea != eb) return ea ? 1 : -1;
                int c = string.CompareOrdinal(a.Class, b.Class);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Name, b.Name);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Id, b.Id);
            });
            foreach (Row r in all)
            {
                sb.AppendLine("  " + r.Name.PadRight(46) + "  " + r.Id);
            }

            sb.AppendLine();
            sb.AppendLine(bar);
            sb.AppendLine("提示：排查问题把这个文件发给技术支持，【三】里有完整实例 ID。");
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  控制台摘要（quiet=false 时才走这里）
        // ------------------------------------------------------------------
        private static void PrintConsole(List<Row> rows, InventoryResult res)
        {
            try
            {
                var sb = new StringBuilder();

                sb.AppendLine();
                sb.AppendLine("── USB 设备（按 VID/PID）──");
                var groups = new SortedDictionary<string, List<Row>>(StringComparer.OrdinalIgnoreCase);
                foreach (Row r in rows)
                {
                    if (!string.Equals(r.Enum, "USB", StringComparison.OrdinalIgnoreCase)) continue;
                    string key = UsbNodeKey(r.Id);
                    List<Row> list;
                    if (!groups.TryGetValue(key, out list)) { list = new List<Row>(); groups[key] = list; }
                    list.Add(r);
                }
                foreach (var kv in groups)
                {
                    string vid = ExtractVid(kv.Key);
                    string vendor = string.IsNullOrEmpty(vid) || !VendorMap.ContainsKey(vid)
                                    ? "未收录" : VendorMap[vid];
                    string name = null;
                    foreach (Row r in kv.Value)
                    {
                        if (!string.IsNullOrEmpty(r.Reported)) { name = r.Reported; break; }
                    }
                    if (string.IsNullOrEmpty(name)) name = kv.Value[0].Name;
                    sb.AppendLine("  " + kv.Key.PadRight(24) + " " + vendor.PadRight(18) + " " + name);
                }

                sb.AppendLine();
                sb.AppendLine("── 有故障码的设备 ──");
                var bad = rows.FindAll(r => r.Problem != 0);
                if (bad.Count == 0) sb.AppendLine("  无");
                else
                {
                    foreach (Row r in bad)
                        sb.AppendLine("  " + r.Name.PadRight(40) + " 故障码 " + r.Problem);
                }

                sb.AppendLine();
                sb.AppendLine("设备清单已生成：" + res.File);
                sb.AppendLine(string.Format("共 {0} 个在线设备，耗时 {1} 毫秒", res.Count, res.Ms));
                Console.Write(sb.ToString());
                Console.Out.Flush();
            }
            catch { /* winexe 下没有控制台，写不出去就算了 */ }
        }

        // ------------------------------------------------------------------
        //  工具函数
        // ------------------------------------------------------------------
        /// <summary>设备清单放在 exe 同目录 —— AppState、settings.json、logs 都在那儿。</summary>
        private static string AppRoot()
        {
            try { return AppDomain.CurrentDomain.BaseDirectory; }
            catch { return Environment.CurrentDirectory; }
        }

        /// <summary>
        /// 保证 Log 可用：logs 目录存在，并且（如果 Log 定义了 Init）已经初始化过。
        /// 用反射调 Init 是刻意的 —— Log 由别的模块负责，它加不加 Init 这个方法
        /// 不该让设备清单编译不过。
        /// </summary>
        private static void EnsureLogReady()
        {
            try
            {
                string dir = Log.LogDir;
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { /* 日志目录建不出来不影响清单生成 */ }

            try
            {
                var mi = typeof(Log).GetMethod("Init",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (mi != null) mi.Invoke(null, null);
            }
            catch { }
        }

        private static string Get(string[] a, int i)
        {
            if (a == null || i < 0 || i >= a.Length) return "";
            return a[i] == null ? "" : a[i];
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        /// <summary>从实例 ID 里取 VID 号（大写），取不到返回空串。</summary>
        public static string ExtractVid(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            Match m = Regex.Match(id, "VID_([0-9A-Fa-f]{4})");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        /// <summary>USB\VID_046D&amp;PID_C534\5&amp;2F8... → VID_046D&amp;PID_C534（分组键）。</summary>
        public static string UsbNodeKey(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string[] parts = id.Split('\\');
            if (parts.Length >= 2 && parts[1].Length > 0) return parts[1];
            return id;
        }

        /// <summary>
        /// 名称优先级：设备自报型号 &gt; 系统友好名 &gt; 系统描述 &gt; 实例 ID 末段。
        /// 只有"非通用名"的候选才会被采用；全是通用名时退回第一个非空候选，
        /// 至少比"USB\VID_0000&amp;PID_0002\5&amp;1"这种给人看。
        /// </summary>
        public static string BestName(string reported, string friendly, string id)
        {
            string lastSeg = "";
            if (!string.IsNullOrEmpty(id))
            {
                int p = id.LastIndexOf('\\');
                if (p >= 0 && p + 1 < id.Length) lastSeg = id.Substring(p + 1);
                else lastSeg = id;
            }

            // 顺序有讲究：USB 设备自报型号最准，但 PCI 设备自报的往往是
            // "以太网控制器"这种兜底名，所以自报排第一、系统名排第二，
            // 谁不是通用名就先用谁。
            string[] cands = { reported, friendly, lastSeg };
            foreach (string c in cands)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                if (GenericName.IsMatch(c)) continue;
                return c;
            }
            // 全是通用名：自报型号仍然比"USB 输入设备"具体，优先给它
            if (!string.IsNullOrWhiteSpace(reported)) return reported;
            return lastSeg;
        }

        /// <summary>报告里的一行设备。</summary>
        private sealed class Row
        {
            public string Id = "";
            public string Name = "";
            public string Class = "";
            public string Enum = "";
            public string Reported = "";
            public string Friendly = "";
            public int Problem;
        }
    }
}
