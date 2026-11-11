using ConnectX.Shared.Formatters;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Codec.MemoryPack;
using Hive.Codec.Shared;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Hive.Network.Udp;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Shared.Helpers;

public static class InitHelper
{
    extension(IServiceCollection services)
    {
        public void AddConnectXEssentials()
        {
            MemoryPackFormatterProvider.Register(new IpEndPointFormatter());
            MemoryPackFormatterProvider.Register(new IpAddressFormatter());

            services.RegisterConnectXSharedPackets();

            services.AddSingleton<ICustomCodecProvider, DefaultCustomCodecProvider>();
            services.AddSingleton<IPacketIdMapper, DefaultPacketIdMapper>();
            services.AddSingleton<IPacketCodec, MemoryPackPacketCodec>();
            services.AddSingleton<IDispatcher, DefaultDispatcher>();

            // TCP
            services.AddSingleton<IAcceptor<TcpSession>, TcpAcceptor>();
            services.AddSingleton<IConnector<TcpSession>, TcpConnector>();

            // UDP
            services.AddSingleton<IAcceptor<UdpSession>, UdpAcceptor>();
            services.AddSingleton<IConnector<UdpSession>, UdpConnector>();
        }
    }
}