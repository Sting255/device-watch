// ============================================================================
//  Toast.cs —— Windows 10/11 原生气泡通知（WinRT Toast）
//
//  对应 DeviceWatch.ps1 里的 Initialize-Notifier（第 735 行）与 Show-Alert（第 749 行）。
//
//  【为什么用反射而不是加 WinMD 引用】
//  net48 项目要用 ToastNotificationManager 有两个办法：
//    a) <Reference Include="Windows.Data.dll" /> 指向 C:\Windows\System32\Windows.Data.dll
//       —— 编译机能过，但 WinMD 的引用会进 .config/编译产物，换一台机器、
//          换一个 VS/msbuild 版本都可能解析不到，违背"单 exe 免安装"。
//    b) 运行时用 Type.GetType("…, ContentType=WindowsRuntime") 拿类型再反射调用
//       —— 编译期零依赖，跑在 Win10/11 上必然成功，老系统上失败也只是没通知。
//  这里选 (b)。PowerShell 版的 [Windows.UI.Notifications.ToastNotificationManager,
//  …] 语法本身就是 (b) 的 PowerShell 写法。
//
//  【AppId 不能改】
//  AppUserModelId = "DeviceWatch.Monitor"。Windows 按 AUMID 归类通知，
//  改了名字用户会在通知中心里看到两套历史记录，像装了两个程序。
// ============================================================================
using System;
using System.Globalization;
using System.Reflection;
using Microsoft.Win32;

// 见 Tray.cs 的说明：避开 Microsoft.VisualBasic.Log 的撞名。
using Log = DeviceWatch.Log;

namespace DeviceWatch
{
    public static class Toast
    {
        /// <summary>AppUserModelId。绝对不要改，改了用户会收到重复通知。</summary>
        public const string AppId = "DeviceWatch.Monitor";

        /// <summary>注册表里给这个 AUMID 一个好听的名字和图标（否则通知头显示 exe 名）。</summary>
        private const string DisplayName = "断联哨兵";

        private static bool _inited;

        /// <summary>
        /// 托盘气泡兜底回调。主程序初始化完托盘后挂上：
        /// <c>Toast.FallbackBalloon = (t, b, k) =&gt; tray.ShowBalloon(t, b, k);</c>。
        /// 这里用委托而不是直接引用 Tray 实例，是为了让 Toast 保持"谁调都行"的解耦状态。
        /// 返回 true 表示气泡已弹出。
        /// </summary>
        public static Func<string, string, string, bool> FallbackBalloon;

        private static readonly object TrayBalloonSync = new object();

