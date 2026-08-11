using System.Net;
using ConnectX.Client.Interfaces;

namespace ConnectX.Client;

public class DefaultClientSettingProvider : IClientSettingProvider
{
    public required IPAddress ServerAddress { get; init; }
    public required ushort ServerPort { get; init; }
}
