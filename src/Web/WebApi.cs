// ============================================================================
//  WebApi.cs —— 网页后端的所有路由与处理逻辑
//
//  路径、返回结构严格对齐 src\_contract\CONTRACT.md 的「HTTP 接口」表；
//  数据全部来自 AppState.I / Settings.I，本文件不采集任何硬件或设备数据。
//
//  性能红线（PowerShell 版的实测教训）：
//    GET /api/state 每秒被前端轮询一次，所以它**只允许读内存里的字段**。
//    不许查 WMI、不许读文件、不许启动进程、不许查计划任务。
//    autostart 直接读 AppState.I.AutostartInstalled 缓存值。
//    （PowerShell 版曾在 /api/state 里放了一次 500ms 的 Get-ScheduledTask，
//      结果主循环每秒被卡 4.5 秒，设备插拔检测延迟到好几秒。）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;

namespace DeviceWatch
{
    public static class WebApi
    {
        /// <summary>网页前端在 exe 里的嵌入资源名（见 DeviceWatch.csproj 的 LogicalName）。</summary>
        private const string HtmlResource = "DeviceWatch.ui.index.html";

        /// <summary>请求体最大字节数：本机接口，超过这个数肯定是哪里不对，直接拒掉。</summary>
        private const int MaxBodyBytes = 4 * 1024 * 1024;

        // 网页只读一次，之后一直用缓存（不要每个请求都读一遍嵌入资源）
        private static string _htmlCache;
        private static bool _htmlTried;

        // ====================================================================
        //  入口
        // ====================================================================

        /// <summary>处理一个已接受的请求。异常由 WebServer.Pump() 兜底，这里只管正常返回。</summary>
        public static void Handle(HttpListenerContext ctx)
        {
            if (ctx == null) return;
            try
            {
                Route(ctx);
            }
            catch (Exception ex)
            {
                // 单个接口出错不能让服务器挂掉，也不能给前端一个空响应
                Log.Write("网页接口出错：" + ex.Message, "网页");
                try { SendJson(ctx, 500, Fail("服务器内部错误：" + ex.Message)); }
                catch { /* 客户端已经断开 */ }
            }
        }

        private static void Route(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;

            string path = "/";
            try { if (req.Url != null) path = req.Url.LocalPath; }
            catch { path = "/"; }
            if (string.IsNullOrEmpty(path)) path = "/";
            if (path.Length > 1 && path[path.Length - 1] == '/') path = path.TrimEnd('/');
            if (path.Length == 0) path = "/";

            string method = req.HttpMethod == null ? "GET" : req.HttpMethod.ToUpperInvariant();
            bool isPost = method == "POST";

            switch (path.ToLowerInvariant())
            {
                case "/":
                case "/index.html":
                    SendHtml(ctx, IndexHtml());
                    return;

                case "/favicon.ico":
                    SendNoContent(ctx);                     // 204，让浏览器别再纠缠
                    return;

                case "/api/state":
                    SendJson(ctx, StateJson());             // 只读内存，见文件头说明
                    return;

                case "/api/devices":
                    SendJson(ctx, DevicesJson());
                    return;

                case "/api/settings":
                    if (isPost) PostSettings(ctx);
                    else SendJson(ctx, SettingsJson());
                    return;

                case "/api/notify":
                    Notify(ctx);
                    return;

                case "/api/autostart":
                    if (isPost) PostAutostart(ctx);
                    else SendJson(ctx, AutostartJson());
                    return;

                case "/api/inventory":
                    InventoryRoute(ctx);
                    return;

                case "/api/openlogs":
                    OpenLogs(ctx);
                    return;

                case "/api/mode":
                    if (isPost) PostMode(ctx);
                    else SendJson(ctx, Ok("mode", AppState.I.Mode));
                    return;

                case "/api/rule":
                    Rule(ctx);
                    return;

                default:
                    SendJson(ctx, 404, Fail("没有这个接口：" + path));
                    return;
            }
        }

        // ====================================================================
        //  GET /api/state
        //
        //  顶层 23 个字段、hw 41 个字段、ext 7 个字段，全部照 CONTRACT.md 拼。
        //  全部来自内存，耗时在微秒级 —— 因为它每秒被调用一次。
        // ====================================================================

