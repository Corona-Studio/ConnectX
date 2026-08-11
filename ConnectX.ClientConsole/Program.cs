using System.Net;
using ConnectX.Client;
using ConnectX.Client.Helpers;
using ConnectX.Client.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ConnectX.ClientConsole;

internal static class Program
{
    private static IClientSettingProvider GetSettings(IConfiguration configuration)
    {
        var listenAddressStr = configuration.GetValue<string>("Server:ListenAddress");
        var port = configuration.GetValue<ushort>("Server:ListenPort");

        ArgumentException.ThrowIfNullOrEmpty(listenAddressStr);

        return new DefaultClientSettingProvider
        {
            ServerAddress = IPAddress.Parse(listenAddressStr),
            ServerPort = port
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
            services.AddHostedService<ConsoleService>();
            services.UseConnectXZeroTier(_ => GetSettings(ctx.Configuration));
        });

        var app = builder.Build();

        app.Run();
    }
}
