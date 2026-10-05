using Hive.Both.General.Dispatchers;
using ConnectX.Actors;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Server.Models.Contexts;
using ConnectX.Server.Services;
using ConnectX.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ConnectX.Server;

internal static class Program
{
    private static void Main(string[] args)
    {
        var builder = Host
            .CreateDefaultBuilder(args)
            .UseSerilog((hostingContext, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(hostingContext.Configuration));

        builder.ConfigureServices((ctx, services) =>
        {
            var configuration = ctx.Configuration;
            var connectionString = configuration.GetConnectionString("Default");
            var useSqlite = configuration.GetSection("Server:UseSqlite").Get<bool>();

            services.AddDbContext<RoomOpsHistoryContext>(o =>
            {
                if (useSqlite) o.UseSqlite(connectionString, b => b.MigrationsAssembly("ConnectX.Server"));
                else o.UseSqlServer(connectionString, b => b.MigrationsAssembly("ConnectX.Server"));
            });

            services.AddSingleton<IServerSettingProvider, ConfigSettingProvider>();
            services.AddSingleton<IInterconnectServerSettingProvider, InterconnectServerSettingProvider>();

            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ControlPlaneActor>();
            services.AddHostedService(sp => sp.GetRequiredService<ControlPlaneActor>());
            services.AddConnectXEssentials();
            services.AddSingleton<IDispatcher, ActorDispatcher>();
            services.RegisterConnectXServerPackets();

            services.AddSingleton<ClientManager>();
            services.AddSingleton<GroupManager>();
            services.AddSingleton<P2PManager>();
            services.AddSingleton<RelayServerManager>();
            services.AddSingleton<RelayLoadManager>();
            services.AddSingleton<InterconnectServerManager>();

            services.AddHostedService<InterconnectServerLinkHolder>();

            if (configuration.GetValue("ZeroTier:Enabled", true))
            {
                services.AddHttpClient<IZeroTierApiService, ZeroTierApiService>(client =>
                {
                    client.BaseAddress = new Uri(configuration["ZeroTier:EndPoint"]!);
                    client.DefaultRequestHeaders.Add("X-ZT1-AUTH", configuration["ZeroTier:Token"]);
                });
                services.AddSingleton<IZeroTierNodeInfoService, ZeroTierNodeInfoService>();
                services.AddHostedService(sc => sc.GetRequiredService<IZeroTierNodeInfoService>());
                services.AddSingleton<PeerInfoService>();
                services.AddHostedService(sc => sc.GetRequiredService<PeerInfoService>());
                services.AddSingleton<RoomJoinRecordService>();
                services.AddHostedService(sc => sc.GetRequiredService<RoomJoinRecordService>());
            }

            services.AddSingleton<RoomCreationRecordService>();
            services.AddHostedService(sc => sc.GetRequiredService<RoomCreationRecordService>());

            services.AddHostedService(sc => sc.GetRequiredService<P2PManager>());
            services.AddHostedService(sc => sc.GetRequiredService<ClientManager>());
            services.AddHostedService<Server>();
        });

        var app = builder.Build();

        // Initialize before hosted record services start concurrently (.NET 10).
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>().Database.EnsureCreated();

        app.Run();
    }
}