        private static string StateJson()
        {
            AppState s = AppState.I;
            var d = new Dictionary<string, object>();

            // 只加锁保护快照一致性，锁里只有内存操作，不做任何采集
            lock (s.Sync)
            {
                d["ok"] = true;
                d["now"] = DateTime.Now.ToString("HH:mm:ss");
                d["start"] = s.StartTime.ToString("HH:mm:ss");
                d["uptime"] = s.UptimeText;
                d["devices"] = s.DeviceCount;
                d["problems"] = s.ProblemCount;
                d["problemList"] = ProblemListJson(s.ProblemList);
                d["benignList"] = BenignListJson(s.BenignList);
                d["eventCount"] = s.EventCount;
                d["netDown"] = s.NetDown;
                d["notify"] = s.NotifyEnabled;
                d["learning"] = s.Learning;

                // internet：{ state, detail, targets[] }
                var internet = new Dictionary<string, object>();
                internet["state"] = string.IsNullOrEmpty(s.InternetState) ? "未知" : s.InternetState;
                internet["detail"] = s.InternetDetail == null ? "" : s.InternetDetail;
                var targets = new List<object>();
                if (s.PingTargets != null)
                {
                    for (int i = 0; i < s.PingTargets.Count; i++)
                    {
                        PingTarget t = s.PingTargets[i];
                        if (t != null) targets.Add(t.ToJson());
                    }
                }
                internet["targets"] = targets;
                d["internet"] = internet;

                // adapters：[{ name, state, speed }]
                var adapters = new List<object>();
                if (s.Adapters != null)
                {
                    for (int i = 0; i < s.Adapters.Count; i++)
                    {
                        AdapterInfo a = s.Adapters[i];
                        if (a != null) adapters.Add(a.ToJson());
                    }
                }
                d["adapters"] = adapters;

                // hw：字段名大小写严格照 CONTRACT.md（错一个字母前端就显示空白）
                d["hw"] = s.Hw != null ? s.Hw.ToJson() : new HwState().ToJson();

                d["mode"] = string.IsNullOrEmpty(s.Mode) ? "All" : s.Mode;
                // autostart：必须用缓存值！这里查一次计划任务就是 500ms，会被每秒钟的轮询放大
                d["autostart"] = s.AutostartInstalled;
                d["hwMetric"] = string.IsNullOrEmpty(s.HwMetric) ? "both" : s.HwMetric;

                // ext：低层传感器，null = 未启用
                d["ext"] = s.Ext != null ? (object)s.Ext.ToJson() : null;

                d["uiLagMs"] = s.UiLagMs;

                // events：最近事件，最多 100 条（AppState 自己截断）
                var events = new List<object>();
                if (s.Recent != null)
                {
                    for (int i = 0; i < s.Recent.Count; i++)
                    {
                        DeviceEvent e = s.Recent[i];
                        if (e != null) events.Add(e.ToJson());
                    }
                }
                d["events"] = events;

                // stats：断联次数多的排前面（前端拿第一条当柱状图的基准）
                var stats = new List<DeviceStat>();
                if (s.Stats != null)
                {
                    foreach (KeyValuePair<string, DeviceStat> kv in s.Stats)
                    {
                        if (kv.Value != null) stats.Add(kv.Value);
                    }
                }
                stats.Sort(delegate (DeviceStat a, DeviceStat b)
                {
                    int c = b.Off.CompareTo(a.Off);
                    if (c != 0) return c;
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });
                var statJson = new List<object>();
                for (int i = 0; i < stats.Count; i++) statJson.Add(stats[i].ToJson());
                d["stats"] = statJson;
            }

            return Json(d);
        }

        /// <summary>
        /// 异常设备列表。契约字段是 name/id/problem/class；
        /// 前端 index.html 实际读的是 name/cls/text（第 390 行）和 name/id，
        /// 所以两套键名都给，谁都不会拿到 undefined。
        /// </summary>
        private static List<object> ProblemListJson(List<ProblemDevice> list)
        {
            var outp = new List<object>();
            if (list == null) return outp;
            for (int i = 0; i < list.Count; i++)
            {
                ProblemDevice p = list[i];
                if (p == null) continue;
                string cls = p.Class == null ? "" : p.Class;
                var o = new Dictionary<string, object>();
                o["name"] = p.Name == null ? "" : p.Name;
                o["id"] = p.Id == null ? "" : p.Id;
                o["problem"] = p.Problem;
                o["class"] = cls;
                o["cls"] = cls;                          // 前端字段
                o["text"] = ProblemText(p.Problem);      // 前端字段：给用户看的中文说明
                outp.Add(o);
            }
            return outp;
        }

