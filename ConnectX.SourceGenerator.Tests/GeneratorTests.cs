using Corona.SourceGeneration.Testing;
using ConnectX.MessageRegister.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace ConnectX.SourceGenerator.Tests;

[TestFixture]
public sealed class GeneratorTests
{
    private const string ActorSupport = """
        namespace Hive.Both.General.Dispatchers
        {
            public sealed class MessageContext<T> { public object FromSession => new(); }
            public readonly record struct HandlerId(int Value);
            public interface IDispatcher
            {
                HandlerId AddHandler<T>(System.Action<MessageContext<T>> handler);
                void RemoveHandler(HandlerId id);
            }
        }
        namespace ConnectX.Actors
        {
            public sealed class ActorMessageAttribute : System.Attribute;
            public interface IActorCommand
            {
                System.Threading.Tasks.ValueTask ExecuteAsync(System.Threading.CancellationToken token);
                void Fail(System.Exception exception);
            }
            public sealed class ControlPlaneActor { public bool TryPost(IActorCommand command) => true; }
            public static class SessionHealth { public static void Close(object session) { } }
        }
        """;

    [Test]
    public void ActorHandlersAcrossPartialDeclarationsCompile()
    {
        var compilation = GeneratorTestHost.Compilation("""
            public record struct ValueMessage(int Value);
            public partial class @event
            {
                [ConnectX.Actors.ActorMessage] private void Handle(Hive.Both.General.Dispatchers.MessageContext<ValueMessage> context) { }
            }
            public partial class @event
            {
                [ConnectX.Actors.ActorMessage] private async System.Threading.Tasks.Task @class(Hive.Both.General.Dispatchers.MessageContext<string> context)
                { await System.Threading.Tasks.Task.Yield(); }
            }
            """ + ActorSupport);
        var driver = GeneratorTestHost.Run(GeneratorTestHost.Driver(new ActorHandlerGenerator()), compilation);
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1));
        Assert.That(GeneratorTestHost.Text(driver), Does.Contain("owner.@class(context)"));
        driver = GeneratorTestHost.Run(driver, compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated;", GeneratorTestHost.ParseOptions)));
        Assert.That(driver.GetRunResult().Results.Single().TrackedSteps["ActorModels"].SelectMany(static step => step.Outputs)
            .All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.True);
    }

    [Test]
    public void InvalidActorSignatureReportsDiagnosticWithoutCrashing()
    {
        var driver = GeneratorTestHost.Driver(new ActorHandlerGenerator()).RunGenerators(
            GeneratorTestHost.Compilation("public partial class Owner { [ConnectX.Actors.ActorMessage] private void Wrong(int value) { } }" + ActorSupport));
        Assert.That(driver.GetRunResult().Diagnostics.Select(static d => d.Id), Contains.Item("CXACT001"));
        Assert.That(driver.GetRunResult().GeneratedTrees, Is.Empty);
    }

    [Test]
    public void PacketValueAndReferenceFormattersCompileAndStayCached()
    {
        var source = """
            namespace Example
            {
                [Hive.Codec.Shared.MessageDefine] public struct Value;
                [Hive.Codec.Shared.MessageDefine] public sealed class Reference;
            }
            namespace Hive.Codec.Shared
            {
                public sealed class MessageDefineAttribute : System.Attribute;
                public sealed class PacketIdMapperOptions { public void Register<T>() { } }
            }
            namespace Microsoft.Extensions.DependencyInjection
            {
                public interface IServiceCollection;
                public static class Extensions
                {
                    public static IServiceCollection Configure<T>(this IServiceCollection services, System.Action<T> action) where T : new() => services;
                }
            }
            namespace MemoryPack
            {
                public ref struct MemoryPackWriter<T> { public void WritePackable<TValue>(TValue value) { } }
                public ref struct MemoryPackReader { public void ReadPackable<TValue>(ref TValue value) { } }
                public abstract class MemoryPackFormatter<T>
                {
                    public abstract void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref T value);
                    public abstract void Deserialize(ref MemoryPackReader reader, scoped ref T value);
                }
                public static class MemoryPackFormatterProvider
                {
                    public static void Register(object formatter) { }
                    public static void Register<T>() { }
                }
                public static class MemoryPackSerializer
                {
                    public static byte[] Serialize<T>(T value) => [];
                    public static object? Deserialize(System.Type type, byte[] value) => null;
                }
            }
            """;
        var compilation = GeneratorTestHost.Compilation(source, "Example.Packets");
        var driver = GeneratorTestHost.Run(GeneratorTestHost.Driver(new PacketRegisterSourceGenerator()), compilation);
        Assert.That(GeneratorTestHost.Text(driver), Does.Contain("scoped ref global::Example.Value value"));
        Assert.That(GeneratorTestHost.Text(driver), Does.Contain("scoped ref global::Example.Reference? value"));
        Assert.That(GeneratorTestHost.Text(driver), Does.Contain("ValidateExamplePacketsPacketFormatters"));
        driver = GeneratorTestHost.Run(driver, compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated;", GeneratorTestHost.ParseOptions)));
        Assert.That(driver.GetRunResult().Results.Single().TrackedSteps["PacketModels"].SelectMany(static step => step.Outputs)
            .All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.True);
    }
}
