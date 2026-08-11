using System.Net;

namespace ConnectX.Client.Interfaces;

public interface IClientSettingProvider
{
    IPAddress ServerAddress { get; }
    ushort ServerPort { get; }
}
