// ============================================================================
//  NvmeSmart.cs —— NVMe 硬盘 SMART 直读（温度 / 寿命 / 通电小时）
//
//  【为什么要绕过 Windows 标准 API】
//  有些 NVMe 盘（尤其国产，例如致态 TiPlus 系列）不通过标准 Windows API 暴露
//  SMART：Get-StorageReliabilityCounter 返回空对象，WMI 的 MSStorageDriver_*
//  直接报"不支持"（该族 WMI 类只覆盖 SATA/IDE 的 ATA SMART）。
//  唯一稳定可行的办法是直接读物理磁盘的 NVMe SMART/Health 日志页（Log Page 02h）：
//
//      1) CreateFileW(@"\\.\PhysicalDriveN", desiredAccess: 0, shareMode: 3, ...)
//         —— 关键点：desiredAccess 必须传 0（只要设备句柄、不要读写权限），
//            因为 GENERIC_READ 打开物理磁盘需要管理员权限，而 access=0
//            普通用户也能打开，程序免提权就能取温度。
//            另外不能用 File.ReadAllBytes 之类，物理磁盘只能走 DeviceIoControl。
//      2) DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY) 取
//         StorageDeviceProtocolSpecificProperty(49) + ProtocolTypeNvme(3) +
//         NVMeDataTypeLogPage(2) + ProtocolDataRequestValue=2（即 Log Page 02h）。
//
//  这段逻辑在 DeviceWatch.ps1 的 Get-HwNvmeSmart（内嵌 Add-Type 的 HwNvmeSmart
//  类）里已经实机验证过，本文件是它的 C# 移植，偏移量和控制码逐字节对齐。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceWatch
{
    /// <summary>NVMe SMART 直读。</summary>
    public static class NvmeSmart
    {
        // ---- Win32 互操作 ----------------------------------------------------

        /// <summary>
        /// CreateFileW。用 CharSet.Unicode + 显式 W 后缀，避免 ANSI/Unicode 自动选择出错。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        /// <summary>
        /// DeviceIoControl。控制码用 uint（不是 int）：0x2D1400 是 DWORD 控制码。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // ---- 常量 ------------------------------------------------------------

        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

        private const uint PropertyStandardQuery = 0;                          // offset 4
        private const uint StorageDeviceProtocolSpecificProperty = 49;         // offset 0
        private const uint ProtocolTypeNvme = 3;                               // offset 8
        private const uint NVMeDataTypeLogPage = 2;                            // offset 12
        private const uint NvmeLogPageSmartHealthInfo = 0x02;                  // offset 16（Log Page 02h）

        /// <summary>sizeof(STORAGE_PROPERTY_QUERY)=8 + sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA)=32 = 40。</summary>
        private const int SpecLen = 40;

        /// <summary>NVMe SMART/Health Information 日志页固定 512 字节。</summary>
        private const int DataLen = 512;

        /// <summary>STORAGE_PROPERTY_QUERY 头 8 字节：PropertyId(4) + QueryType(4)。</summary>
        private const int QueryHeaderLen = 8;

        /// <summary>
        /// STORAGE_PROTOCOL_SPECIFIC_DATA 在 STORAGE_PROPERTY_QUERY 之后紧跟；
        /// 但这一整块在输出缓冲区里还会被前缀 8 字节的 STORAGE_PROPERTY_QUERY 头，
        /// 所以业务数据真正的起点 = 8 + ProtocolDataOffset。
        /// </summary>
        private const int ProtocolOffsetField = 8 + 16;   // 输出缓冲区里 ProtocolDataOffset 字段位置

        private const uint OpenExisting = 3;               // OPEN_EXISTING
        private const uint ShareReadWrite = 3;             // FILE_SHARE_READ | FILE_SHARE_WRITE

        private const uint BusTypeNvme = 17;               // STORAGE_BUS_TYPE::BusTypeNvme

        // ---- 对外接口 --------------------------------------------------------

        /// <summary>
        /// 直读物理磁盘的 NVMe SMART 日志页，读不到返回 null。
        /// pnum 是物理磁盘号（\\.\PhysicalDrive{pnum}），对非 NVMe 盘 / 盘不存在一律返回 null。
        /// 本方法不抛异常，任何一步失败都优雅降级为 null。
        /// </summary>
        public static DriveSmartInfo Read(int pnum)
        {
            SafeFileHandle h = null;
            try
            {
                if (pnum < 0) return null;

                // desiredAccess = 0：只要句柄不要读写权限，这样普通用户也能打开物理磁盘。
                // 传 GENERIC_READ 会因权限不足直接失败，那样就取不到温度了。
                h = CreateFileW(@"\\.\PhysicalDrive" + pnum, 0, ShareReadWrite,
                                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h == null || h.IsInvalid) return null;

                // 非 NVMe 盘（SATA/USB/NVMe 之外的协议）直接放弃：
                // 对它发 NVMe 日志页请求要么失败，要么返回垃圾数据。
                if (!IsNvmeDevice(h)) return null;

                // ---- 构造 STORAGE_PROPERTY_QUERY + STORAGE_PROTOCOL_SPECIFIC_DATA ----
                // 输入缓冲区开得比实际需要大一点没关系，DeviceIoControl 只看
                // 我们传进去的 nInBufferSize；这里按 PowerShell 版原样用 560。
                int total = QueryHeaderLen + SpecLen + DataLen;   // 8 + 40 + 512 = 560
                byte[] buf = new byte[total];

                BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(buf, 0);   // offset 0
                BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buf, 4);                   // offset 4

                int o = QueryHeaderLen;                    // 结构体起点 offset 8
                BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buf, o + 0);                    // ProtocolType
                BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(buf, o + 4);                 // DataType
                BitConverter.GetBytes(NvmeLogPageSmartHealthInfo).CopyTo(buf, o + 8);          // ProtocolDataRequestValue
                BitConverter.GetBytes((uint)0).CopyTo(buf, o + 12);                            // ProtocolDataRequestSubValue
                BitConverter.GetBytes((uint)SpecLen).CopyTo(buf, o + 16);                      // ProtocolDataOffset
                BitConverter.GetBytes((uint)DataLen).CopyTo(buf, o + 20);                      // ProtocolDataLength

                // 输入输出共用同一个缓冲区（原地查询），和 PowerShell 版一致。
                uint ret = 0;
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)total,
                                     buf, (uint)total, out ret, IntPtr.Zero))
                    return null;   // 盘不支持该属性 / 不是 NVMe / 被驱动拒绝

                if (ret < (uint)(QueryHeaderLen + 32)) return null;

                // ---- 定位日志页数据起点 ----
                // 回读输出缓冲区 offset 8 处的 ProtocolDataOffset（本机实测 = 40，即
                // sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA)），因此日志页真正的起点是
                // d = 8（STORAGE_PROPERTY_QUERY 头）+ 40 = 48。
                // 千万不要直接写死 40：不同驱动的 ProtocolDataOffset 不一定相同，
                // 这一步取错就会读到全 0（温度 0K 被下面的合理性校验拦掉，返回 null）。
                int protoDataOffset = (int)BitConverter.ToUInt32(buf, ProtocolOffsetField);
                int d = QueryHeaderLen + protoDataOffset;
                if (protoDataOffset <= 0 || d < 0 || d + DataLen > total) return null;

                // ---- 解析 NVMe SMART/Health Information Log（offset 相对 d）----
                //   0      = 关键警告（Critical Warning）
                //   1..2   = 复合温度，单位开尔文（Sensor 1）
                //   3      = 可用备用块百分比
                //   4      = 备用块阈值百分比
                //   5      = 已用寿命百分比
                //   128..143 = 通电小时数（8 字节小端）
                //   200 起 = Temperature Sensor 1..8，每 2 字节一个，开尔文
                int tempK = BitConverter.ToUInt16(buf, d + 1);
                if (tempK < 200 || tempK > 400) return null;   // 明显不合理 => 没读到真数据
                double tempC = tempK - 273.15;

                int spare = buf[d + 3];
                int used = buf[d + 5];

                ulong hours64 = BitConverter.ToUInt64(buf, d + 128);
                // DriveSmartInfo.Hours 是 int，封顶避免溢出成负数（>68 万年的盘不用管）。
                int hours = hours64 > int.MaxValue ? int.MaxValue : (int)hours64;

                // Temperature Sensor 2 = 闪存（NAND）温度，就是 AIDA64 每块盘显示的第二个值。
                // 读不到或为 0 时按契约填 -1。
                int temp2 = -1;
                int o2 = d + 202;
                if (o2 + 2 <= total)
                {
                    int k2 = BitConverter.ToUInt16(buf, o2);
                    if (k2 >= 200 && k2 <= 400) temp2 = (int)Math.Round(k2 - 273.15);
                }

                return new DriveSmartInfo
                {
                    Pnum = pnum,
                    TempC = Math.Round(tempC, 1),
                    Used = used,
                    Spare = spare,
                    Hours = hours,
                    Temp2 = temp2
                };
            }
            catch
            {
                // 驱动异常 / 缓冲区被驱动写坏 / 盘在读取过程中被拔出：
                // 一律当作"读不到"，绝不把异常抛给采集循环。
                return null;
            }
            finally
            {
                // SafeFileHandle 是内核句柄，不释放会泄漏；每个采样周期都开一次，
                // 泄漏几十个就会耗尽句柄。
                if (h != null) h.Dispose();
            }
        }

        // ---- 内部辅助 --------------------------------------------------------

        /// <summary>
        /// 用同一套 IOCTL 读 STORAGE_DEVICE_DESCRIPTOR 的 BusType，判断是否 NVMe。
        /// 只是为了在非 NVMe 盘上少做一次无用的日志页请求，失败时保守返回 true
        /// （宁可多试一次，也不要因为总线类型读不到就漏掉一块真 NVMe 盘）。
        /// </summary>
        private static bool IsNvmeDevice(SafeFileHandle h)
        {
            try
            {
                const int descLen = 1024;
                byte[] buf = new byte[descLen];
                // StorageDeviceProperty = 0，QueryType = PropertyStandardQuery = 0
                BitConverter.GetBytes((uint)0).CopyTo(buf, 0);
                BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buf, 4);

                uint ret = 0;
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)descLen,
                                     buf, (uint)descLen, out ret, IntPtr.Zero))
                    return true;

                // STORAGE_DEVICE_DESCRIPTOR: Version(0) Size(4) DeviceType(8) DeviceTypeModifier(12)
                //   RemovableMedia(16) CommandQueueing(17) VendorIdOffset(20) ... BusType(28)
                if (ret < 32) return true;
                return BitConverter.ToUInt32(buf, 28) == BusTypeNvme;
            }
            catch
            {
                return true;
            }
        }
    }
}
