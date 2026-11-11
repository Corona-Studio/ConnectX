using System.Collections.Frozen;
using System.Net;
using ConnectX.Server.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public class InterconnectServerSettingProvider : IInterconnectServerSettingProvider
{
    public InterconnectServerSettingProvider(
        IConfiguration configuration,
        ILogger<InterconnectServerSettingProvider> logger)
    {
        var listenAddressStr = configuration.GetSection("Interconnects").Get<string[]>() ?? [];
        var interconnectIpeList = new List<IPEndPoint>();

        foreach (var address in listenAddressStr)
        {
            if (IPEndPoint.TryParse(address, out var listenIpe))
            {
                interconnectIpeList.Add(listenIpe);
                logger.LogInterconnectServerAddressParsed(address);
                continue;
            }

            logger.LogCanNotParseInterconnectServerIpAddress(address);
        }

        EndPoints = interconnectIpeList.ToFrozenSet();
    }

    public FrozenSet<IPEndPoint> EndPoints { get; }
}

internal static partial class InterconnectServerSettingProviderLoggers
{
    [LoggerMessage(LogLevel.Information, "Interconnect server address parsed [{address}]")]
    public static partial void LogInterconnectServerAddressParsed(
        this ILogger logger,
        string address);

    [LoggerMessage(LogLevel.Error, "Can not parse the InterconnectServer:ListenAddress [{address}] to IPAddress")]
    public static partial void LogCanNotParseInterconnectServerIpAddress(
        this ILogger logger,
        string address);
}