        /// <summary>
        /// 已知无害的列表（例如"设备已被禁用"）。
        /// 契约字段是 name/id/problem；前端还读一个 code（第 396 行显示"代码 xx"）。
        /// </summary>
        private static List<object> BenignListJson(List<ProblemDevice> list)
        {
            var outp = new List<object>();
            if (list == null) return outp;
            for (int i = 0; i < list.Count; i++)
            {
                ProblemDevice p = list[i];
                if (p == null) continue;
                var o = new Dictionary<string, object>();
                o["name"] = p.Name == null ? "" : p.Name;
                o["id"] = p.Id == null ? "" : p.Id;
                o["problem"] = p.Problem;
                o["code"] = p.Problem;                   // 前端字段
                outp.Add(o);
            }
            return outp;
        }

        /// <summary>把设备的"问题代码"翻成中文，别让用户自己猜 28 是什么意思。</summary>
        private static string ProblemText(int code)
        {
            string desc;
            switch (code)
            {
                case 1: desc = "设备未配置"; break;
                case 3: desc = "驱动已损坏"; break;
                case 10: desc = "设备无法启动"; break;
                case 12: desc = "资源不足"; break;
                case 14: desc = "需要重启计算机"; break;
                case 18: desc = "需要重新安装驱动"; break;
                case 19: desc = "注册表信息不完整"; break;
                case 21: desc = "系统正在移除该设备"; break;
                case 22: desc = "设备已被禁用"; break;
                case 23: desc = "系统正在启动该设备"; break;
                case 24: desc = "设备未安装"; break;
                case 28: desc = "驱动未安装"; break;
                case 43: desc = "已被其它程序停止"; break;
                default: desc = null; break;
            }
            if (code == 0) return "正常";
            return desc == null
                ? ("代码 " + code.ToString(CultureInfo.InvariantCulture))
                : ("代码 " + code.ToString(CultureInfo.InvariantCulture) + "：" + desc);
        }

        // ====================================================================
        //  GET /api/devices  ——  { count, devices:[{name,cls,id,problem}] }（约 40KB）
        // ====================================================================

        private static string DevicesJson()
        {
            AppState s = AppState.I;
            var entries = new List<DeviceEntry>();

            lock (s.Sync)
            {
                foreach (KeyValuePair<string, DeviceEntry> kv in s.Known)
                {
                    if (kv.Value != null) entries.Add(kv.Value);
                }
            }

            entries.Sort(delegate (DeviceEntry a, DeviceEntry b)
            {
                return string.Compare(DeviceName(a, null), DeviceName(b, null), StringComparison.CurrentCultureIgnoreCase);
            });

            var devices = new List<object>();
            for (int i = 0; i < entries.Count; i++)
            {
                DeviceEntry e = entries[i];
                var o = new Dictionary<string, object>();
                o["name"] = DeviceName(e, e.Id);
                o["cls"] = e.Class == null ? "" : e.Class;
                o["id"] = e.Id == null ? "" : e.Id;
                o["problem"] = e.Problem;
                devices.Add(o);
            }

            var d = new Dictionary<string, object>();
            d["count"] = devices.Count;
            d["devices"] = devices;
            return Json(d);
        }

        /// <summary>优先用友好名，其次设备名，最后描述 / 实例 ID —— 尽量别给前端空字符串。</summary>
        private static string DeviceName(DeviceEntry e, string fallback)
        {
            if (e != null)
            {
                if (!string.IsNullOrEmpty(e.Friendly)) return e.Friendly;
                if (!string.IsNullOrEmpty(e.Name)) return e.Name;
                if (!string.IsNullOrEmpty(e.Desc)) return e.Desc;
                if (!string.IsNullOrEmpty(e.Id)) return e.Id;
            }
            return fallback == null ? "" : fallback;
        }

        // ====================================================================
        //  /api/settings
        //
        //  schema（30 项、7 个分组、每项的范围和中文说明）由 Settings.BuildSchema()
        //  统一提供：那是唯一的一份定义，Apply 的校验和这里的展示都从它取数，
        //  不会出现"网页写着 100~5000、后端却按别的范围校验"这种两处不一致。
        // ====================================================================

