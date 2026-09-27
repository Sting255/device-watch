// ============================================================================
//  MemSpd.cs —— 内存条信息（型号 / 容量 / 频率 / 代数 / 槽位）
//
//  数据来源：WMI Win32_PhysicalMemory（SMBIOS Type 17 Memory Device 的映射）。
//  对应 DeviceWatch.ps1 里 Initialize-Hardware 中 Get-CimInstance Win32_PhysicalMemory
//  那一段，字段映射逐项对齐契约（CONTRACT.md 的 hw.MemModules）。
//
//  【调用时机】内存条不会热插拔，这个只需要在初始化时调用一次，
//  千万不要放进 2 秒一轮的硬件采样循环 —— WMI 查询有几十毫秒开销，
//  而主循环还要泵托盘消息和处理网页请求，那正是界面卡顿的根源。
//
//  【为什么不用 MSStorageDriver_* 拿 SPD 温度】
//  那条路要管理员权限且大多数主板不实现（详见 README）。这里只读 SMBIOS 描述信息，
//  WMI 不需要提权。内存温度由 LibreHardwareMonitor 侧统一提供。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Management;

namespace DeviceWatch
{
    /// <summary>内存条读取（WMI Win32_PhysicalMemory）。</summary>
    public static class MemSpd
    {
        /// <summary>SMBIOS 内存类型代码：其它/未知按契约填 0。</summary>
        private static int MapDdr(int smbiosType)
        {
            switch (smbiosType)
            {
                case 24: return 24;   // DDR3
                case 26: return 26;   // DDR4
                case 34: return 34;   // DDR5
                default: return 0;
            }
        }

        /// <summary>
        /// 读内存条信息（型号/容量/频率/代数/槽位），失败返回空列表。
        /// 本方法不抛异常：WMI 在极老机器或 WMI 服务被禁用时会直接抛，
        /// 这里一律降级成空列表，让上层照常输出（MemModules: []）。
        /// </summary>
        public static List<MemModuleInfo> ReadAll()
        {
            var list = new List<MemModuleInfo>();
            ManagementObjectCollection rows = null;
            ManagementObjectSearcher searcher = null;
            try
            {
                // 只取需要的属性：不写 SELECT * 是刻意的，WMI 取全部属性会明显变慢。
                var q = new ObjectQuery(
                    "SELECT PartNumber, Manufacturer, Capacity, Speed, ConfiguredClockSpeed, " +
                    "DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory");

                searcher = new ManagementObjectSearcher(q);
                rows = searcher.Get();
                if (rows == null) return list;

                foreach (ManagementObject mo in rows)
                {
                    // ManagementObject 也要 Dispose（内部握有 COM 接口），
                    // 一次初始化只跑一次，但仍然照规矩释放。
                    using (mo)
                    {
                        try
                        {
                            var m = new MemModuleInfo();

                            // PartNumber 首尾经常被主板补空格（甚至补 0x00 之外的空格），
                            // 不去掉会变成 "CT16G56C46S5.M8G1        "，网页上很难看。
                            m.Part = (GetString(mo, "PartNumber") ?? "").Trim();
                            if (m.Part.Length == 0) m.Part = "Unknown";

                            string mfg = (GetString(mo, "Manufacturer") ?? "").Trim();
                            // 很多主板 SMBIOS 里 Manufacturer 是空串或全空格（内存条本体没烧录），
                            // 契约里显示的是 "Unknown"，保持一致。
                            m.Mfg = mfg.Length > 0 ? mfg : "Unknown";

                            // Capacity 是字节（ulong）。除 1024^3 取整 => GB；
                            // 用 double 中转，避免 ulong 整除截断带来的误差。
                            double capBytes = GetDouble(mo, "Capacity");
                            if (capBytes > 0)
                                m.SizeGB = (int)Math.Round(capBytes / 1073741824.0, MidpointRounding.AwayFromZero);

                            // ConfiguredClockSpeed = 当前实际运行频率（可能被主板降频），
                            // Speed = 标称频率（XMP/EXPO 关掉时两者不同）。
                            m.Speed = (int)GetDouble(mo, "ConfiguredClockSpeed");
                            m.Rated = (int)GetDouble(mo, "Speed");

                            m.Ddr = MapDdr((int)GetDouble(mo, "SMBIOSMemoryType"));
                            m.Slot = (GetString(mo, "DeviceLocator") ?? "").Trim();

                            list.Add(m);
                        }
                        catch
                        {
                            // 单根内存条读失败不影响其它条，跳过即可。
                        }
                    }
                }

                // 按槽位名排序，保证每次输出顺序稳定（WMI 返回顺序不保证），
                // 否则网页上"内存条 1/2"会随刷新跳来跳去。
                list.Sort(delegate (MemModuleInfo a, MemModuleInfo b)
                {
                    return string.Compare(a.Slot, b.Slot, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch
            {
                return new List<MemModuleInfo>();
            }
            finally
            {
                // WMI 对象是 COM 包装，不 Dispose 会拖住 WMI 服务端的句柄。
                if (rows != null) rows.Dispose();
                if (searcher != null) searcher.Dispose();
            }
            return list;
        }

        // ---- 内部辅助：WMI 属性可能为 null / 类型不符，统一安全取值 -------------

        private static string GetString(ManagementObject mo, string name)
        {
            try
            {
                object v = mo[name];
                if (v == null) return "";
                return v.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static double GetDouble(ManagementObject mo, string name)
        {
            try
            {
                object v = mo[name];
                if (v == null) return 0;
                return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }
    }
}
