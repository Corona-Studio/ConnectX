using Microsoft.Extensions.Hosting;
using System.CommandLine;
using ConnectX.Client.Interfaces;
using ConnectX.ClientConsole.Helpers;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using Microsoft.Extensions.Logging;

namespace ConnectX.ClientConsole;

internal static class Commands
{
    public static class Room
    {
        public static readonly Option PasswordOption =
            new Option<string?>("--password", "-pw") { Description = "The password of the room" };

        public static readonly Option UseRelayServerOption =
            new Option<bool>("--relay", "-r") { Description = "Should use relay server for the connection" }
                .Required()
                .WithDefault(false);

        public static class Create
        {
            public static readonly Option NameOption =
                new Option<string>("--name", "-n") { Description = "The name of the room" }
                    .Required();

            public static readonly Option MaxUserOption =
                new Option<int>("--max-user", "-mu") { Description = "The max number of players in the room" }
                    .Required();

            public static readonly Option DescriptionOption =
                new Option<string?>("--description", "-d") { Description = "The description of the room" };

            public static readonly Option IsPrivateOption =
                new Option<bool>("--private", "-p") { Description = "Is the room private" }
                    .Required()
                    .WithDefault(false);
        }

        public static class Join
        {
            public static readonly Option RoomIdOption = new Option<Guid?>("--room_id", "-id")
                { Description = "The ID of the room" };

            public static readonly Option RoomShortIdOption =
                new Option<string?>("--room_short_id", "-sid") { Description = "The short ID of the room" };
        }

        public static class Kick
        {
            public static readonly Option UserIdToKick =
                new Option<Guid>("--user_id", "-id") { Description = "The ID of the user to kick" }.Required();
        }
    }
}

public class ConsoleService(
    IServerLinkHolder serverLinkHolder,
    Client.Client client,
    ILogger<ConsoleService> logger)
    : BackgroundService
{
    private GroupInfo? _lastGroupInfo;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await serverLinkHolder.ConnectAsync(cancellationToken);
        await TaskHelper.WaitUntilAsync(() => serverLinkHolder.IsConnected, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    private static string[] ParseArguments(string commandLine)
    {
        var paraChars = commandLine.ToCharArray();
        var inQuote = false;

        for (var index = 0; index < paraChars.Length; index++)
        {
            if (paraChars[index] == '"')
                inQuote = !inQuote;
            if (!inQuote && paraChars[index] == ' ')
                paraChars[index] = '\n';
        }

        return new string(paraChars).Split('\n');
    }

    private Command RoomCommand()
    {
        var room = new Command("room");

        var createCommand = new Command("create", "Create a new room")
        {
            Commands.Room.Create.NameOption,
            Commands.Room.Create.MaxUserOption,
            Commands.Room.Create.DescriptionOption,
            Commands.Room.PasswordOption,
            Commands.Room.Create.IsPrivateOption,
            Commands.Room.UseRelayServerOption
        };

        createCommand.SetAction(HandleRoomCreateAsync);

        var joinCommand = new Command("join", "Join a room")
        {
            Commands.Room.Join.RoomIdOption,
            Commands.Room.Join.RoomShortIdOption,
            Commands.Room.PasswordOption
        };

        joinCommand.SetAction(HandleRoomJoinAsync);

        var leaveCommand = new Command("leave", "Leave the room.");
        leaveCommand.SetAction(HandleRoomLeaveAsync);

        var kickCommand = new Command("kick", "Kick a user")
        {
            Commands.Room.Kick.UserIdToKick
        };

        kickCommand.SetAction(HandleRoomKickAsync);

        room.Add(createCommand);
        room.Add(joinCommand);
        room.Add(leaveCommand);
        room.Add(kickCommand);

        return room;
    }

    private async Task HandleRoomKickAsync(ParseResult parseResult)
    {
        var userIdToKick = parseResult.GetValue<Guid>(Commands.Room.Kick.UserIdToKick.Name);

        if (_lastGroupInfo == null)
        {
            logger.LogError("You are not in any room");
            return;
        }

        var (status, error) = await client.KickUserAsync(new KickUser() { UserToKick = userIdToKick });

        logger.LogInformation("User kicked, {status:G}, {error}", status, error);
    }

    private async Task HandleRoomLeaveAsync(ParseResult parseResult)
    {
        if (_lastGroupInfo == null)
        {
            logger.LogError("You are not in any room");
            return;
        }

        var (status, error) = await client.LeaveGroupAsync(new LeaveGroup());

        logger.LogInformation("Room left, {status:G}, {error}", status, error);
    }

    private async Task HandleRoomJoinAsync(ParseResult parseResult)
    {
        var roomId = parseResult.GetValue<Guid?>(Commands.Room.Join.RoomIdOption.Name);
        var roomShortId = parseResult.GetValue<string?>(Commands.Room.Join.RoomShortIdOption.Name);
        var password = parseResult.GetValue<string?>(Commands.Room.PasswordOption.Name);

        if (!roomId.HasValue && string.IsNullOrEmpty(roomShortId))
        {
            logger.LogError("Room ID or Room Short ID is required");
            return;
        }

        var message = new JoinGroup
        {
            GroupId = roomId ?? Guid.Empty,
            RoomShortId = roomShortId,
            RoomPassword = password,
            // UseRelayServer = useRelayServer
        };

        var (groupInfo, status, metadata, error) = await client.JoinGroupAsync(message, CancellationToken.None);

        logger.LogInformation(
            "Room join result received, Info: {@info}, Status: {status:G}, Metadata: {@metadata}, Error: {error}",
            groupInfo, status, metadata, error);

        _lastGroupInfo = groupInfo;
    }

    private async Task HandleRoomCreateAsync(ParseResult parseResult)
    {
        var name = parseResult.GetValue<string>(Commands.Room.Create.NameOption.Name)!;
        var maxUser = parseResult.GetValue<int>(Commands.Room.Create.MaxUserOption.Name);
        var description = parseResult.GetValue<string?>(Commands.Room.Create.DescriptionOption.Name);
        var password = parseResult.GetValue<string?>(Commands.Room.PasswordOption.Name);
        var isPrivate = parseResult.GetValue<bool>(Commands.Room.Create.IsPrivateOption.Name);
        var useRelayServer = parseResult.GetValue<bool>(Commands.Room.UseRelayServerOption.Name);

        var message = new CreateGroup
        {
            IsPrivate = isPrivate,
            RoomName = name,
            RoomDescription = description,
            RoomPassword = password,
            MaxUserCount = maxUser,
            UseRelayServer = useRelayServer
        };

        var (groupInfo, status, metadata, error) = await client.CreateGroupAsync(message, CancellationToken.None);

        logger.LogInformation(
            "Room join result received, Info: {@info}, Status: {status:G}, Metadata: {@metadata}, Error: {error}",
            groupInfo, status, metadata, error);

        _lastGroupInfo = groupInfo;
    }

    private RootCommand BuildCommand()
    {
        var root = new RootCommand { RoomCommand() };

        return root;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rootCommand = BuildCommand();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Task.Delay(2000, CancellationToken.None).ContinueWith(_ => Environment.Exit(0), CancellationToken.None);
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Yield();

            Console.Write(">:");
            var command = Console.ReadLine();
            await Task.Yield();

            if (string.IsNullOrEmpty(command))
                continue;

            var parseResult = rootCommand.Parse(ParseArguments(command));
            await parseResult.InvokeAsync(cancellationToken: stoppingToken);
        }
    }
}