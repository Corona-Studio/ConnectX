## ConnectX 服务端部署

### 前置条件

+ 一台装有 `Visual Studio`，且安装了 `.NET 桌面开发` 工作负荷，用于编译 `ConnectX.Server` 以及 `ConnectX.Relay` 的可执行文件
+ 一台拥有公网的服务器，用于部署 `ConnectX.Server` 以及 `ConnectX.Relay` 服务

> 服务器系统推荐使用 Ubuntu 22.04 或者是 Windows Server，因为后续需要安装 .NET 9 运行时（本人在 Ubuntu 24.04 上安装 .NET 9 没成功..）  

### 编译可执行文件

1. Clone 本项目仓库到本地

2. 使用 Visual Studio 打开本项目

3. 编辑项目文件（可选）

如果你不想为 `ConnectX.Server`  另外单独配置 Sql 数据库服务，你可以将 `Program.cs` 文件中的以下几行代码
``` C#
services.AddDbContext<RoomOpsHistoryContext>(o =>
    o.UseSqlServer(connectionString, b => b.MigrationsAssembly("ConnectX.Server")));
```
替换为
``` C#
services.AddDbContext<RoomOpsHistoryContext>(o =>
    o.UseSqlite(connectionString, b => b.MigrationsAssembly("ConnectX.Server")));
```
它会让你的 `ConnectX.Server` 运行时使用 Sqlite 本地数据库

4. 发布项目 `ConnectX.Server` 以及 `ConnectX.Relay`

请根据你的服务器系统选择目标平台是 `linux-x64` 还是 `win-x64` 或者其他，部署模式选择 `依赖框架` ，文件发布选项启用 `生成单个文件`

> [!IMPORTANT] 
> 现阶段请不要为 `ConnectX.Server` 以及 `ConnectX.Relay` 启用 `PublishAOT` ，这可能会导致运行不正常

### 部署 Zerotier 服务

`ZeroTier:Enabled` 默认为 `true`（省略该项也保留旧行为），此模式需要 `Zerotier 控制器`。

仅使用 TCP Relay 时，设置 `ZeroTier:Enabled` 为 `false`，可以跳过本节，不需要安装 ZeroTier，也不需要填写 `EndPoint` / `Token`。服务端不注册 ZeroTier API、节点检查或 Peer 轮询服务；客户端应创建 Relay 房间。请求创建直连房间会收到 `NetworkControllerNotReady` 和明确的禁用说明。Relay 房间退出/销毁不会访问控制器；无可用 Relay 时建房会返回错误。

后续启用 ZeroTier 时，恢复 `Enabled: true` 并配置控制器地址和 Token，然后重启服务。

1. 安装 Zerotier

