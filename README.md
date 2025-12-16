# SimpleReverseTunnel

## 使用说明

### 1. 编译
确保安装了 .NET SDK 10.0 (或降级至 8.0+)。

```bash
dotnet build -c Release
```

### 2. 服务端 (公网机器)
启动服务端，监听两个端口：
- **Bridge Port**: 供内网客户端连接
- **Public Port**: 供外部用户访问（普通 TCP）

```bash
# 格式: SimpleReverseTunnel.exe server <bridge_port> <public_port> <password>
dotnet run -- server 9000 9001 MySecret
```

### 3. 客户端 (内网机器)
启动客户端，连接服务端并将流量转发到本地目标服务。

```bash
# 格式: SimpleReverseTunnel.exe client <server_ip> <bridge_port> <target_ip> <target_port> <password>
dotnet run -- client 1.2.3.4 9000 127.0.0.1 80 MySecret
```

### 4. 访问
访问公网机器的 Public Port (9001)，流量将被自动解密并转发到内网机器的 Target Port (80)。

```bash
curl http://public-server-ip:9001
```
