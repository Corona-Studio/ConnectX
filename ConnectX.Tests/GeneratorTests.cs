using ConnectX.MessageRegister.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class GeneratorTests
{
    [Test]
    public void InvalidHandlerProducesActionableDiagnostic()
    {
        var compilation = CreateCompilation("""
            namespace Example;
            public class Owner
            {
                [ConnectX.Actors.ActorMessage]
                private void Handle(int message) { }
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ActorHandlerGenerator());
        driver = driver.RunGenerators(compilation);
        Assert.That(driver.GetRunResult().Diagnostics.Select(x => x.Id), Contains.Item("CXACT001"));
    }

    [Test]
    public void GeneratedTaskAndStructHandlersCompileAndAreDeterministic()
    {
        var compilation = CreateCompilation("""
            namespace Example;
            public record struct ValueMessage(int Value);
            public partial class Owner
            {
                [ConnectX.Actors.ActorMessage]
                private async System.Threading.Tasks.Task Handle(Hive.Both.General.Dispatchers.MessageContext<ValueMessage> context)
                { await System.Threading.Tasks.Task.Yield(); }
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ActorHandlerGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        Assert.That(diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error), Is.Empty);
        Assert.That(generated.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error), Is.Empty);
        var first = driver.GetRunResult().GeneratedTrees.Single().ToString();
        driver = driver.RunGenerators(compilation);
        Assert.That(driver.GetRunResult().GeneratedTrees.Single().ToString(), Is.EqualTo(first));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("GeneratorTest", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