前往 [Download Zerotier](https://www.zerotier.com/download/)，选择适合你的服务器系统的版本安装

2. 配置 Zerotier

设置 Zerotier 开机启动，如果你是 linux ，请输入 `systemctl enable zerotier-one` ，Windows 安装完后默认可以在任务管理器里面看到开机启动  

验证 Zerotier 是否启动成功，在终端中输入 `zerotier-cli info` ，  
如果出现 `200 info 495b749d3c 1.14.2 TUNNELED` 或者是 `200 info 495b749d3c 1.14.2 ONLINE` 的字样则说明成功

3. 获取 Zerotier 服务信息

linux 下 Zerotier 的数据文件夹在 `/var/lib/zerotier-one` 目录下，  
Windows 下 Zerotier 的数据文件夹在 `C:\ProgramData\ZeroTier\One` 目录下

其中 `authtoken.secret` 储存的是 Zerotier 控制器的访问 Token ，请记下它  
`zerotier-one.port` 储存的是 Zerotier 控制器的访问访问端口，默认为 `9993` ，请记下它

4. 部署 Zerotier Moon 卫星级服务器 （可选）

请参考这篇文章：https://www.wnark.com/archives/152.html  

> [!IMPORTANT] 
> 暂不清楚部署 Moon 卫星级服务器到底能不能对 P2P 连接起到正向作用，需要验证

5. 放行 Zerotier 控制器端口 (Tcp/Udp)

### 部署 `ConnectX.Server` 以及 `ConnectX.Relay` 服务

1. 将先前发布的可执行文件及其依赖 Dll 上传到服务器，连同发布目录下的 `appsettings.json` 一起上传

2. 配置 `ConnectX.Server`

修改 `Server:ListenAddress` 为 `0.0.0.0`
修改 `Server:ServerId` 为一个 Guid 字符串，例 `ddad03b9-d122-421d-add7-1d09e65b4295`
修改 `ZeroTier:EndPoint` 为 `http://127.0.0.1:{port}` ，其中 `{port}` 为之前你记下的 Zerotier 控制器服务的端口  
修改 `ZeroTier:Token` 为之前你记下的 Zerotier 控制器服务的访问 Token  
修改 `ConnectionStrings:Default` 为你的 Sql 数据库，如果你在前面修改代码使用了 Sqlite ，请将该项写入为 `Data Source=Sqlite.db` ，运行后会自动生成 `Sqlite.db` 文件作为数据库

3. 配置 `ConnectX.Relay`

修改 `RelayServer:ListenAddress` 为 `0.0.0.0`  
修改 `RelayServer:PublicListenAddress` 为运行 `ConnectX.Relay` 的服务器的公网地址  
修改 `Server:ListenPort` 为上面 `ConnectX.Server` 的监听端口，默认为 `3535`  
修改 `Server:ServerId` 为上面 `ConnectX.Server` 的 `ServerId`  
如果你将 `ConnectX.Relay` 和 `ConnectX.Server` 放在一个服务器上运行，则保留原有配置 `127.0.0.1` ，否则请修改 `Server:ListenAddress` 为上面 `ConnectX.Server` 的公网地址，以便访问

> [!IMPORTANT] 
> `ConnectX.Relay` 可以有多个部署在不同的服务器上，连接到同一个 `ConnectX.Server`

4. 放行 `ConnectX.Server` 以及 `ConnectX.Relay` 的端口 (Tcp/Udp)，默认分别为 `3535` 和 `3536`

5. 运行 `ConnectX.Server` 以及 `ConnectX.Relay`

若 `ConnectX.Server` 输出中出现  
``` log
[25-03-16 11:48:56 INF]: [ZTNodeService] Fetching ZT server node status...
[25-03-16 11:48:56 INF]: [ZTNodeService] Node status received, ID [495b749d3c] Version [1.14.2]
[25-03-16 11:48:57 INF]: [CLIENT_MANAGER] Watchdog started.
```
则 `ConnectX.Server` 成功连接到 Zerotier 控制器

若 `ConnectX.Relay` 输出中出现  
``` log
[25-03-16 11:49:03 INF]: [CLIENT] Sending signin message to server...
[25-03-16 11:49:04 INF]: [CLIENT] Connected and signed to server at endpoint 127.0.0.1:3535
[25-03-16 11:49:04 INF]: [CLIENT] Server public address acquired [xxx.xx.xx.xx:3536]
[25-03-16 11:49:04 INF]: [CLIENT] Successfully registered relay server.
```
则 `ConnectX.Relay` 服务成功连接到 `ConnectX.Server` 服务

### 至此完成

`ConnectX.Server` 以及 `ConnectX.Relay` 的守护进程等配置在此不再过多赘述  

使用 `ConnectX.Client` 连接时请使用 `ConnectX.Server` 的公网地址和端口进行连接

### Relay-only 部署验收

先使用 `ZeroTier:Enabled=false` 启动 Server，但不启动 Relay，执行以下测试，验证登录成功、直连建房被拒绝、Relay 缺席时建房被拒绝：

```sh
dotnet run --project tools/DeploymentProbe -- 127.0.0.1 <Relay公网IP> --no-relay
```

启动 Relay 并确认成功注册后，移除 `--no-relay` 再执行，验证两个客户端登录、Relay 房间创建/加入、广播的 Relay 地址、TCP 数据转发和退出/销毁。也可以从另一台机器将第一个参数换成 Server 公网 IP，验证公网端口可达。测试会创建一个私有临时房间，成功后自动退出；异常退出后由服务端清理断开的会话。

服务端入口域名需解析到 Server 的公网 IPv4，LauncherX 默认使用 `connectx.corona.studio:3535`；Relay 广播自身的公网 IPv4 和 `3536` 端口。仅 Relay 模式需要放行 TCP 3535/3536。Cloudflare 常规橙云代理不支持该自定义 TCP 协议，需使用 DNS only，或另行配置支持自定义 TCP 的 Spectrum。
