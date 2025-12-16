using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleReverseTunnel
{
    public class RpaServer
    {
        private readonly int _bridgePort;
        private readonly int _publicPort;
        private readonly string _password;

        private TcpListener _bridgeListener;
        private TcpListener _publicListener;
        
        // 控制连接：同一时间只允许一个活动代理
        private SecureSocket? _controlSocket;
        private readonly object _controlLock = new();

        // 挂起的连接：ID -> Socket (来自桥接的数据连接)
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SecureSocket>> _pendingConnections = new();

        public RpaServer(int bridgePort, int publicPort, string password)
        {
            _bridgePort = bridgePort;
            _publicPort = publicPort;
            _password = password;

            _bridgeListener = new TcpListener(IPAddress.Any, _bridgePort);
            _publicListener = new TcpListener(IPAddress.Any, _publicPort);
        }

        public async Task RunAsync()
        {
            Logger.Info("服务端启动...");
            Logger.Info($"桥接端口: {_bridgePort}");
            Logger.Info($"公共端口: {_publicPort}");
            // Logger.Info($"Password: {_password}"); // 不要记录密码

            _bridgeListener.Start();
            _publicListener.Start();

            var bridgeTask = AcceptBridgeConnectionsAsync();
            var publicTask = AcceptPublicConnectionsAsync();

            Logger.Info("服务已就绪，等待连接...");
            await Task.WhenAll(bridgeTask, publicTask);
            Logger.Info("服务意外停止");
        }

        private async Task AcceptBridgeConnectionsAsync()
        {
            Logger.Info("监听桥接连接...");
            while (true)
            {
                try
                {
                    var socket = await _bridgeListener.AcceptSocketAsync();
                    _ = HandleBridgeHandshakeAsync(socket);
                }
                catch (Exception ex)
                {
                    Logger.Error($"桥接监听异常: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        private async Task HandleBridgeHandshakeAsync(Socket rawSocket)
        {
            var socket = new SecureSocket(rawSocket, _password);
            try
            {
                // 设置握手超时
                using var cts = new CancellationTokenSource(5000);

                var (success, type, connId) = await NetworkHelper.ReceiveHandshakeAsync(socket);
                
                if (!success)
                {
                    Logger.Warn($"认证失败: {socket.RemoteEndPoint}");
                    socket.Dispose();
                    return;
                }
                
                Logger.Info($"握手成功: {type} {connId}");

                if (type == NetworkHelper.ConnectionType.Control)
                {
                    RegisterControlSocket(socket);
                    // 启动心跳
                    _ = SendHeartbeatsAsync(socket);
                    // 监控连接
                    await MonitorControlConnectionAsync(socket);
                }
                else if (type == NetworkHelper.ConnectionType.Data)
                {
                    if (_pendingConnections.TryGetValue(connId, out var tcs))
                    {
                        tcs.TrySetResult(socket);
                    }
                    else
                    {
                        Logger.Warn($"无效数据连接ID: {connId}");
                        socket.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"握手异常: {ex.Message}");
                socket.Dispose();
            }
        }

        private async Task SendHeartbeatsAsync(SecureSocket socket)
        {
            try
            {
                byte[] heartbeat = new byte[17]; // 命令(1) + 填充(16)
                heartbeat[0] = 0x00;
                
                while (socket.Connected)
                {
                    await Task.Delay(5000);
                    await SendControlCommandAsync(socket, heartbeat);
                }
            }
            catch
            {
                // 心跳失败，可能已断开连接
            }
        }

        private async Task MonitorControlConnectionAsync(SecureSocket socket)
        {
            try
            {
                byte[] buffer = new byte[1024];
                while (true)
                {
                    int read = await socket.ReceiveAsync(buffer);
                    if (read == 0)
                    {
                        Logger.Info("客户端控制连接已断开");
                        break;
                    }
                    else
                    {
                         Logger.Warn($"控制连接收到异常数据 ({read} bytes)，已忽略");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"控制连接异常: {ex.Message}");
            }
            finally
            {
                CleanupControlSocket(socket);
            }
        }

        private void CleanupControlSocket(SecureSocket socket)
        {
            lock (_controlLock)
            {
                if (_controlSocket == socket)
                {
                    _controlSocket = null;
                    Logger.Info("控制连接已清理");
                }
            }
            socket.Dispose();
        }

        private void RegisterControlSocket(SecureSocket socket)
        {
            lock (_controlLock)
            {
                if (_controlSocket != null)
                {
                    Logger.Info("替换旧的控制连接");
                    _controlSocket.Dispose();
                }
                _controlSocket = socket;
                Logger.Info($"控制连接已注册: {socket.RemoteEndPoint}");
            }
        }

        private async Task AcceptPublicConnectionsAsync()
        {
            Logger.Info("监听公共连接...");
            while (true)
            {
                try
                {
                    var userSocket = await _publicListener.AcceptSocketAsync();
                    _ = HandleUserConnectionAsync(userSocket);
                }
                catch (Exception ex)
                {
                    Logger.Error($"公共端口监听异常: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        private async Task HandleUserConnectionAsync(Socket userSocket)
        {
            SecureSocket? control = null;
            lock (_controlLock)
            {
                if (_controlSocket != null && _controlSocket.Connected)
                {
                    control = _controlSocket;
                }
            }

            if (control == null)
            {
                // Logger.Warn("没有活动的代理连接，拒绝用户请求。");
                NetworkHelper.CleanupSocket(userSocket);
                return;
            }

            Guid connId = Guid.NewGuid();
            // Logger.Info($"新用户请求 {connId}");

            var tcs = new TaskCompletionSource<SecureSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingConnections[connId] = tcs;

            try
            {
                // 发送请求给客户端
                // 协议: [命令(1)] [连接ID(16)]
                byte[] cmd = new byte[17];
                cmd[0] = 0x01; // RequestConnect
                connId.TryWriteBytes(cmd.AsSpan(1));
                
                await SendControlCommandAsync(control, cmd);

                // 等待数据连接
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                
                if (completedTask == tcs.Task)
                {
                    SecureSocket bridgeDataSocket = await tcs.Task;
                    await NetworkHelper.ForwardAsync(userSocket, bridgeDataSocket);
                }
                else
                {
                    // 超时
                    // Logger.Warn($"等待桥接数据超时 {connId}");
                    NetworkHelper.CleanupSocket(userSocket);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"用户连接处理失败 {connId}: {ex.Message}");
                NetworkHelper.CleanupSocket(userSocket);
            }
            finally
            {
                _pendingConnections.TryRemove(connId, out _);
            }
        }

        private readonly SemaphoreSlim _controlSendLock = new(1, 1);
        private async Task SendControlCommandAsync(SecureSocket socket, byte[] data)
        {
            await _controlSendLock.WaitAsync();
            try
            {
                await socket.SendAsync(data);
            }
            finally
            {
                _controlSendLock.Release();
            }
        }
    }
}
