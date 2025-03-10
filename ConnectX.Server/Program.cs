﻿using ConnectX.Server.Interfaces;
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

            services.AddDbContext<RoomOpsHistoryContext>(o =>
                o.UseSqlServer(connectionString, b => b.MigrationsAssembly("ConnectX.Server")));

            services.AddHttpClient<IZeroTierApiService, ZeroTierApiService>(client =>
            {
                client.BaseAddress = new Uri(configuration["ZeroTier:EndPoint"]!);
                client.DefaultRequestHeaders.Add("X-ZT1-AUTH", configuration["ZeroTier:Token"]);
            });

            services.AddSingleton<IServerSettingProvider, ConfigSettingProvider>();
            services.AddConnectXEssentials();

            services.AddSingleton<ClientManager>();
            services.AddSingleton<GroupManager>();
            services.AddSingleton<P2PManager>();
            services.AddSingleton<RelayServerManager>();

            services.AddSingleton<IZeroTierNodeInfoService, ZeroTierNodeInfoService>();
            services.AddHostedService(sc => sc.GetRequiredService<IZeroTierNodeInfoService>());

            services.AddSingleton<PeerInfoService>();
            services.AddHostedService(sc => sc.GetRequiredService<PeerInfoService>());

            services.AddSingleton<RoomJoinRecordService>();
            services.AddHostedService(sc => sc.GetRequiredService<RoomJoinRecordService>());

            services.AddSingleton<RoomCreationRecordService>();
            services.AddHostedService(sc => sc.GetRequiredService<RoomCreationRecordService>());

            services.AddHostedService(sc => sc.GetRequiredService<ClientManager>());
            services.AddHostedService<Server>();
        });

        var app = builder.Build();

        app.Run();
    }
}