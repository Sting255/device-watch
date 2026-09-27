// ============================================================================
//  DevNative.cs —— 设备枚举的原生调用（SetupAPI + CfgMgr32）
//
//  三条路径的耗时差两个数量级，用错地方界面会明显发卡，所以分开实现：
//    PresentIds()  只列"在线设备的实例 ID"                实测 ~0.06ms → 每 200ms 的轮询走这条
//    DetailsFor()  按 ID 直接打开指定设备                   实测 ~3ms    → 插拔时只查变化的那几台
//    Details()     全量枚举 + 每台设备读 4 个属性           实测 ~118ms  → 只在启动建基线时用
//
//  移植自 DeviceWatch.ps1 第 289~411 行那段已经在真机上验证过的 P/Invoke。
//  struct 布局、DEVPROPKEY 的 pid、属性常量都保持原样，不要"顺手优化"，
//  这些值是 Windows SDK 里的固定契约，写错只会静默返回空字符串。
//
//  约定：本类所有方法都不抛异常，失败时返回空集合/空串，调用方不必再包 try。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DeviceWatch
{
    /// <summary>
    /// 设备枚举。返回的 string[] 固定 7 列：
    /// [0]=实例ID [1]=名称 [2]=类别 [3]=枚举器 [4]=故障码 [5]=状态位 [6]=设备自报型号。
    /// 用 string[] 而不是强类型对象，是为了和 PowerShell 版逐字段对齐（那边就是 string[]），
    /// 上层 DeviceMonitor 按固定下标取值，改这里的列序必须同步改上层。
    /// </summary>
    public static class DevNative
    {
        // ------------------------------------------------------------ P/Invoke
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiOpenDeviceInfo(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType, byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint need, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint CM_Get_Device_ID_ListW(string pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);

        // ------------------------------------------------------------ 常量
        private const uint DIGCF_PRESENT = 0x02, DIGCF_ALLCLASSES = 0x04;
        private const uint SPDRP_DEVICEDESC = 0x00, SPDRP_CLASS = 0x07, SPDRP_FRIENDLYNAME = 0x0C, SPDRP_ENUMERATOR_NAME = 0x16;
        private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // DEVPKEY_Device_BusReportedDeviceDesc —— 设备自己上报的型号字符串（USB 设备最准的名字来源）
        private static readonly Guid BusReportedKey = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2");
        private const uint DEVPKEY_PID_BUS_REPORTED_DESC = 4;
        private const uint DEVPROP_TYPE_STRING = 18;

        private static readonly string[] EmptyIds = new string[0];

        // ------------------------------------------------------------ 属性读取
        /// <summary>
        /// 读一个注册表属性。scratch 由调用方复用：全量扫描时每台设备要读 4 次，
        /// 每次新分配 2~4KB 的话，289 台设备就是 1000 多次分配，纯属白给 GC 添活。
        /// </summary>
        private static string Str(IntPtr set, ref SP_DEVINFO_DATA d, uint prop, byte[] scratch)
        {
            uint type, req;
            if (SetupDiGetDeviceRegistryProperty(set, ref d, prop, out type, scratch, (uint)scratch.Length, out req) && req > 2)
                return Encoding.Unicode.GetString(scratch, 0, (int)req - 2);   // req 含结尾的 L'\0'，要减掉
            return "";
        }

        /// <summary>设备自报的型号（读不到就返回空串，不要编一个"未知设备"出来）。</summary>
        private static string BusReported(IntPtr set, ref SP_DEVINFO_DATA d, byte[] scratch)
        {
            DEVPROPKEY k = new DEVPROPKEY();
            k.fmtid = BusReportedKey;
            k.pid = DEVPKEY_PID_BUS_REPORTED_DESC;
            uint type, req;
            try
            {
                // 只接受 DEVPROP_TYPE_STRING：有的驱动在这个 key 上放别的类型，硬读会得到乱码
                if (SetupDiGetDevicePropertyW(set, ref d, ref k, out type, scratch, (uint)scratch.Length, out req, 0)
                    && req > 2 && type == DEVPROP_TYPE_STRING)
                    return Encoding.Unicode.GetString(scratch, 0, (int)req - 2);
            }
            catch { }
            return "";
        }

        // ------------------------------------------------------------ 热路径
        // 列表缓冲区跨调用复用：每 200ms 调一次，一次几十 KB 的分配没必要。
        // 锁的开销在这个量级下可以忽略，但能挡住将来万一从别的线程调用。
        private static readonly object BufLock = new object();
        private static char[] _idBuf = new char[8192];

        /// <summary>
        /// 当前在线设备的实例 ID 列表（约 0.06ms，轮询热路径专用）。
        /// 返回的数组是一次性快照，调用方不要改写。
        /// </summary>
        public static string[] PresentIds()
        {
            lock (BufLock)
            {
                try
                {
                    uint len;
                    if (CM_Get_Device_ID_List_SizeW(out len, null, CM_GETIDLIST_FILTER_PRESENT) != 0 || len == 0)
                        return EmptyIds;
                    if (_idBuf.Length < len + 16) _idBuf = new char[len + 16];

                    // 先清干净：万一这次返回的列表比上次短又没有双 \0 收尾，
                    // 残留的旧字符会被当成本次数据读出来（比"设备凭空多出来"这种鬼故事好防）
                    int clear = (int)Math.Min((long)len + 16, _idBuf.Length);
                    Array.Clear(_idBuf, 0, clear);

                    if (CM_Get_Device_ID_ListW(null, _idBuf, (uint)_idBuf.Length, CM_GETIDLIST_FILTER_PRESENT) != 0)
                        return EmptyIds;

                    var res = new List<string>(256);
                    int start = 0;
                    for (int i = 0; i < _idBuf.Length; i++)
                    {
                        if (_idBuf[i] != '\0') continue;
                        if (i == start) break;                  // 连续两个 \0 = 列表结束
                        res.Add(new string(_idBuf, start, i - start));
                        start = i + 1;
                    }
                    return res.ToArray();
                }
                catch { return EmptyIds; }
            }
        }

        /// <summary>
        /// 只查指定的几台设备，不枚举全部（实测 3ms vs 全量 118ms）。
        /// 给"刚插上"的设备取名字用：一次插拔通常只涉及 1~3 台设备，
        /// 这是插入提示延迟从约 1.2 秒降到约 0.6 秒的关键。
        /// </summary>
        public static List<string[]> DetailsFor(string[] ids)
        {
            var list = new List<string[]>();
            if (ids == null || ids.Length == 0) return list;

            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == IntPtr.Zero || set == INVALID_HANDLE_VALUE) return list;
            try
            {
                byte[] scratch = new byte[4096];
                foreach (string id in ids)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    try
                    {
                        SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                        d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                        // 已断开的设备在这里会直接失败 —— 这是正常的，跳过即可
                        if (!SetupDiOpenDeviceInfo(set, id, IntPtr.Zero, 0, ref d)) continue;

                        string desc = Str(set, ref d, SPDRP_FRIENDLYNAME, scratch);
                        if (desc.Length == 0) desc = Str(set, ref d, SPDRP_DEVICEDESC, scratch);
                        uint status = 0, problem = 0;
                        uint cr = CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0);
                        list.Add(new string[] {
                            id, desc, Str(set, ref d, SPDRP_CLASS, scratch), Str(set, ref d, SPDRP_ENUMERATOR_NAME, scratch),
                            (cr == 0 ? problem : 0).ToString(), (cr == 0 ? status : 0).ToString(), BusReported(set, ref d, scratch)
                        });
                    }
                    catch { }   // 单台设备读失败不能连累其余的
                }
            }
            catch { }
            finally { try { SetupDiDestroyDeviceInfoList(set); } catch { } }
            return list;
        }

        /// <summary>
        /// 完整详情：ID / 名称 / 类别 / 枚举器 / 故障码 / 状态位 / 自报型号（约 118ms）。
        /// 每 90 秒的周期扫描不要用这个，只在启动建基线时用（见 DeviceMonitor.FullScan 的说明）。
        /// </summary>
        public static List<string[]> Details()
        {
            var list = new List<string[]>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == INVALID_HANDLE_VALUE) return list;
            try
            {
                byte[] scratch = new byte[4096];
                SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        uint need;
                        if (!SetupDiGetDeviceInstanceId(set, ref d, sb, (uint)sb.Capacity, out need) || sb.Length == 0)
                            continue;      // 拿不到实例 ID 的行没有任何用，丢掉比带个空 ID 往上走强
                        string desc = Str(set, ref d, SPDRP_FRIENDLYNAME, scratch);
                        if (desc.Length == 0) desc = Str(set, ref d, SPDRP_DEVICEDESC, scratch);
                        uint status = 0, problem = 0;
                        uint cr = CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0);
                        list.Add(new string[] {
                            sb.ToString(), desc, Str(set, ref d, SPDRP_CLASS, scratch), Str(set, ref d, SPDRP_ENUMERATOR_NAME, scratch),
                            (cr == 0 ? problem : 0).ToString(), (cr == 0 ? status : 0).ToString(), BusReported(set, ref d, scratch)
                        });
                    }
                    catch { }   // 单台设备读失败不能连累其余的
                }
            }
            catch { }
            finally { try { SetupDiDestroyDeviceInfoList(set); } catch { } }
            return list;
        }
    }
}
