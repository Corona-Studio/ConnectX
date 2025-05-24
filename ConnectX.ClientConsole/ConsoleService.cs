using Microsoft.Extensions.Hosting;
using System.CommandLine;
using System.CommandLine.Invocation;
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
            new Option<string?>(["--password", "-pw"], "The password of the room");

        public static readonly Option UseRelayServerOption = new Option<bool>(["--relay", "-r"], "Should use relay server for the connection")
            .Required()
            .WithDefault(false);

        public static class Create
        {
            public static readonly Option NameOption = new Option<string>(["--name", "-n"], "The name of the room")
                .Required();

            public static readonly Option MaxUserOption =
                new Option<int>(["--max-user", "-mu"], "The max number of players in the room")
                    .Required();

            public static readonly Option DescriptionOption =
                new Option<string?>(["--description", "-d"], "The description of the room");

            public static readonly Option IsPrivateOption = new Option<bool>(["--private", "-p"], "Is the room private")
                .Required()
                .WithDefault(false);
        }

        public static class Join
        {
            public static readonly Option RoomIdOption = new Option<Guid?>(["--room_id", "-id"], "The ID of the room");

            public static readonly Option RoomShortIdOption = new Option<string?>(["--room_short_id", "-sid"], "The short ID of the room");
        }

        public static class Kick
        {
            public static readonly Option UserIdToKick = new Option<Guid?>(["--user_id", "-id"], "The ID of the user to kick");
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

        createCommand.SetHandler(HandleRoomCreateAsync);

        var joinCommand = new Command("join", "Join a room")
        {
            Commands.Room.Join.RoomIdOption,
            Commands.Room.Join.RoomShortIdOption,
            Commands.Room.PasswordOption
        };

        joinCommand.SetHandler(HandleRoomJoinAsync);

        var leaveCommand = new Command("leave", "Leave the room.");
        leaveCommand.SetHandler(HandleRoomLeaveAsync);

        var kickCommand = new Command("kick", "Kick a user")
        {
            Commands.Room.Kick.UserIdToKick
        };

        kickCommand.SetHandler(HandleRoomKickAsync);

        room.AddCommand(createCommand);
        room.AddCommand(joinCommand);
        room.AddCommand(leaveCommand);
        room.AddCommand(kickCommand);

        return room;
    }

    private async Task HandleRoomKickAsync(InvocationContext obj)
    {
        var userIdToKick = (Guid)obj.ParseResult.GetValueForOption(Commands.Room.Kick.UserIdToKick)!;

        if (_lastGroupInfo == null)
        {
            logger.LogError("You are not in any room");
            return;
        }

        var (status, error) = await client.KickUserAsync(new KickUser() { UserToKick = userIdToKick });

        logger.LogInformation("User kicked, {status:G}, {error}", status, error);
    }

    private async Task HandleRoomLeaveAsync(InvocationContext obj)
    {
        if (_lastGroupInfo == null)
        {
            logger.LogError("You are not in any room");
            return;
        }

        var (status, error) = await client.LeaveGroupAsync(new LeaveGroup());

        logger.LogInformation("Room left, {status:G}, {error}", status, error);
    }

    private async Task HandleRoomJoinAsync(InvocationContext obj)
    {
        var roomId = (Guid?)obj.ParseResult.GetValueForOption(Commands.Room.Join.RoomIdOption);
        var roomShortId = (string?)obj.ParseResult.GetValueForOption(Commands.Room.Join.RoomShortIdOption);
        var password = (string?)obj.ParseResult.GetValueForOption(Commands.Room.PasswordOption);

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

        logger.LogInformation("Room join result received, Info: {@info}, Status: {status:G}, Metadata: {@metadata}, Error: {error}", groupInfo, status, metadata, error);

        _lastGroupInfo = groupInfo;
    }

    private async Task HandleRoomCreateAsync(InvocationContext obj)
    {
        var name = (string)obj.ParseResult.GetValueForOption(Commands.Room.Create.NameOption)!;
        var maxUser = (int)obj.ParseResult.GetValueForOption(Commands.Room.Create.MaxUserOption)!;
        var description = (string?)obj.ParseResult.GetValueForOption(Commands.Room.Create.DescriptionOption);
        var password = (string?)obj.ParseResult.GetValueForOption(Commands.Room.PasswordOption);
        var isPrivate = (bool)obj.ParseResult.GetValueForOption(Commands.Room.Create.IsPrivateOption)!;
        var useRelayServer = (bool)obj.ParseResult.GetValueForOption(Commands.Room.UseRelayServerOption)!;

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

        logger.LogInformation("Room join result received, Info: {@info}, Status: {status:G}, Metadata: {@metadata}, Error: {error}", groupInfo, status, metadata, error);

        _lastGroupInfo = groupInfo;
    }

    private RootCommand BuildCommand()
    {
        var root = new RootCommand();

        root.AddCommand(RoomCommand());

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

            await rootCommand.InvokeAsync(ParseArguments(command));
        }
    }
}