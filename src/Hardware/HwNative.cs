// ============================================================================
//  HwNative.cs —— 直接调 Win32 API 的最底层部分（不走 WMI、不要管理员、最快）
//
//  为什么这几项必须用 P/Invoke 而不是 WMI / PerformanceCounter：
//   - GetSystemTimes：CPU 占用只能靠两次采样的差值算，几微秒就返回。
//     WMI 的 Win32_Processor.LoadPercentage 要等它自己刷新（最多 1 秒才变一次），
//     而且整类查询本身在本机实测要 1130ms —— 采样间隔才 2 秒，用它等于自杀。
//   - GlobalMemoryStatusEx：提交量（commit）只有这里给得准；WMI 的
//     Win32_OperatingSystem 要查 20~40ms，字段还是"约数"。
//   - GetSystemPowerStatus：电池百分比 / 是否接电源，微秒级。
//   - GetTickCount64：开机时长，省掉一次查 Win32_OperatingSystem.LastBootUpTime。
//
//  约定：所有 P/Invoke 包装都返回 bool 表示"这次调用成不成功"，
//        失败时输出参数一律给 0 / -1，调用方不用处理异常。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
// FILETIME 有两个同名类型（System.Runtime.InteropServices.FILETIME 和 ComTypes.FILETIME），
// 同时 using 两个命名空间会 CS0104 二义性。契约要的是 ComTypes 那个，这里钉死它。
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace DeviceWatch
{
    public static class HwNative
    {
        // ------------------------------------------------------------ 结构体

        /// <summary>GlobalMemoryStatusEx 的入参/出参。字段顺序和大小必须和 Win32 完全一致。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;               // 必须由调用方填 sizeof(MEMORYSTATUSEX)=64，否则 API 直接返回失败
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;          // 物理内存总量
            public ulong ullAvailPhys;          // 可用物理内存
            public ulong ullTotalPageFile;      // 提交上限（物理内存 + 页面文件）
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>GetSystemPowerStatus 的出参。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;           // 0=用电池 1=接电源 255=未知
            public byte BatteryFlag;            // 128=没有系统电池 255=未知
            public byte BatteryLifePercent;     // 0~100，255=未知
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        // ------------------------------------------------------------ P/Invoke

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        // ------------------------------------------------------------ 包装

        /// <summary>
        /// 把 GetSystemTimes 出来的 FILETIME 转成 64 位计数（单位 100ns）。
        /// </summary>
        public static ulong FileTimeToUlong(FILETIME ft)
        {
            // 两个字段都是 int（有符号）：必须先按 uint 截断再拼，
            // 否则高位为 1 时（开机超过约 24 天后必然出现）会符号扩展成 0xFFFFFFFF........
            return ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        }

        /// <summary>
        /// 读 CPU 累计时间。kernel 时间里**包含 idle**，所以"总时间" = kernel + user，
        /// 算占用率时必须 busy = (kernel+user) - idle。
        /// </summary>
        public static bool GetCpuTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            idle = kernel = user = 0;
            try
            {
                FILETIME i, k, u;
                if (!GetSystemTimes(out i, out k, out u)) return false;
                idle = FileTimeToUlong(i);
                kernel = FileTimeToUlong(k);
                user = FileTimeToUlong(u);
                return true;
            }
            catch { return false; }   // 极端情况下（API 不可用）也不要抛给主循环
        }

        /// <summary>
        /// 读物理内存：总量 / 可用 / 已提交 / 提交上限（都是字节）。
        /// 已提交 = 提交上限 - 剩余提交额度（页面文件已用掉的部分）。
        /// </summary>
        public static bool GetMemory(out ulong totalBytes, out ulong availBytes, out ulong commitBytes, out ulong commitLimitBytes)
        {
            totalBytes = availBytes = commitBytes = commitLimitBytes = 0;
            try
            {
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));   // 不填这个 API 一定失败
                if (!GlobalMemoryStatusEx(ref m)) return false;

                totalBytes = m.ullTotalPhys;
                availBytes = m.ullAvailPhys;
                commitLimitBytes = m.ullTotalPageFile;
                commitBytes = m.ullTotalPageFile > m.ullAvailPageFile ? m.ullTotalPageFile - m.ullAvailPageFile : 0;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 读电池 / 电源状态。
        /// batteryPct = -1 表示"读不到或这台机器没有电池"（台式机 BatteryFlag=128）；
        /// 注意 C# 的 HwState.BatPct 是非空 int，表达不了 null，只能用 -1 当哨兵值。
        /// </summary>
        public static bool GetPower(out int batteryPct, out bool onAC)
        {
            batteryPct = -1;
            onAC = false;
            try
            {
                SYSTEM_POWER_STATUS s;
                if (!GetSystemPowerStatus(out s)) return false;

                int ac = s.ACLineStatus;
                int flag = s.BatteryFlag;
                int pct = s.BatteryLifePercent;

                // 128 = 没有系统电池，255 = 未知；AC 和电量同时未知也当作没电池
                bool noBattery = (flag == 128 || flag == 255 || (ac == 255 && pct == 255));
                onAC = (ac == 1);                       // 255（未知）当作"不是接电源"
                batteryPct = (noBattery || pct == 255) ? -1 : pct;
                return true;
            }
            catch { return false; }
        }

        /// <summary>开机到现在的毫秒数（含睡眠/休眠时间，和"上次开机时间"算出来的墙钟时长一致）。</summary>
        public static ulong TickCount64()
        {
            try { return GetTickCount64(); }
            catch { return 0; }
        }
    }
}
