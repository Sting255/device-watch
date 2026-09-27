// ============================================================================
//  NetProbe.cs —— 外网连通性探测
//
//  分工时这块漏掉了：AppState 里的 InternetState / PingTargets 只有人读、没人写，
//  结果网页「网络」卡片一直显示「未知」。这里补上。
//
//  设计要点：
//    - Ping 是阻塞调用（一个目标最多等 PingTimeoutMs，默认 1500ms），
//      所以整轮探测必须放后台线程，绝不能塞进主循环 —— 否则界面卡死。
//    - 用「连续失败 N 次才判定断网」而不是一次失败就报，避免偶发丢包误报。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace DeviceWatch
{
    public static class NetProbe
    {
        private static readonly object Sync = new object();
        private static volatile bool _busy;
        private static int _failStreak;
        private static bool _started;

        /// <summary>启动后台探测（幂等，调用一次即可）。</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "NetProbe";
            t.Start();
        }

        private static void Loop()
        {
            while (true)
            {
                int interval;
                try { interval = Math.Max(1000, Settings.I.NetCheckMs); } catch { interval = 8000; }

                try { ProbeOnce(); } catch { }
                Thread.Sleep(interval);
            }
        }

        private static void ProbeOnce()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var targets = new List<string>();
                try
                {
                    if (Settings.I.PingTargets != null)
                    {
                        foreach (string t in Settings.I.PingTargets)
                            if (!string.IsNullOrEmpty(t) && !targets.Contains(t)) targets.Add(t);
                    }
                }
                catch { }
                if (targets.Count == 0)
                {
                    targets.Add("223.5.5.5");
                    targets.Add("119.29.29.29");
                    targets.Add("1.1.1.1");
                }

                int timeout = 1500;
                try { timeout = Math.Max(200, Settings.I.PingTimeoutMs); } catch { }

                var results = new List<PingTarget>();

                // 网关放在最前面：网关都不通说明是本机到路由器这一段的问题，
                // 和「外网断了」是两回事，一眼能区分开。
                string gw = FindGateway();
                if (!string.IsNullOrEmpty(gw))
                {
                    results.Add(PingOne(gw, timeout, true));
                    if (!targets.Contains(gw)) { /* 网关不重复放进目标列表 */ }
                }

                foreach (string t in targets)
                {
                    if (!string.IsNullOrEmpty(gw) && string.Equals(t, gw, StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add(PingOne(t, timeout, false));
                }

                // ---- 判定 ----
                int okCount = 0, total = 0;
                foreach (var r in results)
                {
                    if (r.Gw) continue;          // 网关只用来显示，不参与断网判定
                    total++;
                    if (r.Ok) okCount++;
                }

                bool anyOk = okCount > 0;
                if (anyOk) _failStreak = 0; else _failStreak++;

                int threshold = 2;
                try { threshold = Math.Max(1, Settings.I.InternetFailThreshold); } catch { }
                bool down = _failStreak >= threshold;

                var sb = new StringBuilder();
                foreach (var r in results)
                {
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append(r.T);
                    sb.Append(' ');
                    if (r.Ok) sb.Append(r.Ms >= 0 ? r.Ms + "ms" : "ok");
                    else sb.Append("超时");
                }

                AppState.I.PingTargets = results;
                AppState.I.InternetDetail = sb.ToString();
                AppState.I.InternetState = total == 0 ? "未知" : (anyOk ? "正常" : "异常");

                bool wasDown = AppState.I.NetDown;
                AppState.I.NetDown = down;

                // 只在状态翻转时提醒，不要每次探测都弹
                if (down && !wasDown)
                {
                    Log.Write("外网探测连续失败 " + _failStreak + " 次，判定为断网：" + sb, "Warn");
                    Toast.Show("网络断开", "连续 " + _failStreak + " 次探测失败\n" + sb, "Warn");
                }
                else if (!down && wasDown)
                {
                    Log.Write("外网已恢复：" + sb);
                    Toast.Show("网络已恢复", sb.ToString(), "Info");
                }
            }
            finally { _busy = false; }
        }

        private static PingTarget PingOne(string host, int timeout, bool isGw)
        {
            var r = new PingTarget { T = host, Gw = isGw, Ok = false, Ms = -1 };
            try
            {
                using (var p = new Ping())
                {
                    PingReply reply = p.Send(host, timeout);
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        r.Ok = true;
                        r.Ms = (int)reply.RoundtripTime;
                    }
                }
            }
            catch { }
            return r;
        }

        /// <summary>找一个默认网关地址。找不到返回 null。</summary>
        private static string FindGateway()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        NetworkInterfaceType t = ni.NetworkInterfaceType;
                        if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
                        IPInterfaceProperties props = ni.GetIPProperties();
                        if (props == null) continue;
                        foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                        {
                            if (g == null || g.Address == null) continue;
                            if (g.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            string s = g.Address.ToString();
                            if (!string.IsNullOrEmpty(s) && s != "0.0.0.0") return s;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }
    }
}
