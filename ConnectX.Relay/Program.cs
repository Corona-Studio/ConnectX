using Hive.Both.General.Dispatchers;
using ConnectX.Actors;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Net;
using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Managers;
using Microsoft.Extensions.DependencyInjection;
using ConnectX.Relay.Services;

namespace ConnectX.Relay
{
    internal static class Program
    {
        private static DefaultServerSettingProvider GetSettings(IConfiguration configuration)
        {
            var listenAddressStr = configuration.GetValue<string>("Server:ListenAddress");
            var listenPort = configuration.GetValue<ushort>("Server:ListenPort");
            var serverId = configuration.GetValue<Guid>("Server:ServerId");

            var relayListenAddressStr = configuration.GetValue<string>("RelayServer:ListenAddress");
            var relayServerPort = configuration.GetValue<ushort>("RelayServer:ListenPort");

            var publicListenAddressStr = configuration.GetValue<string>("RelayServer:PublicListenAddress");
            var publicListenPort = configuration.GetValue<ushort>("RelayServer:PublicListenPort");

            var serverPriority = configuration.GetValue<uint?>("RelayServer:ServerPriority");
            var maxReferenceConnectionCount = configuration.GetValue<int?>("RelayServer:MaxReferenceConnectionCount");

            ArgumentException.ThrowIfNullOrEmpty(listenAddressStr);
            ArgumentException.ThrowIfNullOrEmpty(relayListenAddressStr);
            ArgumentException.ThrowIfNullOrEmpty(publicListenAddressStr);

            var serverAddress = IPAddress.Parse(listenAddressStr);
            var relayServerAddress = IPAddress.Parse(relayListenAddressStr);
            var publicListenAddress = IPAddress.TryParse(publicListenAddressStr, out var outListenAddress) ? outListenAddress : null;

            return new DefaultServerSettingProvider
            {
                ServerAddress = serverAddress,
                ServerPort = listenPort,
                RelayServerAddress = relayServerAddress,
                RelayServerPort = relayServerPort,
                JoinP2PNetwork = true,
                ServerId = serverId,
                EndPoint = new IPEndPoint(serverAddress, listenPort),
                RelayEndPoint = new IPEndPoint(relayServerAddress, relayServerPort),
                PublicListenAddress = publicListenAddress,
                PublicListenPort = publicListenPort,
                ServerPriority = serverPriority ?? 80,
                MaxReferenceConnectionCount = maxReferenceConnectionCount ?? 50
            };
        }

        private static void Main(string[] args)
        {
            var builder = Host
                .CreateDefaultBuilder(args)
                .UseSerilog((hostingContext, loggerConfiguration) => loggerConfiguration
                    .ReadFrom.Configuration(hostingContext.Configuration));

            builder.ConfigureServices((ctx, services) =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<ControlPlaneActor>();
                services.AddHostedService(sp => sp.GetRequiredService<ControlPlaneActor>());
                services.AddConnectXEssentials();
                services.AddSingleton<IDispatcher, ActorDispatcher>();
                services.AddSingleton<IServerSettingProvider>(_ => GetSettings(ctx.Configuration));

                services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
                services.AddHostedService(sP => sP.GetRequiredService<IServerLinkHolder>());

                services.AddSingleton<ClientManager>();
                services.AddSingleton<RelayManager>();
                services.AddHostedService(sp => sp.GetRequiredService<RelayManager>());

                services.AddHostedService(sc => sc.GetRequiredService<ClientManager>());

                services.AddHostedService<RelayServer>();
                services.AddHostedService<RelayLoadReportService>();
            });

            var app = builder.Build();

            app.Run();
        }
    }
}