        private static string SettingsJson()
        {
            Dictionary<string, object> root;
            try
            {
                root = Settings.I.BuildSchema();
            }
            catch (Exception ex)
            {
                Log.Write("生成设置 schema 失败：" + ex.Message, "网页");
                return Json(Fail("读取设置失败：" + ex.Message));
            }

            if (root == null) return Json(Fail("读取设置失败：没有拿到 schema"));

            string s = Json(root);
            return s;
        }

        /// <summary>
        /// POST /api/settings：body 是"部分设置"的对象。
        /// 成功 → {"ok":true,"applied":[...],"errors":[],"needRestart":[]}
        /// 越界 → {"ok":false,"applied":[],"errors":["PollMs: 数值超出范围，应在 100 ~ 5000 之间"],...}
        /// 坏 JSON → 带 err 的失败响应（HTTP 200，绝不 500）
        /// </summary>
        private static void PostSettings(HttpListenerContext ctx)
        {
            string body = ReadBodyText(ctx);

            Dictionary<string, object> obj;
            try
            {
                obj = JsonParser.Parse(body) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                SendJson(ctx, Fail("请求体不是合法的 JSON：" + ex.Message));
                return;
            }
            if (obj == null)
            {
                SendJson(ctx, Fail("请求体必须是一个 JSON 对象，例如 {\"PollMs\":300}"));
                return;
            }

            var applied = new List<string>();
            var errors = new List<string>();
            var needRestart = new List<string>();

            foreach (KeyValuePair<string, object> kv in obj)
            {
                string key = kv.Key;
                if (string.IsNullOrEmpty(key)) continue;

                // 校验 + 落值都交给 Settings.Apply：
                // 范围、类型、错误文案全部由它那份 schema 决定，网页这边不复制第二份。
                string err;
                bool restart;
                bool ok;
                try
                {
                    ok = Settings.I.Apply(key, kv.Value, out err, out restart);
                }
                catch (Exception ex)
                {
                    ok = false;
                    restart = false;
                    err = key + "：设置失败（" + ex.Message + "）";
                    Log.Write("应用设置 " + key + " 出错：" + ex.Message, "网页");
                }

                if (!ok)
                {
                    errors.Add(string.IsNullOrEmpty(err) ? (key + "：设置失败") : err);
                    continue;
                }

                applied.Add(key);
                if (restart) needRestart.Add(key);
                SyncLiveState(key, kv.Value);
            }

            if (applied.Count > 0)
            {
                try { Settings.I.Save(); }
                catch (Exception ex) { errors.Add("写配置文件失败：" + ex.Message); }
            }

            var d = new Dictionary<string, object>();
            d["ok"] = errors.Count == 0;
            d["applied"] = applied;
            d["errors"] = errors;
            d["needRestart"] = needRestart;
            SendJson(ctx, d);
        }

        /// <summary>设置生效后需要立刻反映到运行状态里的那几项（网页上的开关要立刻见效）。</summary>
        private static void SyncLiveState(string key, object value)
        {
            if (key == null) return;
            switch (key.ToLowerInvariant())
            {
                case "notify":
                    AppState.I.NotifyEnabled = ToBool(value, true);
                    break;
                case "hwmetric":
                    AppState.I.HwMetric = ToStr(value);
                    break;
                case "runmode":
                    AppState.I.Mode = ToStr(value);
                    break;
            }
        }

        // ====================================================================
        //  /api/notify?on=0|1
        // ====================================================================