        /// <summary>
        /// 注册 AUMID（HKCU\SOFTWARE\Classes\AppUserModelId\DeviceWatch.Monitor）。
        /// 只在主循环启动前调一次；失败不抛异常，只记日志 —— 通知是锦上添花，
        /// 绝不能因为它启动不了就整个程序起不来。
        /// </summary>
        public static void Initialize()
        {
            if (_inited) return;
            _inited = true;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(
                           @"SOFTWARE\Classes\AppUserModelId\" + AppId))
                {
                    if (k != null)
                    {
                        k.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
                        // imageres.dll,-1015 是系统自带的"显示器/电脑"图标，免外部 ico
                        k.SetValue("IconUri",
                                   Environment.GetEnvironmentVariable("SystemRoot") + @"\System32\imageres.dll,-1015",
                                   RegistryValueKind.String);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("注册通知 AppId 失败（通知可能显示为 exe 名）：" + ex.Message, "Warn");
            }
        }

        /// <summary>
        /// 弹一条通知。kind: Off / On / Warn / Info。
        /// Off 用 duration='long'（断联要看得见），其余用设置里的 ToastDuration。
        /// 任何失败都吞掉：通知失败绝不能影响主流程。
        /// </summary>
        public static void Show(string title, string body, string kind)
        {
            if (string.IsNullOrEmpty(kind)) kind = "Info";
            try
            {
                Initialize();

                // 用户关了弹窗总开关就直接返回（调用方仍然会写日志，记录不受影响）
                bool notify = true;
                try { notify = Settings.I.Notify; } catch { }
                if (!notify) return;

                // 托盘菜单的「暂停弹窗通知」和「学习模式 10 分钟」也要在这里统一生效 ——
                // 否则只有设备模块自己去查这两个开关，其它模块直接调 Show() 就绕过去了。
                try
                {
                    if (!AppState.I.NotifyEnabled) return;
                    if (AppState.I.Learning)
                    {
                        if (AppState.I.LearnUntil.HasValue && DateTime.Now < AppState.I.LearnUntil.Value) return;
                        AppState.I.Learning = false;   // 学习时间已过，自动恢复
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(title)) title = DisplayName;
                if (body == null) body = "";

                bool sound = false;
                try { sound = Settings.I.Sound; } catch { }

                string duration;
                if (string.Equals(kind, "Off", StringComparison.OrdinalIgnoreCase)) duration = "long";
                else
                {
                    duration = "short";
                    try
                    {
                        string d = Settings.I.ToastDuration;
                        if (!string.IsNullOrEmpty(d)) duration = d;
                    }
                    catch { }
                }

                // 提示音统一由 SystemSounds 负责（和 PowerShell 版一致），
                // 所以 XML 里显式 silent='true'，避免"系统音 + 提示音"响两下。
                bool xmlOk = ShowNative(title, body, duration);

                if (sound)
                {
                    try
                    {
                        if (string.Equals(kind, "Off", StringComparison.OrdinalIgnoreCase))
                            System.Media.SystemSounds.Hand.Play();
                        else if (string.Equals(kind, "On", StringComparison.OrdinalIgnoreCase))
                            System.Media.SystemSounds.Asterisk.Play();
                    }
                    catch { }
                }

                if (!xmlOk)
                {
                    // WinRT 走不通时（老系统 / 通知被策略禁用）退回托盘气泡，
                    // 至少让用户看到；托盘没起来就只剩日志了。
                    lock (TrayBalloonSync)
                    {
                        if (FallbackBalloon != null)
                        {
                            try { FallbackBalloon(title, body, kind); } catch { }
                        }
                    }
                }
            }
            catch { /* 通知失败绝不影响主流程 */ }
        }

        /// <summary>
        /// 真正的 WinRT 调用。成功返回 true。
        ///
        /// 【为什么一半反射、一半 dynamic —— 这不是风格混搭，是实测结论】
        /// WinRT 对象经 CLR 投影后只实现 IInspectable，不实现 IDispatch。
        /// 本机实测（.NET Framework 4.8）：
        ///   ✔ Type.GetType(..., ContentType=WindowsRuntime)        拿到投影类型
        ///   ✔ Type.InvokeMember 调**静态**方法 CreateToastNotifier  成功
        ///   ✔ Activator.CreateInstance(XmlDocument)                成功
        ///   ✘ Type.InvokeMember 调**实例**方法 LoadXml
        ///       → TargetInvocationException: "COM 目标不会实现 IDispatch"
        ///   ✔ (dynamic)doc.LoadXml(...)                            成功（走 WinRT binder）
        /// 所以：类型解析和对象构造继续用反射（零编译期引用，老系统上失败也只是没通知），
        /// 实例方法一律用 dynamic 调。缺 Microsoft.CSharp 引用时编译不过，csproj 里已加。
        /// </summary>
        private static bool ShowNative(string title, string body, string duration)
        {
            try
            {
                // ContentType=WindowsRuntime 是给 Type.GetType 的"从 WinMD 加载"指令，
                // 少了它 CLR 会去 GAC 里找同名的 .NET 类型，必然返回 null。
                Type mgrType = Type.GetType(
                    "Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime");
                Type xmlType = Type.GetType(
                    "Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime");
                Type toastType = Type.GetType(
                    "Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime");
                if (mgrType == null || xmlType == null || toastType == null) return false;

                // 建 notifier 带 AUMID 参数：不依赖快捷方式，注册表已把这个 Id 登记好了
                object notifierObj = mgrType.InvokeMember(
                    "CreateToastNotifier",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod,
                    null, null, new object[] { AppId },
                    CultureInfo.InvariantCulture);
                if (notifierObj == null) return false;

                // 单引号包属性值，双引号留给可能的属性；文本节点走 Escape()，
                // 否则设备名里的 & < > 会让 LoadXml 直接抛异常，整条通知消失。
                string xml = "<toast duration='" + Escape(duration) + "'>" +
                             "<visual><binding template='ToastGeneric'>" +
                             "<text>" + Escape(title) + "</text>" +
                             "<text>" + Escape(body) + "</text>" +
                             "</binding></visual>" +
                             "<audio silent='true'/></toast>";

                dynamic doc = Activator.CreateInstance(xmlType);
                doc.LoadXml(xml);                                    // 必须 dynamic，见方法注释

                dynamic toast = Activator.CreateInstance(toastType, new object[] { (object)doc });
                if (toast == null) return false;

                dynamic notifier = notifierObj;
                notifier.Show(toast);                                // 同样必须 dynamic
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("WinRT 通知发送失败，尝试托盘气泡：" + Inner(ex), "Warn");
                return false;
            }
        }

        /// <summary>取最内层异常：反射/绑定包了几层，只看 Message 会丢掉真正的原因。</summary>
        private static string Inner(Exception ex)
        {
            Exception e = ex;
            int guard = 0;
            while (e.InnerException != null && guard++ < 8) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>
        /// XML 转义。通知正文里出现 "R&amp;D"、"&lt;未知设备&gt;" 这类字符时，
        /// 不转义会让 LoadXml 直接抛异常 —— 整条通知就没了。
        /// </summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }
    }
}
