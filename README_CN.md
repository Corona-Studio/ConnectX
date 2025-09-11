# ConnectX

[English](./README.md) | 中文

![CodeFactor Grade](https://img.shields.io/codefactor/grade/github/corona-studio/connectx?logo=codefactor&style=for-the-badge)
![GitHub](https://img.shields.io/github/license/corona-studio/connectx?logo=github&style=for-the-badge)
![Maintenance](https://img.shields.io/maintenance/yes/2025?logo=diaspora&style=for-the-badge)
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/Corona-Studio/connectx?style=for-the-badge)
![GitHub closed pull requests](https://img.shields.io/github/issues-pr-closed/corona-studio/connectx?logo=github&style=for-the-badge)
![GitHub repo size](https://img.shields.io/github/repo-size/corona-studio/connectx?logo=github&style=for-the-badge)
![DotNet Version](https://img.shields.io/badge/.NET-9-blue?style=for-the-badge)
![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/Corona-Studio/ConnectX/codeql.yml?style=for-the-badge&logo=github&label=CodeQL%20Advanced)

一个跨平台的 Minecraft P2P 在线多人库，支持异地跨网联机，采用 C# 开发，使用高性能套接字实现出色的转发性能，由 Zerotier SDK 实现 P2P 功能。

由我们的另一个开源项目提供支持： [Hive.Framework](https://github.com/Corona-Studio/Hive.Framework)

![Demo Screenshot](https://github.com/user-attachments/assets/893ffc13-92b2-4700-bca1-6b8e5151efa8)

## 架构图

![ConnectX cn drawio](https://github.com/user-attachments/assets/f8dc7829-5dd1-48c1-bbab-fa8df30fbbda#gh-light-mode-only)
![ConnectX cn dark drawio](https://github.com/user-attachments/assets/d584f885-d671-4d43-bab7-f49ec1d14868#gh-dark-mode-only)

## 项目状态

| 功能                                                 |        状态        |
| :--------------------------------------------------- | :----------------: |
| `服务端`: 将房间相关操作记录到本地数据库（合规用途） | :white_check_mark: |
| `服务端`: 客户端/房间管理                            | :white_check_mark: |
| `服务端`: 中继服务器管理                             | :white_check_mark: |
| `中继`: 通过 Hive 实现的中继服务器                   | :white_check_mark: |
| `客户端`: 基于 ZT 的 P2P 联机实现                    | :white_check_mark: |
| `客户端`: 基于 ZT 的中继联机实现                     | :white_check_mark: |
| `客户端`: 基于 ConnectX 中继服务器的联机实现         | :white_check_mark: |

## 如何部署？

[快速部署教程](https://github.com/Corona-Studio/ConnectX/blob/main/deploy.md)

## 快速开始！

我们使用 MSDI（`Microsoft.Extensions.DependencyInjection`）作为 DI 容器。最佳做法是在程序中使用 `.NET Generic Host`。

首先，为服务器配置添加以下方法，`ConnectXServerIp` 将是 ConnectX.Server 的后台地址。

```c#
private static IClientSettingProvider GetConnectXSettings()
{
    var serverIp = IPAddress.None;

    try
    {
        var ips = Dns.GetHostAddresses(ConnectXServerIp);
        var ipv4Addresses = ips
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .Where(ip => !ip.IsLocalIpAddress())
            .ToArray();

        if (ipv4Addresses.Length > 0)
            serverIp = ipv4Addresses[0];
    }
    catch (Exception ex)
    {
        Log.Logger.Error(ex, "Failed to resolve ConnectX server IP.");
    }

    return new DefaultClientSettingProvider
    {
        ServerAddress = serverIp,
        ServerPort = ConnectXServerPort,
        JoinP2PNetwork = true
    };
}
```

然后，只需要再添加一行即可完成配置！

```diff
private static void ConfigureServices(IServiceCollection services)
{
    // ...
+   services.UseConnectX(GetConnectXSettings);
    // ...
}
```

## 如何使用？

将 `IServerLinkHolder` 和 `Client` 注入要管理房间实例的 ViewModel 当中。

### 连接到服务器

```c#
await _serverLinkHolder.ConnectAsync(CancellationToken.None);
```

### 执行任何房间相关操作

> [!IMPORTANT]  
> 请确保在执行任何房间操作前您已经成功和服务器建立了连接，否则可能会出现预期之外的结果！
>
> ```c#
> await TaskHelper.WaitUntilAsync(() => _serverLinkHolder is { IsConnected: true, IsSignedIn: true });
> ```

```c#
var message = new CreateGroup
{
    UserId = _serverLinkHolder.UserId,
    RoomName = createRoomRecord.RoomName,
    RoomDescription = createRoomRecord.RoomDescription,
    RoomPassword = createRoomRecord.RoomPassword,
    IsPrivate = createRoomRecord.IsPrivateRoom,
    MaxUserCount = 3
};

var (groupInfo, status, err) = await _multiPlayerClient.CreateGroupAsync(message, CancellationToken.None);

if (groupInfo == null || status != GroupCreationStatus.Succeeded || !string.IsNullOrEmpty(err))
{
    // Error process
    return;
}

_multiPlayerClient.OnGroupStateChanged += MultiPlayerClientOnGroupStateChanged;

// Other actions
```

## 开源协议

MIT。这意味着您可以出于任何目的修改或使用我们的代码，但您的软件的所有副本或重要部分都应包含版权声明和许可声明。

## 统计信息

![Alt](https://repobeats.axiom.co/api/embed/6087c9625a31a996d4aa921483f8b10ea00853d5.svg "Repobeats analytics image")

## 免责声明

ConnectX 与 Mojang 或其软件的任何部分均无关联。

## 许可声明

本项目 ConnectX 采用 MIT 许可证 开源。

ConnectX 使用了 ZeroTier，该项目自 2025 年 1 月 1 日 起采用 Apache 2.0 许可证，此前为 BSL 1.1 许可证。您可以在 https://github.com/zerotier/ZeroTierOne 查看 ZeroTier 的许可信息。

[详见](https://github.com/Corona-Studio/ConnectX/blob/main/ZEROTIER_LICENSE.txt)

## 耻辱柱

在此，我们将列出所有使用我们的代码但未遵守 MIT 许可的程序。