        private static void Notify(HttpListenerContext ctx)
        {
            string on = null;
            try { on = ctx.Request.QueryString["on"]; } catch { }

            bool value;
            if (string.IsNullOrEmpty(on)) value = !AppState.I.NotifyEnabled;      // 没带参数就当成"切换"
            else value = !(on == "0" || string.Equals(on, "false", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(on, "off", StringComparison.OrdinalIgnoreCase));

            AppState.I.NotifyEnabled = value;

            // 顺手写回设置文件，重启后保持用户的选择
            string err;
            if (SetSetting("Notify", value, out err))
            {
                try { Settings.I.Save(); }
                catch (Exception ex) { Log.Write("保存弹窗开关失败：" + ex.Message, "网页"); }
            }

            // 返回结构严格照契约：{"notify":true}（抓到的样本是 {"notify":false}）
            var d = new Dictionary<string, object>();
            d["notify"] = value;
            SendJson(ctx, d);
        }

        // ====================================================================
        //  /api/autostart   GET { ok, installed } / POST { on }
        // ====================================================================

        private static string AutostartJson()
        {
            bool installed = AppState.I.AutostartInstalled;
            try
            {
                installed = Autostart.IsInstalled();
                AppState.I.AutostartInstalled = installed;      // 顺便刷新缓存，/api/state 就不用查了
            }
            catch (Exception ex)
            {
                Log.Write("查询开机自启状态失败：" + ex.Message, "网页");
            }
            return Json(Ok("installed", installed));
        }

        private static void PostAutostart(HttpListenerContext ctx)
        {
            string body = ReadBodyText(ctx);
            bool on = true;

            if (!string.IsNullOrEmpty(body))
            {
                Dictionary<string, object> o;
                try { o = JsonParser.Parse(body) as Dictionary<string, object>; }
                catch (Exception ex)
                {
                    SendJson(ctx, Fail("请求体不是合法的 JSON：" + ex.Message));
                    return;
                }
                if (o == null)
                {
                    SendJson(ctx, Fail("请求体必须是一个 JSON 对象，例如 {\"on\":true}"));
                    return;
                }
                on = JsonParser.GetBool(o, "on", true);
            }

            bool ok;
            string err = null;
            try
            {
                ok = on ? Autostart.Install() : Autostart.Uninstall();
                if (!ok) err = on ? "安装开机自启失败" : "取消开机自启失败";
            }
            catch (Exception ex)
            {
                ok = false;
                err = ex.Message;
                Log.Write("切换开机自启失败：" + err, "网页");
            }

            if (ok) AppState.I.AutostartInstalled = on;

            if (!ok)
            {
                SendJson(ctx, Fail(err == null ? "操作失败" : err));
                return;
            }
            SendJson(ctx, Ok("installed", on));
        }

        // ====================================================================
        //  GET /api/inventory
        //
        //  这是唯一一个"故意很慢"的接口（要枚举几百个设备的详情，几秒级），
        //  前端自己会提示"正在读取设备信息，大约需要几秒…"。除此以外所有接口都必须快。
        // ====================================================================

        private static void InventoryRoute(HttpListenerContext ctx)
        {
            try
            {
                InventoryResult r = Inventory.Run(true);        // quiet=true：网页触发时不用往控制台刷进度
                if (r == null)
                {
                    SendJson(ctx, Fail("生成设备清单失败：没有拿到结果"));
                    return;
                }

                var d = new Dictionary<string, object>();
                d["ok"] = true;
                d["file"] = r.File == null ? "" : r.File;
                d["count"] = r.Count;
                d["ms"] = r.Ms;
                d["text"] = r.Text == null ? "" : r.Text;
                SendJson(ctx, d);
            }
            catch (Exception ex)
            {
                Log.Write("生成设备清单失败：" + ex.Message, "网页");
                SendJson(ctx, Fail("生成设备清单失败：" + ex.Message));
            }
        }

        // ====================================================================
        //  GET /api/openlogs
        // ====================================================================

        private static void OpenLogs(HttpListenerContext ctx)
        {
            try
            {
                string dir = Log.LogDir;
                if (string.IsNullOrEmpty(dir))
                {
                    SendJson(ctx, Fail("日志目录未知"));
                    return;
                }

                // 目录还没建起来时 explorer 会弹错误框，先补一下
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { /* 建不出来就让 explorer 自己报错 */ }

                var psi = new ProcessStartInfo("explorer.exe", "\"" + dir + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
                SendJson(ctx, Ok());
            }
            catch (Exception ex)
            {
                Log.Write("打开日志文件夹失败：" + ex.Message, "网页");
                SendJson(ctx, Fail("打开日志文件夹失败：" + ex.Message));
            }
        }

        // ====================================================================
        //  /api/mode
        // ====================================================================

        private static void PostMode(HttpListenerContext ctx)
        {
            Dictionary<string, object> o;
            string bad;
            if (!TryParseBody(ctx, out o, out bad))
            {
                SendJson(ctx, Fail(bad));
                return;
            }

            string raw = JsonParser.GetString(o, "mode", null);
            string mode = CanonMode(raw);
            if (mode == null)
            {
                SendJson(ctx, Fail("模式只能是 All / Device / Hardware"));
                return;
            }

            AppState.I.Mode = mode;

            // 持久化到 RunMode，下次启动还是这个模式
            string err;
            if (!SetSetting("RunMode", mode, out err))
            {
                try { Settings.I.RunMode = mode; }
                catch (Exception ex) { Log.Write("写入 RunMode 失败：" + ex.Message, "网页"); }
            }
            try { Settings.I.Save(); }
            catch (Exception ex) { Log.Write("保存运行模式失败：" + ex.Message, "网页"); }

            SendJson(ctx, Ok("mode", mode));
        }

        private static string CanonMode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string m = raw.Trim();
            if (string.Equals(m, "All", StringComparison.OrdinalIgnoreCase)) return "All";
            if (string.Equals(m, "Device", StringComparison.OrdinalIgnoreCase)) return "Device";
            if (string.Equals(m, "Hardware", StringComparison.OrdinalIgnoreCase)) return "Hardware";
            return null;
        }

        // ====================================================================
        //  GET /api/rule?kind=ignore|focus&v=xxx
        //
        //  注意：契约文档写的是 v=，但前端 index.html 第 1501 行用的是 pattern=，
        //  两个参数名都收，另外再兼容一个 p=。
        // ====================================================================

        private static void Rule(HttpListenerContext ctx)
        {
            string kind = null, v = null;
            try
            {
                var q = ctx.Request.QueryString;
                kind = q["kind"];
                v = q["v"];
                if (string.IsNullOrEmpty(v)) v = q["pattern"];
                if (string.IsNullOrEmpty(v)) v = q["p"];
            }
            catch (Exception ex)
            {
                SendJson(ctx, Fail("读取查询参数失败：" + ex.Message));
                return;
            }

            kind = (kind ?? "").Trim().ToLowerInvariant();
            v = (v ?? "").Trim();

            if (kind != "ignore" && kind != "focus")
            {
                SendJson(ctx, Fail("kind 只能是 ignore 或 focus"));
                return;
            }
            if (v.Length == 0)
            {
                SendJson(ctx, Fail("缺少参数 v（或 pattern）"));
                return;
            }

            bool isIgnore = kind == "ignore";
            List<string> list;
            try
            {
                list = isIgnore ? Settings.I.IgnorePatterns : Settings.I.FocusPatterns;
            }
            catch (Exception ex)
            {
                SendJson(ctx, Fail("读取规则失败：" + ex.Message));
                return;
            }
            if (list == null) list = new List<string>();
            // 万一属性返回的是只读集合（IsReadOnly 是显式接口实现），这里换成可写副本，后面统一写回 Settings
            else list = new List<string>(list);   // 统一复制一份，避免改到只读集合或正在被其它线程读的实例

            // 已存在就删掉，不存在就加上（前端按钮就是这个语义）
            bool added;
            int idx = IndexOfIgnoreCase(list, v);
            if (idx >= 0) { list.RemoveAt(idx); added = false; }
            else { list.Add(v); added = true; }

            string err;
            if (!SetSetting(isIgnore ? "IgnorePatterns" : "FocusPatterns", list, out err))
            {
                SendJson(ctx, Fail(err == null ? "保存规则失败" : err));
                return;
            }
            try { Settings.I.Save(); }
            catch (Exception ex) { Log.Write("保存规则失败：" + ex.Message, "网页"); }

            var d = new Dictionary<string, object>();
            d["ok"] = true;
            d["kind"] = kind;
            d["added"] = added;
            d["patterns"] = new List<string>(list);
            d["list"] = new List<string>(list);
            SendJson(ctx, d);
        }

        private static int IndexOfIgnoreCase(List<string> list, string v)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        // ====================================================================
        //  网页 HTML（嵌入资源，只读一次）
        // ====================================================================

        private static string IndexHtml()
        {
            if (_htmlTried) return _htmlCache;
            _htmlTried = true;

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(HtmlResource))
                {
                    if (s != null)
                    {
                        // detectEncodingFromByteOrderMarks: 前端文件带不带 BOM 都能正确解码
                        using (var sr = new StreamReader(s, new UTF8Encoding(false), true))
                        {
                            _htmlCache = sr.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取网页资源失败：" + ex.Message, "网页");
            }

            if (string.IsNullOrEmpty(_htmlCache))
            {
                Log.Write("网页资源没找到：" + HtmlResource + "（检查 csproj 里的 EmbeddedResource）", "网页");
                _htmlCache = HtmlFallback();
            }
            return _htmlCache;
        }

        /// <summary>嵌入资源丢失时给用户看的中文提示页，总比一片空白强。</summary>
        private static string HtmlFallback()
        {
            var names = new StringBuilder();
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] all = asm.GetManifestResourceNames();
                for (int i = 0; i < all.Length; i++)
                    names.Append("<li><code>").Append(HtmlEscape(all[i])).Append("</code></li>");
            }
            catch { /* 连资源列表都拿不到就只能空着 */ }

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            sb.Append("<title>网页面板资源缺失</title><style>");
            sb.Append("body{font-family:'Microsoft YaHei',-apple-system,sans-serif;background:#14161a;color:#e6e8ea;");
            sb.Append("padding:40px;line-height:1.9;max-width:760px;margin:0 auto}");
            sb.Append("code{background:#22262c;padding:2px 6px;border-radius:4px;color:#8fd0ff}");
            sb.Append("a{color:#5aa9ff}h2{margin-bottom:6px}.dim{color:#8b939c}</style></head><body>");
            sb.Append("<h2>网页面板没有可显示的页面</h2>");
            sb.Append("<p>程序里没有嵌入 <code>").Append(HtmlEscape(HtmlResource)).Append("</code>，所以打不开界面。</p>");
            sb.Append("<p class=\"dim\">监控本身还在正常运行：数据接口 <a href=\"/api/state\">/api/state</a> 可以正常访问。</p>");
            sb.Append("<p>当前程序里嵌入的资源：</p><ul>");
            sb.Append(names.Length == 0 ? "<li class=\"dim\">（一个都没有）</li>" : names.ToString());
            sb.Append("</ul><p class=\"dim\">请在 DeviceWatch.csproj 里确认：<br>");
            sb.Append("<code>&lt;EmbeddedResource Include=\"ui\\index.html\"&gt;");
            sb.Append("&lt;LogicalName&gt;DeviceWatch.ui.index.html&lt;/LogicalName&gt;&lt;/EmbeddedResource&gt;</code></p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>写一项设置：统一走 Settings.I.Apply；失败时把原因带回去。</summary>
        private static bool SetSetting(string key, object value, out string error)
        {
            error = null;
            try
            {
                bool needRestart;
                return Settings.I.Apply(key, value, out error, out needRestart);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("写入设置 " + key + " 失败：" + ex.Message, "网页");
                return false;
            }
        }

        // ====================================================================
        //  响应与请求辅助
        // ====================================================================

        private static Dictionary<string, object> Ok()
        {
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            return d;
        }

        /// <summary>{"ok":true, key: value}</summary>
        private static Dictionary<string, object> Ok(string key, object value)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            d[key] = value;
            return d;
        }

        /// <summary>
        /// 失败响应。err 是契约里的字段名；error 是前端保存设置时读的字段名
        /// （index.html 第 1357 行），两个都给，谁都不会拿到 undefined。
        /// </summary>
        private static Dictionary<string, object> Fail(string msg)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            d["err"] = msg;
            d["error"] = msg;
            return d;
        }

