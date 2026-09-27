# 第三方组件

`lib\` 目录下的 DLL 是 **LibreHardwareMonitor** 及其依赖，用于读取需要内核驱动
才能访问的传感器（内存温度、CPU 封装功耗、GPU 核心热点等）。

| | |
|---|---|
| 项目 | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) |
| 许可 | Mozilla Public License 2.0 (MPL-2.0) |
| 分发方式 | 未修改的二进制形式，符合 MPL-2.0 的要求 |

如需修改其源码，请遵循 MPL-2.0 的规定公开修改后的源码。

本项目的其余代码（`DeviceWatch.ps1`、`启动器源码.cs` 等）均为原创，按 [MIT 许可](LICENSE) 发布。