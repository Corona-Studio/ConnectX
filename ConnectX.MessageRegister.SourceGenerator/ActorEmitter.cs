using Corona.SourceGeneration;

namespace ConnectX.MessageRegister.SourceGenerator;

internal static class ActorEmitter
{
    public static SourceFile Emit(ActorModel model)
    {
        var owner = model.Methods[0];
        var writer = new CodeWriter().Header();
        owner.Type.Open(writer, " : global::System.IDisposable");
        writer.Line("private global::Hive.Both.General.Dispatchers.IDispatcher? _actorDispatcher;")
            .Line("private readonly global::System.Collections.Generic.List<global::Hive.Both.General.Dispatchers.HandlerId> _actorHandlerIds = new();")
            .Open("private void RegisterActorHandlers(global::Hive.Both.General.Dispatchers.IDispatcher dispatcher, global::ConnectX.Actors.ControlPlaneActor actor)")
            .Line("_actorDispatcher = dispatcher;");
        for (var i = 0; i < model.Methods.Count; i++)
            writer.Line($"_actorHandlerIds.Add(dispatcher.AddHandler<{model.Methods[i].Message}>(ctx => {{ if (!actor.TryPost(new ActorCommand{i}(this, ctx))) global::ConnectX.Actors.SessionHealth.Close(ctx.FromSession); }}));");
        writer.Close().Open($"public {(owner.Background ? "override " : "")}void Dispose()")
            .Line("if (_actorDispatcher != null) foreach (var id in _actorHandlerIds) _actorDispatcher.RemoveHandler(id);")
            .Line("_actorHandlerIds.Clear();").Line("DisposeActorResources();");
        if (owner.Background) writer.Line("base.Dispose();");
        writer.Close().Line("partial void DisposeActorResources();");
        for (var i = 0; i < model.Methods.Count; i++)
        {
            var method = model.Methods[i];
            writer.Line().Open($"private sealed class ActorCommand{i}({owner.OwnerName} owner, {method.Parameter} context) : global::ConnectX.Actors.IActorCommand")
                .Open($"public {(method.ReturnsVoid ? "" : "async ")}global::System.Threading.Tasks.ValueTask ExecuteAsync(global::System.Threading.CancellationToken cancellationToken)")
                .Line($"{(method.ReturnsVoid ? "" : "await ")}owner.{CSharpNames.Identifier(method.Name)}(context);");
            if (method.ReturnsVoid) writer.Line("return global::System.Threading.Tasks.ValueTask.CompletedTask;");
            writer.Close().Line("public void Fail(global::System.Exception exception) => global::ConnectX.Actors.SessionHealth.Close(context.FromSession);").Close();
        }
        owner.Type.Close(writer);
        return new(CSharpNames.HintName(owner.Type.Identity) + ".ActorHandlers.g.cs", writer.ToString());
    }
}