        private static string Json(object payload)
        {
            string s = payload as string;
            if (s != null) return s;
            var w = new JsonWriter();
            w.WriteValue(payload);
            return w.ToString();
        }

        private static void SendJson(HttpListenerContext ctx, object payload)
        {
            Send(ctx, 200, "application/json; charset=utf-8", Json(payload));
        }

        private static void SendJson(HttpListenerContext ctx, int status, object payload)
        {
            Send(ctx, status, "application/json; charset=utf-8", Json(payload));
        }

        private static void SendHtml(HttpListenerContext ctx, string html)
        {
            Send(ctx, 200, "text/html; charset=utf-8", html);
        }

        private static void Send(HttpListenerContext ctx, int status, string contentType, string body)
        {
            try
            {
                HttpListenerResponse res = ctx.Response;
                res.StatusCode = status;
                if (!string.IsNullOrEmpty(contentType)) res.ContentType = contentType;
                res.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";

                // 中文必须走 UTF-8，并且长度按字节算（按字符算会让浏览器少读一截）
                byte[] buf = string.IsNullOrEmpty(body) ? new byte[0] : Encoding.UTF8.GetBytes(body);
                res.ContentEncoding = Encoding.UTF8;
                res.ContentLength64 = buf.Length;
                if (buf.Length > 0)
                {
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.OutputStream.Flush();
                }
                res.Close();
            }
            catch (Exception ex)
            {
                // 客户端提前断开是常事（刷新页面、关标签页），记一条就够了
                Log.Write("回响应失败（HTTP " + status + "）：" + ex.Message, "网页");
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private static void SendNoContent(HttpListenerContext ctx)
        {
            try
            {
                HttpListenerResponse res = ctx.Response;
                res.StatusCode = 204;
                try { res.ContentLength64 = 0; } catch { /* 有些实现不给 204 设长度，无所谓 */ }
                res.Close();
            }
            catch (Exception ex)
            {
                Log.Write("回 204 失败：" + ex.Message, "网页");
                try { ctx.Response.Abort(); } catch { }
            }
        }

        /// <summary>
        /// 读请求体。一律按 UTF-8 解码：JSON 的默认编码就是 UTF-8，
        /// 而中文系统上 HttpListenerRequest.ContentEncoding 会退回 GBK，中文会乱码。
        /// </summary>
        private static string ReadBodyText(HttpListenerContext ctx)
        {
            try
            {
                HttpListenerRequest req = ctx.Request;
                if (!req.HasEntityBody) return "";

                byte[] buf;
                using (var ms = new MemoryStream())
                {
                    var chunk = new byte[8192];
                    int n;
                    while ((n = req.InputStream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        ms.Write(chunk, 0, n);
                        if (ms.Length > MaxBodyBytes) break;
                    }
                    buf = ms.ToArray();
                }
                if (buf.Length == 0) return "";
                if (buf.Length > MaxBodyBytes) buf = new byte[0];

                string s = new UTF8Encoding(false).GetString(buf);
                if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);   // 去掉可能的 BOM
                return s;
            }
            catch (Exception ex)
            {
                Log.Write("读取请求体失败：" + ex.Message, "网页");
                return "";
            }
        }

        /// <summary>解析请求体为对象；失败时给出可以直接回给前端的错误文案。</summary>
        private static bool TryParseBody(HttpListenerContext ctx, out Dictionary<string, object> obj, out string error)
        {
            obj = null;
            error = null;
            string body = ReadBodyText(ctx);

            if (string.IsNullOrEmpty(body))
            {
                error = "请求体是空的，应该是一个 JSON 对象";
                return false;
            }
            try
            {
                obj = JsonParser.Parse(body) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                error = "请求体不是合法的 JSON：" + ex.Message;
                return false;
            }
            if (obj == null)
            {
                error = "请求体必须是一个 JSON 对象";
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------ 取值转换

        private static bool TryNum(object v, out double n)
        {
            n = 0;
            if (v == null) return false;
            if (v is bool) return false;                       // true/false 不算数字
            if (v is double) { n = (double)v; return !double.IsNaN(n) && !double.IsInfinity(n); }
            if (v is int) { n = (int)v; return true; }
            if (v is long) { n = (long)v; return true; }
            if (v is float) { n = (float)v; return true; }
            if (v is decimal) { n = (double)(decimal)v; return true; }
            string s = v as string;
            if (s != null)
                return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
            return false;
        }

        private static bool ToBool(object v, bool def)
        {
            if (v == null) return def;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s != null)
            {
                s = s.Trim();
                if (s.Length == 0) return def;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (s == "1" || string.Equals(s, "on", StringComparison.OrdinalIgnoreCase)) return true;
                if (s == "0" || string.Equals(s, "off", StringComparison.OrdinalIgnoreCase)) return false;
            }
            double d;
            if (TryNum(v, out d)) return Math.Abs(d) > 0.0001;
            return def;
        }

        private static string ToStr(object v)
        {
            if (v == null) return "";
            if (v is bool) return ((bool)v) ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>把 JSON 数组转成字符串列表（设置项的 list 类型用）。</summary>
        private static List<string> ToStrList(object v)
        {
            var outp = new List<string>();
            if (v == null) return outp;

            var arr = v as System.Collections.IEnumerable;
            if (arr != null && !(v is string))
            {
                foreach (object o in arr)
                {
                    string s = ToStr(o).Trim();
                    if (s.Length > 0) outp.Add(s);
                }
                return outp;
            }

            string text = ToStr(v).Trim();
            if (text.Length > 0) outp.Add(text);
            return outp;
        }
    }
}
