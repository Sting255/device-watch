// ============================================================================
//  Models.cs —— 所有模块共用的数据契约
//
//  字段名严格对齐 src\_contract\CONTRACT.md（从运行中的 PowerShell 版抓取）。
//  改这里的字段名 = 改网页前端的契约，除非同步改 src\ui\index.html，否则会显示空白。
// ============================================================================
using System;
using System.Collections.Generic;

namespace DeviceWatch
{
    /// <summary>一个传感器的读数。name 必须是中文（见 CnSensorNames）。</summary>
    public sealed class SensorReading
    {
        public string Name;
        public double Value;
        public SensorReading() { }
        public SensorReading(string name, double value) { Name = name; Value = value; }
        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> { { "name", Name }, { "value", Math.Round(Value, 2) } };
        }
    }

    /// <summary>低层传感器（LibreHardwareMonitor / NVMe 之外的补充来源，需管理员）。</summary>
    public sealed class ExtSensors
    {
        public string At = "";
        public string Source = "";
        public List<SensorReading> MemoryTemps = new List<SensorReading>();
        public List<SensorReading> CpuTemps = new List<SensorReading>();
        public List<SensorReading> Fans = new List<SensorReading>();
        public List<SensorReading> Power = new List<SensorReading>();
        public List<SensorReading> GpuTemps = new List<SensorReading>();
        public List<SensorReading> Volts = new List<SensorReading>();

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["at"] = At;
            d["source"] = Source;
            d["memoryTemps"] = MemoryTemps.ConvertAll(x => (object)x.ToJson());
            d["cpuTemps"] = CpuTemps.ConvertAll(x => (object)x.ToJson());
            d["fans"] = Fans.ConvertAll(x => (object)x.ToJson());
            d["power"] = Power.ConvertAll(x => (object)x.ToJson());
            d["gpuTemps"] = GpuTemps.ConvertAll(x => (object)x.ToJson());
            d["volts"] = Volts.ConvertAll(x => (object)x.ToJson());
            return d;
        }
    }

    /// <summary>一个分区的空间信息。</summary>
    public sealed class DiskVolume
    {
        public string Mount = "";      // "C:"
        public double TotalGB;
        public double FreeGB;
        public double Pct;
        public int Pnum = -1;          // 所属物理磁盘号
        public string Phys = "";       // 物理磁盘型号

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "m", Mount }, { "totalGB", Math.Round(TotalGB, 1) }, { "freeGB", Math.Round(FreeGB, 1) },
                { "pct", Math.Round(Pct, 1) }, { "pnum", Pnum }, { "phys", Phys }
            };
        }
    }

    /// <summary>NVMe SMART 直读结果。</summary>
    public sealed class DriveSmartInfo
    {
        public int Pnum;
        public double TempC;           // 复合温度（Sensor 1）
        public int Used;               // 已用寿命百分比
        public int Spare;              // 可用备用块百分比
        public int Hours;              // 通电小时
        public int Temp2 = -1;         // 闪存温度（Sensor 2），读不到为 -1

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "pnum", Pnum }, { "tempC", Math.Round(TempC, 1) }, { "used", Used },
                { "spare", Spare }, { "hours", Hours }, { "temp2", Temp2 }
            };
        }
    }

    /// <summary>每块物理磁盘的实时读写速率。</summary>
    public sealed class DiskIoInfo
    {
        public int Pnum;
        public double ReadKB;
        public double WriteKB;

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "pnum", Pnum }, { "readKB", Math.Round(ReadKB, 1) }, { "writeKB", Math.Round(WriteKB, 1) }
            };
        }
    }

    /// <summary>一根内存条。</summary>
    public sealed class MemModuleInfo
    {
        public string Part = "";
        public string Mfg = "";
        public int SizeGB;
        public int Speed;
        public int Rated;
        public int Ddr;
        public string Slot = "";

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "part", Part }, { "mfg", Mfg }, { "sizeGB", SizeGB },
                { "speed", Speed }, { "rated", Rated }, { "ddr", Ddr }, { "slot", Slot }
            };
        }
    }

    /// <summary>
    /// 硬件状态。字段名 = 网页前端的契约，不要改。
    /// 可空项（GpuTempC 等）用 double? / int?，读不到时输出 null。
    /// </summary>
    public sealed class HwState
    {
        public double CpuPct;
        public double CpuUserPct;
        public int CpuKernelPct;
        public string CpuName = "";
        public int CpuCores;
        public int CpuThreads;
        public double? CpuTempC;
        public int CpuMaxMHz;
        public int CpuCurMHz;
        public int CpuL2MB;
        public int CpuL3MB;

        public double MemTotalGB;
        public double MemUsedGB;
        public double MemPct;
        public double MemFreeGB;
        public double MemCommitGB;
        public double MemCommitMaxGB;

        public string GpuName;
        public int? GpuTempC;
        public int? GpuUtilPct;
        public int? GpuMemPct;
        public int? GpuMemUsedMB;
        public int? GpuMemTotalMB;
        public double? GpuPowerW;
        public int? GpuPowerMaxW;
        public int? GpuClockMHz;
        public int? GpuMemClockMHz;
        public int? GpuFanPct;
        public string GpuPstate;

        public double DiskReadKB;
        public double DiskWriteKB;
        public double NetRxKB;
        public double NetTxKB;

        public List<DiskVolume> Disks = new List<DiskVolume>();
        public List<DriveSmartInfo> DriveSmart = new List<DriveSmartInfo>();
        public List<DiskIoInfo> DiskIo = new List<DiskIoInfo>();
        public List<MemModuleInfo> MemModules = new List<MemModuleInfo>();

        public int? BatPct;
        public bool? OnAC;
        public string Uptime = "";
        public string LastUpdate = "";

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["CpuPct"] = Math.Round(CpuPct, 1);
            d["CpuUserPct"] = Math.Round(CpuUserPct, 1);
            d["CpuKernelPct"] = CpuKernelPct;
            d["CpuName"] = CpuName;
            d["CpuCores"] = CpuCores;
            d["CpuThreads"] = CpuThreads;
            d["CpuTempC"] = CpuTempC.HasValue ? (object)Math.Round(CpuTempC.Value, 1) : null;
            d["CpuMaxMHz"] = CpuMaxMHz;
            d["CpuCurMHz"] = CpuCurMHz;
            d["CpuL2MB"] = CpuL2MB;
            d["CpuL3MB"] = CpuL3MB;

            d["MemTotalGB"] = Math.Round(MemTotalGB, 1);
            d["MemUsedGB"] = Math.Round(MemUsedGB, 1);
            d["MemPct"] = Math.Round(MemPct, 1);
            d["MemFreeGB"] = Math.Round(MemFreeGB, 1);
            d["MemCommitGB"] = Math.Round(MemCommitGB, 1);
            d["MemCommitMaxGB"] = Math.Round(MemCommitMaxGB, 1);

            d["GpuName"] = GpuName;
            d["GpuTempC"] = GpuTempC.HasValue ? (object)GpuTempC.Value : null;
            d["GpuUtilPct"] = GpuUtilPct.HasValue ? (object)GpuUtilPct.Value : null;
            d["GpuMemPct"] = GpuMemPct.HasValue ? (object)GpuMemPct.Value : null;
            d["GpuMemUsedMB"] = GpuMemUsedMB.HasValue ? (object)GpuMemUsedMB.Value : null;
            d["GpuMemTotalMB"] = GpuMemTotalMB.HasValue ? (object)GpuMemTotalMB.Value : null;
            d["GpuPowerW"] = GpuPowerW.HasValue ? (object)Math.Round(GpuPowerW.Value, 2) : null;
            d["GpuPowerMaxW"] = GpuPowerMaxW.HasValue ? (object)GpuPowerMaxW.Value : null;
            d["GpuClockMHz"] = GpuClockMHz.HasValue ? (object)GpuClockMHz.Value : null;
            d["GpuMemClockMHz"] = GpuMemClockMHz.HasValue ? (object)GpuMemClockMHz.Value : null;
            d["GpuFanPct"] = GpuFanPct.HasValue ? (object)GpuFanPct.Value : null;
            d["GpuPstate"] = GpuPstate;

            d["DiskReadKB"] = Math.Round(DiskReadKB, 1);
            d["DiskWriteKB"] = Math.Round(DiskWriteKB, 1);
            d["NetRxKB"] = Math.Round(NetRxKB, 1);
            d["NetTxKB"] = Math.Round(NetTxKB, 1);

            d["Disks"] = Disks.ConvertAll(x => (object)x.ToJson());
            d["DriveSmart"] = DriveSmart.ConvertAll(x => (object)x.ToJson());
            d["DiskIo"] = DiskIo.ConvertAll(x => (object)x.ToJson());
            d["MemModules"] = MemModules.ConvertAll(x => (object)x.ToJson());

            d["BatPct"] = BatPct.HasValue ? (object)BatPct.Value : null;
            d["OnAC"] = OnAC.HasValue ? (object)OnAC.Value : null;
            d["Uptime"] = Uptime;
            d["LastUpdate"] = LastUpdate;
            return d;
        }
    }

    /// <summary>网络适配器状态。</summary>
    public sealed class AdapterInfo
    {
        public string Name = "";
        public string State = "";      // Up / Down
        public string Speed = "";

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> { { "name", Name }, { "state", State }, { "speed", Speed } };
        }
    }

    /// <summary>一条设备变化记录。</summary>
    public sealed class DeviceEvent
    {
        public int Seq;
        public DateTime Time;
        public string Kind = "";       // 断开 / 接入
        public string Name = "";
        public string Id = "";
        public string Detail = "";
        public bool Notified;

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "seq", Seq }, { "t", Time.ToString("HH:mm:ss") },
                { "kind", Kind }, { "label", Name }, { "id", Id }, { "detail", Detail }
            };
        }
    }

    /// <summary>按物理设备统计的断联次数。</summary>
    public sealed class DeviceStat
    {
        public string Key = "";
        public string Name = "";
        public int Off;
        public int On;

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "key", Key }, { "name", Name }, { "off", Off }, { "on", On }
            };
        }
    }

    /// <summary>异常设备条目。</summary>
    public sealed class ProblemDevice
    {
        public string Name = "";
        public string Id = "";
        public int Problem;
        public string Class = "";

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "name", Name }, { "id", Id }, { "problem", Problem }, { "class", Class }
            };
        }
    }

    /// <summary>外网探测目标。</summary>
    public sealed class PingTarget
    {
        public string T = "";
        public int Ms = -1;
        public bool Ok;
        public bool Gw;                // 是否是网关（0ms 那条）

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "t", T }, { "ms", Ms }, { "ok", Ok }, { "gw", Gw }
            };
        }
    }

    /// <summary>在线设备（用于 /api/devices）。</summary>
    public sealed class DeviceEntry
    {
        public string Id = "";
        public string Name = "";
        public string Friendly = "";
        public string Desc = "";
        public string Class = "";
        public string Enumerator = "";
        public int Problem;
        public uint Status;
        public string Reported = "";
    }
}
