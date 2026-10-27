// ============================================================================
//  TcpCoordinator.cs —— TCP 协调助手（单文件 / 零第三方依赖）
//
//  一个极轻量的“协调中枢”：让多个进程、脚本、程序在同一个 TCP 端口上
//  互相发现、点名通信、抢占租约锁、共享少量状态。适合做小规模的
//  任务分发、主从协调、多机器人/多进程互相等待这类场景。
//
//  编译（Windows 自带的 .NET Framework 4.x 编译器即可，不用装 SDK）：
//      csc /target:exe /out:TcpCoordinator.exe /codepage:65001 TcpCoordinator.cs
//
//  运行：
//      TcpCoordinator.exe --port 9000
//      TcpCoordinator.exe --port 9000 --verbose
//      TcpCoordinator.exe --connect 127.0.0.1:9000 --name alice --role worker
//
//  命令行参数：
//      --bind <ip>            监听地址，默认 0.0.0.0
//      --port <n>             监听端口，默认 9000；填 0 表示让系统随机分配
//      --max-clients <n>      最大连接数，默认 64
//      --idle <秒>            空闲超时，默认 60 秒，超时会断开
//      --hello <秒>           连上但没登记名字的超时，默认 15 秒
//      --ping <秒>            空闲多久主动发心跳，默认 20 秒
//      --verbose              打印每条收到的命令
//      --connect <host:port>  以客户端身份连上去，进入交互模式
//      --name <名字>          客户端模式下的登记名
//      --role <角色>          客户端模式下的角色，默认 peer
//      --help                 看这条帮助
//
//  ---------------------------------------------------------------------------
//  协议：UTF-8 文本行，一行一条命令，以 \n 结尾（\r\n 也接受）
//  ---------------------------------------------------------------------------
//  客户端 -> 服务端
//      HELLO <name> [role]     登记名字（连上后必须先做，名字全局唯一）
//      LIST                    列出在线成员
//      SAY <to> <text>         发消息，to = 名字 | #角色 | *（所有人）
//      WHOAMI                  看看自己是谁
//      LOCK <key> [ttl]        抢租约锁，默认 60 秒，范围 5~3600
//      UNLOCK <key>            释放自己持有的锁
//      LOCKS                   列出当前有效的锁
//      SET <key> <value>       写共享值
//      GET <key>               读共享值
//      DEL <key>               删共享值
//      BOARD                   列出全部共享值
//      PING                    心跳，服务端回 PONG
//      QUIT                    退出
//
//  服务端 -> 客户端
//      WELCOME <connId> TcpCoordinator/1.0    连接一建立就发送
//      OK <cmd> [args...]                     命令成功
//      ERR <code> [detail...]                 命令失败
//      PEERS <count> [name:role ...]          成员列表
//      PRESENCE <join|leave> <name> <role>    有人上线/下线
//      MSG <from> <fromRole> <target> <text>   收到消息
//      VALUE <key> <value>                     GET 的结果
//      LOCKS <count> / LK <key> <owner> <ttl> / END
//      BOARD <count> / KV <key> <value> / END
//      PONG                                   心跳回应
//      BYE <reason>                           服务端要断开你
//
//  常见 ERR code：hello_required / already_hello / bad_name / name_taken /
//  unknown_peer / no_recipients / locked / not_owner / not_locked /
//  bad_key / bad_ttl / unknown_command / line_too_long / server_full
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TcpCoordinator
{
    /// <summary>入口：解析参数，托管协调器的生命周期。</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            ConfigureConsoleOutput();

            Options options;
            string error;
            if (!Options.TryParse(args, out options, out error))
            {
                Console.Error.WriteLine("参数错误：" + error);
                Console.Error.WriteLine();
                Options.PrintUsage(Console.Error);
                return 2;
            }

            if (options.ShowHelp)
            {
                Options.PrintUsage(Console.Out);
                return 0;
            }

            if (options.ConnectTarget != null)
            {
                return InteractiveClient.Run(options.ConnectTarget, options.ClientName, options.ClientRole);
            }

            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    Console.WriteLine();
                    Log.Info("收到 Ctrl+C，准备停止");
                    cts.Cancel();
                };
                Console.CancelKeyPress += handler;

                try
                {
                    using (Coordinator server = new Coordinator(options, cts.Token))
                    {
                        server.Run();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("服务异常退出：" + ex);
                    return 1;
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            }

            Log.Info("已退出");
            return 0;
        }

        /// <summary>
        /// 让中文日志在“重定向到文件/管道”和“控制台码页装不下中文”两种情况下都不变成问号。
        /// 重定向时统一用 UTF-8；控制台能正常显示中文时保持原样。
        /// </summary>
        private static void ConfigureConsoleOutput()
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                    return;
                }
                if (!CanEncode(Console.OutputEncoding, '协'))
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                }
            }
            catch (Exception)
            {
                // 没有控制台或句柄不可用时忽略，退回系统默认编码
            }
        }

        private static bool CanEncode(Encoding encoding, char probe)
        {
            try
            {
                string roundTrip = encoding.GetString(encoding.GetBytes(probe.ToString()));
                return string.Equals(roundTrip, probe.ToString(), StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------------
    //  命令行参数
    // ------------------------------------------------------------------------
    internal sealed class Options
    {
        public string BindAddress { get; private set; }
        public int Port { get; private set; }
        public int MaxClients { get; private set; }
        public int IdleTimeoutSeconds { get; private set; }
        public int HelloTimeoutSeconds { get; private set; }
        public int PingIntervalSeconds { get; private set; }
        public bool Verbose { get; private set; }
        public bool ShowHelp { get; private set; }
        public string ConnectTarget { get; private set; }
        public string ClientName { get; private set; }
        public string ClientRole { get; private set; }

        private Options()
        {
            BindAddress = "0.0.0.0";
            Port = 9000;
            MaxClients = 64;
            IdleTimeoutSeconds = 60;
            HelloTimeoutSeconds = 15;
            PingIntervalSeconds = 20;
            Verbose = false;
            ShowHelp = false;
            ConnectTarget = null;
            ClientName = null;
            ClientRole = "peer";
        }

        public static bool TryParse(string[] args, out Options options, out string error)
        {
            options = new Options();
            error = null;
            if (args == null || args.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string value;
                string name = SplitOption(args[i], out value);

                switch (name)
                {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;

                    case "--verbose":
                    case "-v":
                        options.Verbose = true;
                        break;

                    case "--bind":
                        if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                        options.BindAddress = value;
                        break;

                    case "--port":
                    case "-p":
                        {
                            int port;
                            if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 0 || port > 65535)
                            {
                                error = "--port 需要 0~65535 之间的整数，收到 " + value;
                                return false;
                            }
                            options.Port = port;
                            break;
                        }

                    case "--max-clients":
                        {
                            int max;
                            if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out max) || max < 1)
                            {
                                error = "--max-clients 需要正整数，收到 " + value;
                                return false;
                            }
                            options.MaxClients = max;
                            break;
                        }

                    case "--idle":
                        if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                        if (!TryParseSeconds(value, 5, out error)) return false;
                        options.IdleTimeoutSeconds = int.Parse(value, CultureInfo.InvariantCulture);
                        break;

                    case "--hello":
                        if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                        if (!TryParseSeconds(value, 1, out error)) return false;
                        options.HelloTimeoutSeconds = int.Parse(value, CultureInfo.InvariantCulture);
                        break;

                    case "--ping":
                        if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                        if (!TryParseSeconds(value, 1, out error)) return false;
                        options.PingIntervalSeconds = int.Parse(value, CultureInfo.InvariantCulture);
                        break;

                    case "--connect":
                        if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                        options.ConnectTarget = value;
                        break;

                    case "--name":
                        if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                        options.ClientName = value;
                        break;

                    case "--role":
                        if (!NeedValue(args, ref i, value, name, out value, out error)) return false;
                        options.ClientRole = value;
                        break;

                    default:
                        error = "未知参数 " + args[i];
                        return false;
                }
            }

            // 心跳必须比空闲超时短，否则还没等到心跳就被判定为掉线
            if (options.PingIntervalSeconds * 2 > options.IdleTimeoutSeconds)
            {
                options.PingIntervalSeconds = Math.Max(1, options.IdleTimeoutSeconds / 2);
            }
            if (options.HelloTimeoutSeconds > options.IdleTimeoutSeconds)
            {
                options.HelloTimeoutSeconds = options.IdleTimeoutSeconds;
            }

            return true;
        }

        /// <summary>支持 "--port 9000" 和 "--port=9000" 两种写法。</summary>
        private static string SplitOption(string arg, out string inlineValue)
        {
            inlineValue = null;
            int eq = arg.IndexOf('=');
            if (eq <= 0)
            {
                return arg;
            }
            inlineValue = arg.Substring(eq + 1);
            return arg.Substring(0, eq);
        }

        private static bool NeedValue(string[] args, ref int index, string inlineValue, string optionName, out string value, out string error)
        {
            error = null;
            if (inlineValue != null)
            {
                value = inlineValue;
                return true;
            }
            if (index + 1 >= args.Length)
            {
                value = null;
                error = optionName + " 缺少取值";
                return false;
            }
            index++;
            value = args[index];
            return true;
        }

        private static bool TryParseSeconds(string text, int minimum, out string error)
        {
            int seconds;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds < minimum)
            {
                error = "需要不小于 " + minimum + " 的整数秒，收到 " + text;
                return false;
            }
            error = null;
            return true;
        }

        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("TcpCoordinator " + Coordinator.Version + " —— TCP 协调助手");
            writer.WriteLine();
            writer.WriteLine("用法：");
            writer.WriteLine("  TcpCoordinator.exe --port 9000 [其他参数]        启动协调服务");
            writer.WriteLine("  TcpCoordinator.exe --connect <host:port> --name <名字> [--role <角色>]");
            writer.WriteLine("                                                   连上去并进入交互模式");
            writer.WriteLine();
            writer.WriteLine("服务端参数：");
            writer.WriteLine("  --bind <ip>          监听地址，默认 0.0.0.0");
            writer.WriteLine("  --port <n>           监听端口，默认 9000，0 表示随机分配");
            writer.WriteLine("  --max-clients <n>    最大连接数，默认 64");
            writer.WriteLine("  --idle <秒>          空闲超时，默认 60");
            writer.WriteLine("  --hello <秒>         登记超时，默认 15");
            writer.WriteLine("  --ping <秒>          心跳间隔，默认 20");
            writer.WriteLine("  --verbose            打印每条收到的命令");
            writer.WriteLine();
            writer.WriteLine("客户端参数：");
            writer.WriteLine("  --connect <主机:端口>");
            writer.WriteLine("  --name <名字>        HELLO 用的名字，必填");
            writer.WriteLine("  --role <角色>        默认 peer");
            writer.WriteLine();
            writer.WriteLine("交互模式下直接输入协议命令，例如 HELLO alice worker、LIST、SAY bob 你好。");
        }
    }

    // ------------------------------------------------------------------------
    //  日志
    // ------------------------------------------------------------------------
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Info(string message)
        {
            Write("INFO", message, ConsoleColor.Gray);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, ConsoleColor.DarkYellow);
        }

        public static void Error(string message)
        {
            Write("ERR ", message, ConsoleColor.Red);
        }

        public static void Debug(string message)
        {
            Write("DBG ", message, ConsoleColor.DarkGray);
        }

        private static void Write(string level, string message, ConsoleColor color)
        {
            try
            {
                lock (Gate)
                {
                    bool colored = !Console.IsOutputRedirected;
                    try
                    {
                        if (colored)
                        {
                            Console.ForegroundColor = color;
                        }
                        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + level + "  " + message);
                    }
                    finally
                    {
                        if (colored)
                        {
                            Console.ResetColor();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 没有控制台时（例如被 GUI 内嵌）静默丢弃日志，绝不影响业务
            }
        }
    }

    // ------------------------------------------------------------------------
    //  租约锁
    // ------------------------------------------------------------------------
    internal sealed class Lease
    {
        public Lease(string key, string owner, string ownerId, DateTime expiresAt)
        {
            Key = key;
            Owner = owner;
            OwnerId = ownerId;
            ExpiresAt = expiresAt;
        }

        public string Key { get; private set; }
        public string Owner { get; private set; }
        public string OwnerId { get; private set; }
        public DateTime ExpiresAt { get; private set; }

        public int RemainingSeconds(DateTime now)
        {
            double remaining = (ExpiresAt - now).TotalSeconds;
            if (remaining <= 0)
            {
                return 0;
            }
            return (int)Math.Ceiling(remaining);
        }
    }

    // ------------------------------------------------------------------------
    //  协调器本体
    // ------------------------------------------------------------------------
    internal sealed class Coordinator : IDisposable
    {
        public const string Version = "1.0";
        public const string Banner = "TcpCoordinator/" + Version;

        private const int MaxLineChars = 8192;
        private const int MaxMessageChars = 4096;
        private const int MaxValueChars = 2048;
        private const int BacklogSize = 128;

        private readonly Options _options;
        private readonly CancellationToken _shutdown;
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<string, Client> _clients;      // clientId -> client
        private readonly ConcurrentDictionary<string, string> _names;        // 名字 -> clientId
        private readonly Dictionary<string, Lease> _leases;                  // key -> 租约，_leaseGate 保护
        private readonly object _leaseGate = new object();
        private readonly ConcurrentDictionary<string, string> _board;        // key -> 共享值
        private readonly Thread _acceptThread;
        private readonly Thread _housekeepingThread;
        private int _connectionSeq;
        private bool _disposed;

        public Coordinator(Options options, CancellationToken shutdown)
        {
            _options = options;
            _shutdown = shutdown;
            _clients = new ConcurrentDictionary<string, Client>(StringComparer.Ordinal);
            _names = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            _leases = new Dictionary<string, Lease>(StringComparer.Ordinal);
            _board = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            _listener = new TcpListener(ResolveBindAddress(options.BindAddress), options.Port);

            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "accept";

            _housekeepingThread = new Thread(HousekeepingLoop);
            _housekeepingThread.IsBackground = true;
            _housekeepingThread.Name = "housekeeping";
        }

        // ---------------------------------------------------------------- 启动/停止

        public void Run()
        {
            _listener.Start(BacklogSize);
            IPEndPoint local = (IPEndPoint)_listener.LocalEndpoint;

            Log.Info(Banner + " 启动");
            Log.Info("监听 " + _options.BindAddress + "，实际端口 " + local.Port.ToString(CultureInfo.InvariantCulture));
            Log.Info("上限 " + _options.MaxClients + " 连接，空闲 " + _options.IdleTimeoutSeconds + " 秒超时；Ctrl+C 停止");

            _acceptThread.Start();
            _housekeepingThread.Start();

            _shutdown.WaitHandle.WaitOne();
            Stop();
        }

        private void Stop()
        {
            Log.Info("正在停止服务……");
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // 监听已停时忽略
            }

            foreach (Client client in _clients.Values)
            {
                client.RequestClose("server_stopping");
            }
            Thread.Sleep(300);
            foreach (Client client in _clients.Values)
            {
                client.HardClose();
            }

            _acceptThread.Join(1500);
            _housekeepingThread.Join(1500);
            Log.Info("服务已停止");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
            }
        }

        private static IPAddress ResolveBindAddress(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "*" || text == "any" || text == "0.0.0.0")
            {
                return IPAddress.Any;
            }
            if (text == "localhost")
            {
                return IPAddress.Loopback;
            }
            IPAddress parsed;
            if (IPAddress.TryParse(text, out parsed))
            {
                return parsed;
            }
            throw new ArgumentException("无法解析绑定地址：" + text);
        }

        // ---------------------------------------------------------------- 接入

        private void AcceptLoop()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = _listener.AcceptTcpClient();
                }
                catch (SocketException ex)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    Log.Warn("接受连接失败：" + ex.Message);
                    Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                try
                {
                    AcceptClient(tcp);
                }
                catch (Exception ex)
                {
                    Log.Error("处理新连接出错：" + ex.Message);
                    try
                    {
                        tcp.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void AcceptClient(TcpClient tcp)
        {
            if (_clients.Count >= _options.MaxClients)
            {
                RejectConnection(tcp, "server_full");
                return;
            }

            int seq = Interlocked.Increment(ref _connectionSeq);
            string id = "c" + seq.ToString(CultureInfo.InvariantCulture);
            Client client = new Client(this, tcp, id);
            _clients[id] = client;

            Log.Info("连接建立 " + id + " 来自 " + client.Remote + "，当前连接 " + _clients.Count.ToString(CultureInfo.InvariantCulture));
            client.Send("WELCOME " + id + " " + Banner);
            client.Start();
        }

        private static void RejectConnection(TcpClient tcp, string reason)
        {
            try
            {
                tcp.NoDelay = true;
                using (NetworkStream stream = tcp.GetStream())
                {
                    byte[] payload = Encoding.UTF8.GetBytes("ERR " + reason + "\nBYE " + reason + "\n");
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    tcp.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        // ---------------------------------------------------------------- 巡检

        private void HousekeepingLoop()
        {
            while (!_shutdown.WaitHandle.WaitOne(1000))
            {
                DateTime now = DateTime.UtcNow;
                foreach (Client client in _clients.Values)
                {
                    try
                    {
                        Patrol(client, now);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("巡检 " + client.Id + " 出错：" + ex.Message);
                    }
                }
                SweepExpiredLeases(now);
            }
        }

        private void Patrol(Client client, DateTime now)
        {
            if (!client.IsRegistered)
            {
                if ((now - client.CreatedAt).TotalSeconds >= _options.HelloTimeoutSeconds)
                {
                    Kick(client, "hello_timeout");
                }
                return;
            }

            double idleSeconds = (now - client.LastSeen).TotalSeconds;
            if (idleSeconds >= _options.IdleTimeoutSeconds)
            {
                Kick(client, "idle_timeout");
                return;
            }

            double sinceSend = (now - client.LastSent).TotalSeconds;
            if (idleSeconds >= _options.PingIntervalSeconds && sinceSend >= _options.PingIntervalSeconds)
            {
                client.Send("PING");
            }
        }

        private void SweepExpiredLeases(DateTime now)
        {
            lock (_leaseGate)
            {
                if (_leases.Count == 0)
                {
                    return;
                }
                List<string> expired = new List<string>();
                foreach (KeyValuePair<string, Lease> pair in _leases)
                {
                    if (pair.Value.ExpiresAt <= now)
                    {
                        expired.Add(pair.Key);
                    }
                }
                foreach (string key in expired)
                {
                    _leases.Remove(key);
                    Log.Debug("租约过期 " + key);
                }
            }
        }

        private void Kick(Client client, string reason)
        {
            Log.Info("断开 " + client.Describe() + "：" + reason);
            client.RequestClose(reason);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(500);
                client.HardClose();
            });
        }

        // ---------------------------------------------------------------- 命令分发

        /// <summary>处理一行命令。返回 false 表示这行结束后要断开。</summary>
        public bool HandleLine(Client client, string rawLine)
        {
            if (rawLine == null)
            {
                return false;
            }
            if (rawLine.Length > MaxLineChars)
            {
                client.Send("ERR line_too_long " + rawLine.Length.ToString(CultureInfo.InvariantCulture));
                return false;
            }

            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                return true;
            }

            int space = line.IndexOf(' ');
            string command = space < 0 ? line : line.Substring(0, space);
            string rest = space < 0 ? string.Empty : line.Substring(space + 1).TrimStart();
            command = command.ToUpperInvariant();

            if (_options.Verbose)
            {
                Log.Debug((client.IsRegistered ? client.Name : client.Id) + " > " + line);
            }

            switch (command)
            {
                case "HELLO":
                    CmdHello(client, rest);
                    return true;

                case "QUIT":
                    client.RequestClose("quit");
                    return false;

                case "PING":
                    client.Send("PONG");
                    return true;

                case "PONG":
                    return true;
            }

            if (!client.IsRegistered)
            {
                client.Send("ERR hello_required ");
                return true;
            }

            switch (command)
            {
                case "LIST":
                    client.Send(BuildPeersLine());
                    return true;

                case "WHOAMI":
                    client.Send("OK whoami " + client.Name + " " + client.Role + " " + client.Id + " " + client.Remote);
                    return true;

                case "SAY":
                    CmdSay(client, rest);
                    return true;

                case "LOCK":
                    CmdLock(client, rest);
                    return true;

                case "UNLOCK":
                    CmdUnlock(client, rest);
                    return true;

                case "LOCKS":
                    CmdLocks(client);
                    return true;

                case "SET":
                    CmdSet(client, rest);
                    return true;

                case "GET":
                    CmdGet(client, rest);
                    return true;

                case "DEL":
                    CmdDelete(client, rest);
                    return true;

                case "BOARD":
                    CmdBoard(client);
                    return true;

                default:
                    client.Send("ERR unknown_command " + command);
                    return true;
            }
        }

        // ---------------------------------------------------------------- HELLO / LIST

        private void CmdHello(Client client, string rest)
        {
            if (client.IsRegistered)
            {
                client.Send("ERR already_hello " + client.Name);
                return;
            }

            string name = TakeToken(ref rest);
            string role = TakeToken(ref rest);
            if (role == null || role.Length == 0)
            {
                role = "peer";
            }

            string why;
            if (!IsValidName(name, out why))
            {
                client.Send("ERR bad_name " + why);
                return;
            }
            if (!IsValidName(role, out why))
            {
                client.Send("ERR bad_role " + why);
                return;
            }

            if (!_names.TryAdd(name, client.Id))
            {
                client.Send("ERR name_taken " + name);
                return;
            }

            client.Register(name, role);
            client.Send("OK hello " + name + " " + role);
            client.Send(BuildPeersLine());
            BroadcastPresence("join", client, client);
            Log.Info("成员加入 " + name + "（" + role + "）" + client.Remote + "，在线 " + _names.Count.ToString(CultureInfo.InvariantCulture));
        }

        private string BuildPeersLine()
        {
            List<Client> peers = _clients.Values
                .Where(delegate(Client c) { return c.IsRegistered; })
                .OrderBy(delegate(Client c) { return c.Name; }, StringComparer.OrdinalIgnoreCase)
                .ToList();

            StringBuilder builder = new StringBuilder();
            builder.Append("PEERS ").Append(peers.Count.ToString(CultureInfo.InvariantCulture));
            foreach (Client peer in peers)
            {
                builder.Append(' ').Append(peer.Name).Append(':').Append(peer.Role);
            }
            return builder.ToString();
        }

        private void BroadcastPresence(string kind, Client subject, Client except)
        {
            string line = "PRESENCE " + kind + " " + subject.Name + " " + subject.Role;
            foreach (Client peer in _clients.Values)
            {
                if (peer == except || !peer.IsRegistered)
                {
                    continue;
                }
                peer.Send(line);
            }
        }

        // ---------------------------------------------------------------- SAY

        private void CmdSay(Client client, string rest)
        {
            string target = TakeToken(ref rest);
            string text = rest;

            if (target == null || target.Length == 0 || text.Length == 0)
            {
                client.Send("ERR usage SAY <name|#role|*> <text>");
                return;
            }
            if (text.Length > MaxMessageChars)
            {
                client.Send("ERR message_too_long " + text.Length.ToString(CultureInfo.InvariantCulture));
                return;
            }

            string line = "MSG " + client.Name + " " + client.Role + " " + target + " " + text;
            int delivered = 0;

            if (target == "*")
            {
                foreach (Client peer in _clients.Values)
                {
                    if (peer == client || !peer.IsRegistered)
                    {
                        continue;
                    }
                    if (peer.Send(line))
                    {
                        delivered++;
                    }
                }
            }
            else if (target[0] == '#')
            {
                string role = target.Substring(1);
                if (role.Length == 0)
                {
                    client.Send("ERR no_recipients role_empty");
                    return;
                }
                foreach (Client peer in _clients.Values)
                {
                    if (peer == client || !peer.IsRegistered)
                    {
                        continue;
                    }
                    if (string.Equals(peer.Role, role, StringComparison.OrdinalIgnoreCase) && peer.Send(line))
                    {
                        delivered++;
                    }
                }
            }
            else
            {
                string peerId;
                if (!_names.TryGetValue(target, out peerId))
                {
                    client.Send("ERR unknown_peer " + target);
                    return;
                }

                Client peer;
                if (!_clients.TryGetValue(peerId, out peer) || !peer.Send(line))
                {
                    client.Send("ERR unknown_peer " + target);
                    return;
                }

                delivered = 1;
            }

            client.Send("OK say " + delivered.ToString(CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------- 租约锁

        private void CmdLock(Client client, string rest)
        {
            string key = TakeToken(ref rest);
            string ttlText = TakeToken(ref rest);

            string why;
            if (!IsValidKey(key, out why))
            {
                client.Send("ERR bad_key " + why);
                return;
            }

            int ttl = 60;
            if (ttlText != null && ttlText.Length > 0)
            {
                if (!int.TryParse(ttlText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ttl) || ttl < 5 || ttl > 3600)
                {
                    client.Send("ERR bad_ttl 需要 5~3600 秒，收到 " + ttlText);
                    return;
                }
            }

            DateTime now = DateTime.UtcNow;
            lock (_leaseGate)
            {
                Lease existing;
                if (_leases.TryGetValue(key, out existing)
                    && existing.ExpiresAt > now
                    && !string.Equals(existing.Owner, client.Name, StringComparison.Ordinal))
                {
                    client.Send("ERR locked " + key + " " + existing.Owner + " " + existing.RemainingSeconds(now).ToString(CultureInfo.InvariantCulture));
                    return;
                }

                bool renewed = existing != null && string.Equals(existing.Owner, client.Name, StringComparison.Ordinal);
                _leases[key] = new Lease(key, client.Name, client.Id, now.AddSeconds(ttl));
                client.Send("OK lock " + key + " " + ttl.ToString(CultureInfo.InvariantCulture) + (renewed ? " renewed" : ""));
            }
        }

        private void CmdUnlock(Client client, string rest)
        {
            string key = TakeToken(ref rest);
            string why;
            if (!IsValidKey(key, out why))
            {
                client.Send("ERR bad_key " + why);
                return;
            }

            DateTime now = DateTime.UtcNow;
            lock (_leaseGate)
            {
                Lease existing;
                if (!_leases.TryGetValue(key, out existing))
                {
                    client.Send("ERR not_locked " + key);
                    return;
                }
                if (existing.ExpiresAt > now && !string.Equals(existing.Owner, client.Name, StringComparison.Ordinal))
                {
                    client.Send("ERR not_owner " + key + " " + existing.Owner);
                    return;
                }
                _leases.Remove(key);
            }
            client.Send("OK unlock " + key);
        }

        private void CmdLocks(Client client)
        {
            DateTime now = DateTime.UtcNow;
            lock (_leaseGate)
            {
                List<Lease> active = _leases.Values
                    .Where(delegate(Lease lease) { return lease.ExpiresAt > now; })
                    .OrderBy(delegate(Lease lease) { return lease.Key; }, StringComparer.Ordinal)
                    .ToList();

                client.Send("LOCKS " + active.Count.ToString(CultureInfo.InvariantCulture));
                foreach (Lease lease in active)
                {
                    client.Send("LK " + lease.Key + " " + lease.Owner + " " + lease.RemainingSeconds(now).ToString(CultureInfo.InvariantCulture));
                }
                client.Send("END");
            }
        }

        // ---------------------------------------------------------------- 共享值

        private void CmdSet(Client client, string rest)
        {
            string key = TakeToken(ref rest);
            string value = rest;

            string why;
            if (!IsValidKey(key, out why))
            {
                client.Send("ERR bad_key " + why);
                return;
            }
            if (value.Length == 0)
            {
                client.Send("ERR usage SET <key> <value>");
                return;
            }
            if (value.Length > MaxValueChars)
            {
                client.Send("ERR value_too_long " + value.Length.ToString(CultureInfo.InvariantCulture));
                return;
            }

            _board[key] = value;
            client.Send("OK set " + key);
        }

        private void CmdGet(Client client, string rest)
        {
            string key = TakeToken(ref rest);
            string why;
            if (!IsValidKey(key, out why))
            {
                client.Send("ERR bad_key " + why);
                return;
            }

            string value;
            if (!_board.TryGetValue(key, out value))
            {
                client.Send("ERR not_found " + key);
                return;
            }
            client.Send("VALUE " + key + " " + value);
        }

        private void CmdDelete(Client client, string rest)
        {
            string key = TakeToken(ref rest);
            string why;
            if (!IsValidKey(key, out why))
            {
                client.Send("ERR bad_key " + why);
                return;
            }

            string removed;
            if (!_board.TryRemove(key, out removed))
            {
                client.Send("ERR not_found " + key);
                return;
            }
            client.Send("OK del " + key);
        }

        private void CmdBoard(Client client)
        {
            List<KeyValuePair<string, string>> items = _board
                .OrderBy(delegate(KeyValuePair<string, string> pair) { return pair.Key; }, StringComparer.Ordinal)
                .ToList();

            client.Send("BOARD " + items.Count.ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, string> item in items)
            {
                client.Send("KV " + item.Key + " " + item.Value);
            }
            client.Send("END");
        }

        // ---------------------------------------------------------------- 断开收尾

        public void OnClientGone(Client client, string reason)
        {
            Client removed;
            if (!_clients.TryRemove(client.Id, out removed))
            {
                return;
            }

            // 先把队列排空再关 socket：由写线程负责关，2 秒后无论如何强制关闭兜底
            client.BeginClose();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(2000);
                client.HardClose();
            });

            string name = client.Name;
            if (name == null)
            {
                Log.Info("连接结束 " + client.Id + "（未登记）：" + reason);
                return;
            }

            string ownerId;
            if (_names.TryGetValue(name, out ownerId) && ownerId == client.Id)
            {
                string ignored;
                _names.TryRemove(name, out ignored);
            }

            int released = ReleaseLeasesOf(name);
            BroadcastPresence("leave", client, client);
            Log.Info("成员离开 " + name + "（" + client.Role + "）：" + reason
                + (released > 0 ? "，释放租约 " + released.ToString(CultureInfo.InvariantCulture) + " 个" : string.Empty)
                + "，在线 " + _names.Count.ToString(CultureInfo.InvariantCulture));
        }

        private int ReleaseLeasesOf(string owner)
        {
            lock (_leaseGate)
            {
                if (_leases.Count == 0)
                {
                    return 0;
                }
                List<string> keys = new List<string>();
                foreach (KeyValuePair<string, Lease> pair in _leases)
                {
                    if (string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal))
                    {
                        keys.Add(pair.Key);
                    }
                }
                foreach (string key in keys)
                {
                    _leases.Remove(key);
                }
                return keys.Count;
            }
        }

        // ---------------------------------------------------------------- 小工具

        /// <summary>从行里取下一个以空格分隔的词，剩余的文本写回 text。</summary>
        private static string TakeToken(ref string text)
        {
            if (text == null)
            {
                return null;
            }
            text = text.TrimStart();
            if (text.Length == 0)
            {
                return null;
            }

            int space = text.IndexOf(' ');
            if (space < 0)
            {
                string whole = text;
                text = string.Empty;
                return whole;
            }

            string token = text.Substring(0, space);
            text = text.Substring(space + 1).TrimStart();
            return token;
        }

        private static bool IsValidName(string text, out string why)
        {
            if (string.IsNullOrEmpty(text))
            {
                why = "名字为空";
                return false;
            }
            if (text.Length > 32)
            {
                why = "名字超过 32 个字符";
                return false;
            }
            foreach (char ch in text)
            {
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                          || ch == '_' || ch == '-' || ch == '.';
                if (!ok)
                {
                    why = "只能用字母、数字、下划线、减号、点";
                    return false;
                }
            }
            why = null;
            return true;
        }

        private static bool IsValidKey(string text, out string why)
        {
            if (string.IsNullOrEmpty(text))
            {
                why = "键为空";
                return false;
            }
            if (text.Length > 64)
            {
                why = "键超过 64 个字符";
                return false;
            }
            foreach (char ch in text)
            {
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                          || ch == '_' || ch == '-' || ch == '.' || ch == ':';
                if (!ok)
                {
                    why = "只能用字母、数字、下划线、减号、点、冒号";
                    return false;
                }
            }
            why = null;
            return true;
        }
    }

    // ------------------------------------------------------------------------
    //  单个客户端连接
    // ------------------------------------------------------------------------
    internal sealed class Client
    {
        private const int OutboxCapacity = 512;
        private const int SendTimeoutMilliseconds = 10000;

        private readonly Coordinator _coordinator;
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly BlockingCollection<string> _outbox;
        private readonly Thread _readThread;
        private readonly Thread _writeThread;
        private readonly DateTime _createdAt;
        private long _lastSeenTicks;
        private long _lastSentTicks;
        private int _closing;
        private int _transportClosed;
        private volatile string _name;
        private volatile string _role;

        public Client(Coordinator coordinator, TcpClient tcp, string id)
        {
            _coordinator = coordinator;
            _tcp = tcp;
            Id = id;

            _tcp.NoDelay = true;
            try
            {
                _tcp.SendTimeout = SendTimeoutMilliseconds;
            }
            catch (Exception)
            {
            }

            _stream = tcp.GetStream();
            _reader = new StreamReader(_stream, new UTF8Encoding(false), false, 4096);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false), 4096);
            _writer.NewLine = "\n";
            _writer.AutoFlush = false;

            _outbox = new BlockingCollection<string>(OutboxCapacity);
            _createdAt = DateTime.UtcNow;

            long now = _createdAt.Ticks;
            Interlocked.Exchange(ref _lastSeenTicks, now);
            Interlocked.Exchange(ref _lastSentTicks, now);

            Remote = SafeRemote(tcp);

            _readThread = new Thread(ReadLoop);
            _readThread.IsBackground = true;
            _readThread.Name = "rx-" + id;

            _writeThread = new Thread(WriteLoop);
            _writeThread.IsBackground = true;
            _writeThread.Name = "tx-" + id;
        }

        public string Id { get; private set; }
        public string Name { get { return _name; } }
        public string Role { get { return _role; } }
        public string Remote { get; private set; }
        public DateTime CreatedAt { get { return _createdAt; } }

        public DateTime LastSeen
        {
            get { return new DateTime(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc); }
        }

        public DateTime LastSent
        {
            get { return new DateTime(Interlocked.Read(ref _lastSentTicks), DateTimeKind.Utc); }
        }

        public bool IsRegistered
        {
            get { return _name != null; }
        }

        public void Register(string name, string role)
        {
            _role = role;
            _name = name;
        }

        public string Describe()
        {
            if (_name == null)
            {
                return Id;
            }
            return _name + "(" + Id + ")";
        }

        public void Start()
        {
            _writeThread.Start();
            _readThread.Start();
        }

        // ------------------------------------------------------------ 收发

        public bool Send(string line)
        {
            if (line == null)
            {
                return false;
            }
            try
            {
                if (_outbox.IsAddingCompleted)
                {
                    return false;
                }
                return _outbox.TryAdd(line);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReadLoop()
        {
            string reason = "disconnected";
            try
            {
                while (true)
                {
                    string line = _reader.ReadLine();
                    if (line == null)
                    {
                        reason = "eof";
                        break;
                    }

                    Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);

                    if (Volatile.Read(ref _closing) != 0)
                    {
                        reason = "closing";
                        break;
                    }

                    if (!_coordinator.HandleLine(this, line))
                    {
                        reason = "client_quit";
                        break;
                    }
                }
            }
            catch (IOException)
            {
                reason = Volatile.Read(ref _closing) != 0 ? "closing" : "io_error";
            }
            catch (ObjectDisposedException)
            {
                reason = "closed";
            }
            catch (Exception ex)
            {
                reason = "error:" + ex.GetType().Name;
            }
            finally
            {
                _coordinator.OnClientGone(this, reason);
            }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (string line in _outbox.GetConsumingEnumerable())
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                    Interlocked.Exchange(ref _lastSentTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch (Exception)
            {
                // 对端已断开，交给读线程做收尾
            }
            finally
            {
                HardClose();
            }
        }

        // ------------------------------------------------------------ 关闭

        /// <summary>礼貌关闭：先把 BYE 排进发送队列，再由写线程排空后收尾。</summary>
        public void RequestClose(string reason)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
            {
                return;
            }
            if (!string.IsNullOrEmpty(reason))
            {
                Send("BYE " + reason);
            }
            try
            {
                _outbox.CompleteAdding();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 只停止接收新消息，由写线程把队列排空后自行关闭套接字，
        /// 这样最后一条 BYE 不会因为立即关连接而被吞掉。
        /// </summary>
        public void BeginClose()
        {
            try
            {
                _outbox.CompleteAdding();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>立即关闭套接字，可重复调用。</summary>
        public void HardClose()
        {
            if (Interlocked.Exchange(ref _transportClosed, 1) != 0)
            {
                return;
            }
            try
            {
                _outbox.CompleteAdding();
            }
            catch (Exception)
            {
            }
            try
            {
                if (_tcp.Client != null)
                {
                    _tcp.Client.Shutdown(SocketShutdown.Both);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                _reader.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                _writer.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                _tcp.Close();
            }
            catch (Exception)
            {
            }
        }

        private static string SafeRemote(TcpClient tcp)
        {
            try
            {
                if (tcp.Client != null && tcp.Client.RemoteEndPoint != null)
                {
                    return tcp.Client.RemoteEndPoint.ToString();
                }
            }
            catch (Exception)
            {
            }
            return "?";
        }
    }

    // ------------------------------------------------------------------------
    //  交互式客户端（--connect 模式）
    // ------------------------------------------------------------------------
    internal static class InteractiveClient
    {
        private static volatile bool _closed;

        public static int Run(string target, string name, string role)
        {
            if (string.IsNullOrEmpty(name))
            {
                Console.Error.WriteLine("客户端模式必须用 --name 指定名字");
                return 2;
            }

            string host;
            int port;
            if (!TryParseEndpoint(target, out host, out port))
            {
                Console.Error.WriteLine("无法解析地址：" + target + "（应形如 127.0.0.1:9000）");
                return 2;
            }

            TcpClient tcp = new TcpClient();
            try
            {
                tcp.Connect(host, port);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("连接 " + host + ":" + port + " 失败：" + ex.Message);
                return 1;
            }

            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();
            StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096);
            StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096);
            writer.NewLine = "\n";
            writer.AutoFlush = true;

            Thread receiver = new Thread(delegate()
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        Console.WriteLine("<- " + line);
                    }
                }
                catch (Exception)
                {
                }
                _closed = true;
                Console.WriteLine("连接已断开，按回车退出。");
            });
            receiver.IsBackground = true;
            receiver.Name = "client-rx";
            receiver.Start();

            writer.WriteLine("HELLO " + name + " " + role);
            Console.WriteLine("已连接 " + host + ":" + port + "，输入协议命令（QUIT 退出）。");

            while (!_closed)
            {
                string input;
                try
                {
                    input = Console.ReadLine();
                }
                catch (Exception)
                {
                    break;
                }
                if (input == null)
                {
                    break;
                }
                input = input.Trim();
                if (input.Length == 0)
                {
                    continue;
                }

                try
                {
                    writer.WriteLine(input);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("发送失败：" + ex.Message);
                    break;
                }

                if (string.Equals(input, "QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            Thread.Sleep(200);
            try
            {
                tcp.Close();
            }
            catch (Exception)
            {
            }
            return 0;
        }

        private static bool TryParseEndpoint(string text, out string host, out int port)
        {
            host = null;
            port = 9000;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int colon = text.LastIndexOf(':');
            if (colon < 0)
            {
                host = text;
                return host.Length > 0;
            }

            host = text.Substring(0, colon);
            string portText = text.Substring(colon + 1);
            if (host.Length == 0)
            {
                host = "127.0.0.1";
            }
            return int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0 && port <= 65535;
        }
    }
}
