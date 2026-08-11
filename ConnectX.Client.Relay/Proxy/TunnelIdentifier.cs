namespace ConnectX.Client.Proxy;

public readonly record struct TunnelIdentifier(Guid PartnerId, ushort LocalRealPort, ushort RemoteRealPort);