// ============================================================================
//  AppState.cs —— 全局可变状态（各模块的汇合点）
//
//  设备模块、硬件模块、网页模块都读写这里。字段名对应用户看到的界面。
// ============================================================================
using System;
using System.Collections.Generic;

namespace DeviceWatch
{
    public sealed class AppState
    {
        public static readonly AppState I = new AppState();
        private AppState() { }

        public readonly object Sync = new object();

        // ---- 运行信息 ----
        public DateTime StartTime = DateTime.Now;
        public string Mode = "All";                 // All / Device / Hardware
        public string HwMetric = "both";            // temp / load / both
        public int UiLagMs;                         // 界面卡顿看门狗峰值
        public string CurrentPhase = "启动";        // 看门狗用：当前在哪一段

        // ---- 设备 ----
        public readonly Dictionary<string, DeviceEntry> Known = new Dictionary<string, DeviceEntry>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DeviceStat> Stats = new Dictionary<string, DeviceStat>(StringComparer.OrdinalIgnoreCase);
        public readonly List<DeviceEvent> Recent = new List<DeviceEvent>();
        public List<ProblemDevice> ProblemList = new List<ProblemDevice>();
        public List<ProblemDevice> BenignList = new List<ProblemDevice>();
        public int EventCount;
        public DateTime? LastEvent;

        // ---- 硬件 ----
        public HwState Hw = new HwState();
        public List<AdapterInfo> Adapters = new List<AdapterInfo>();
        public ExtSensors Ext;                      // null = 未启用
        public string ExtSource = "";

        // ---- 网络探测 ----
        public string InternetState = "未知";
        public string InternetDetail = "";
        public List<PingTarget> PingTargets = new List<PingTarget>();
        public bool NetDown;

        // ---- 开关 ----
        public bool NotifyEnabled = true;
        public bool Learning;
        public DateTime? LearnUntil;
        public bool AutostartInstalled;

        // ---- 派生 ----
        public int DeviceCount { get { lock (Sync) { return Known.Count; } } }
        public int ProblemCount { get { lock (Sync) { return ProblemList.Count; } } }

        public string UptimeText
        {
            get
            {
                TimeSpan t = DateTime.Now - StartTime;
                return string.Format("{0} 天 {1:00}:{2:00}:{3:00}", (int)t.TotalDays, t.Hours, t.Minutes, t.Seconds);
            }
        }

        /// <summary>加一条"最近事件"，只保留最新 100 条。</summary>
        public void AddRecent(string kind, string label, string id, string detail)
        {
            lock (Sync)
            {
                Recent.Add(new DeviceEvent
                {
                    Seq = ++EventCount,
                    Time = DateTime.Now,
                    Kind = kind,
                    Name = label,
                    Id = id,
                    Detail = detail
                });
                while (Recent.Count > 100) Recent.RemoveAt(0);
                LastEvent = DateTime.Now;
            }
        }
    }
}
