namespace ConnectX.Actors;

/// <summary>Generates a typed mailbox command and lifetime-scoped dispatcher binding.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ActorMessageAttribute : Attribute;

public interface IActorCommand
{
    ValueTask ExecuteAsync(CancellationToken cancellationToken);
    void Fail(Exception exception);
}
