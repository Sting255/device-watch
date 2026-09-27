// ============================================================================
//  WebServer.cs —— 本地网页面板（System.Net.HttpListener）
//
//  设计要点（每一条都是从 PowerShell 版的实测教训里来的）：
//
//  1. 只绑 127.0.0.1：不对外网开放，而且 http://127.0.0.1:8787/ 这种回环前缀
//     不需要管理员权限；写成 http://+:8787/ 或 http://*:8787/ 就会要求管理员。
//
//  2. 接受连接是"异步挂任务"：Start() 里调一次 GetContextAsync() 把任务存起来，
//     之后 Pump() 只查这个任务的 IsCompleted。**没有任何一处同步等待请求。**
//
//  3. **Pump() 必须极快返回**。主循环每 50ms 调一次 Pump()，所以它里面绝对不能
//     出现"等一个请求进来"的阻塞调用（GetContext() / Task.Wait() / Thread.Sleep /
//     await 同步化 之类）。PowerShell 版就在这里踩过大坑：/api/state 里塞了一次
//     500ms 的计划任务查询，前端每秒轮询一次，主循环每秒被卡 4.5 秒，
//     U 盘插拔要好几秒后才被发现，网页也跟着一顿一顿的。
//     这里的规矩很简单：
//         没有请求      → 立刻 return（一次 bool 判断的开销）
//         有请求        → 取出来处理；处理前先把下一轮的异步接受任务挂上
//     除了 /api/inventory（本来就慢，前端会自己提示"大约需要几秒"），
//     所有接口都只读内存字段，不做 WMI / 文件 / 进程操作。
// ============================================================================
using System;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;

namespace DeviceWatch
{
    public sealed class WebServer
    {
        private HttpListener _listener;
        private Task<HttpListenerContext> _ctxTask;
        private bool _running;
        private string _url = "";

        /// <summary>网页面板根地址，例如 http://127.0.0.1:8787/（结尾带 /，可直接丢给浏览器）。</summary>
        public string Url { get { return _url; } }

        /// <summary>监听是否处于活动状态。</summary>
        public bool Running { get { return _running; } }

        /// <summary>
        /// 启动监听。端口被占用、前缀无权限等情况返回 false（不抛异常出去）。
        /// </summary>
        public bool Start(int port)
        {
            if (_running) return true;
            if (port < 1 || port > 65535) port = 8787;      // 配置坏了也别让界面打不开

            HttpListener l = null;
            try
            {
                l = new HttpListener();
                // 回环地址：免管理员，且只有本机能访问
                string prefix = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                l.Prefixes.Add(prefix);
                l.Start();                                  // 端口被占用时在这里抛 HttpListenerException

                _listener = l;
                _url = prefix;
                _running = true;

                // 关键：现在就挂上"接受下一个请求"的异步任务，
                // 之后 Pump() 只是查它的状态，绝不会为此阻塞主循环。
                _ctxTask = l.GetContextAsync();

                Log.Write("网页面板已启动：" + prefix, "网页");
                return true;
            }
            catch (Exception ex)
            {
                // 最常见原因：端口被别的程序占了（或者被上一次没退干净的自己占着）
                _running = false;
                _ctxTask = null;
                _listener = null;
                _url = "";
                try { if (l != null) l.Close(); } catch { /* 已经关掉了就算了 */ }
                Log.Write("网页面板启动失败（端口 " + port + "）：" + ex.Message, "网页");
                return false;
            }
        }

        /// <summary>
        /// 非阻塞地处理一个待处理请求。由主循环每 50ms 调一次。
        /// 没有请求时只做一次 IsCompleted 判断就返回 —— 这是本类最重要的一条约定。
        /// </summary>
        public void Pump()
        {
            Task<HttpListenerContext> t = _ctxTask;
            if (t == null || !t.IsCompleted) return;        // 绝大多数轮次走这条路径，零等待

            HttpListenerContext ctx = null;
            try
            {
                ctx = t.Result;
            }
            catch (Exception ex)
            {
                // Stop() 之后旧任务会以异常收尾，这属于正常情况，不用刷日志
                if (_running) Log.Write("接受网页连接失败：" + ex.Message, "网页");
            }

            // 先重新挂上异步接受任务，再去处理当前请求：
            // 这样即使某个接口跑得久（/api/inventory 要枚举几百个设备），
            // 也不会漏掉期间进来的下一个连接。
            if (_running && _listener != null)
            {
                try { _ctxTask = _listener.GetContextAsync(); }
                catch (Exception ex)
                {
                    _ctxTask = null;
                    Log.Write("重新挂接网页监听失败：" + ex.Message, "网页");
                    _running = false;
                }
            }
            else
            {
                _ctxTask = null;
            }

            if (ctx == null) return;

            // 一个请求出错绝不能把服务器带崩：这里兜底，最差也回个 500
            try
            {
                WebApi.Handle(ctx);
            }
            catch (Exception ex)
            {
                Log.Write("处理网页请求出错：" + ex.Message, "网页");
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch { /* 客户端可能已经跑了 */ }
            }
        }

        /// <summary>停止监听并释放端口。可重复调用。</summary>
        public void Stop()
        {
            _running = false;
            _ctxTask = null;

            HttpListener l = _listener;
            _listener = null;
            if (l == null) return;

            try { l.Stop(); }
            catch (Exception ex) { Log.Write("停止网页面板出错：" + ex.Message, "网页"); }
            try { l.Close(); } catch { /* 已经关掉了就算了 */ }

            // _url 保留：调用方在退出流程里可能还要用它写日志 / 提示
        }
    }
}
