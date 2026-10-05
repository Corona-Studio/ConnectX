using System.Net;
using System.Text;
using ConnectX.Shared;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Messages.Relay.Datagram;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

if (args.Length < 2)
    throw new ArgumentException("Usage: DeploymentProbe <connect-host-or-ip> <advertised-relay-ip> [--no-relay]");

using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(35));
var ip = IPAddress.TryParse(args[0], out var parsedIp)
    ? parsedIp
    : (await Dns.GetHostAddressesAsync(args[0], ct.Token))
        .First(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
services.AddConnectXEssentials();
using var sp = services.BuildServiceProvider();
var d = sp.GetRequiredService<IDispatcher>();
var connector = sp.GetRequiredService<IConnector<TcpSession>>();
var sessions = new List<ISession>();
async Task<ISession> Connect(int port) {
 var s = await connector.ConnectAsync(new IPEndPoint(ip,port),ct.Token) ?? throw new Exception("Connect failed");
 s.BindTo(d); _ = s.StartAsync(ct.Token); sessions.Add(s); await Task.Delay(300,ct.Token); return s;
}
async Task<R> Request<T,R>(ISession s,T msg) {
 var result = await d.SendAndListenOnce<T,R>(s,msg,ct.Token);
 return result ?? throw new Exception($"No {typeof(R).Name}");
}
void Check(bool ok,string msg) { if(!ok) throw new Exception(msg); Console.WriteLine("PASS: "+msg); }
try {
 var a = await Connect(3535);
 var signA = await Request<SigninMessage,SigninResult>(a,new() { DisplayName="deploy-probe-a",JoinP2PNetwork=true,LinkProtocolMajor=LinkProtocolConstants.ProtocolMajor,LinkProtocolMinor=LinkProtocolConstants.ProtocolMinor });
 Check(signA.Succeeded,"client A login");
 var direct = await Request<CreateGroup,GroupOpResult>(a,new() { RoomName="probe-direct",MaxUserCount=2,UseRelayServer=false });
 Check(direct.Status==GroupCreationStatus.NetworkControllerNotReady && direct.ErrorMessage!.Contains("disabled"),"direct room explicitly rejected when ZeroTier disabled");
 var create = await Request<CreateGroup,GroupOpResult>(a,new() { RoomName="deploy-probe",MaxUserCount=2,UseRelayServer=true,IsPrivate=true });
 if(args.Contains("--no-relay")) { Check(create.Status==GroupCreationStatus.Other && create.ErrorMessage!.Contains("No relay"),"relay room rejected when no relay is registered"); return; }
 Check(create.Status==GroupCreationStatus.Succeeded,"relay room created");
 var info = await Request<AcquireGroupInfo,GroupInfo>(a,new());
 Check(info.UseRelayServer && info.Users.All(u=>u.RelayServerAddress?.Port==3536 && u.RelayServerAddress.Address.ToString()==args[1]),"advertised public relay endpoint correct");
 var b = await Connect(3535);
 var signB = await Request<SigninMessage,SigninResult>(b,new() { DisplayName="deploy-probe-b",JoinP2PNetwork=false,LinkProtocolMajor=LinkProtocolConstants.ProtocolMajor,LinkProtocolMinor=LinkProtocolConstants.ProtocolMinor });
 Check(signB.Succeeded,"relay-only client B login");
 var join = await Request<JoinGroup,GroupOpResult>(b,new() { GroupId=create.RoomId });
 Check(join.Status==GroupCreationStatus.Succeeded,"client B joined relay room");
 await Task.Delay(500,ct.Token);
 var dataA=await Connect(3536); var dataB=await Connect(3536);
 await Request<CreateRelayDataLinkMessage,RelayDataLinkCreatedMessage>(dataA,new(){ UserId=signA.UserId,RoomId=create.RoomId });
 await Request<CreateRelayDataLinkMessage,RelayDataLinkCreatedMessage>(dataB,new(){ UserId=signB.UserId,RoomId=create.RoomId });
 var workerB=await Connect(3536);
 await Request<CreateRelayWorkerLinkMessage,RelayWorkerLinkCreatedMessage>(workerB,new(){ UserId=signB.UserId,RelayTo=signA.UserId,RoomId=create.RoomId });
 var received=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
 d.AddHandler<UnwrappedRelayDatagram>(ctx=> { if(ctx.Message.From==signA.UserId && Encoding.UTF8.GetString(ctx.Message.Payload.Span)=="connectx-probe") received.TrySetResult(true); });
 await d.SendAsync(dataA,new RelayDatagram(signA.UserId,signB.UserId,Encoding.UTF8.GetBytes("connectx-probe")),ct.Token);
 Check(await received.Task.WaitAsync(ct.Token),"TCP relay payload delivered A to B");
 await Request<LeaveGroup,GroupOpResult>(b,new());
 await Request<LeaveGroup,GroupOpResult>(a,new());
 Check(true,"relay room members left and room dismissed without ZeroTier");
} finally { foreach(var s in sessions) s.Close(); }
