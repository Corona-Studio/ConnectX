using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using System.Linq;
using Corona.SourceGeneration;

namespace ConnectX.MessageRegister.SourceGenerator;

[Generator]
public sealed class PacketRegisterSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var packetModels = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Hive.Codec.Shared.MessageDefineAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) =>
            {
                var type = (INamedTypeSymbol)ctx.TargetSymbol;
                return (Name: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), IsValue: type.IsValueType);
            }).Collect().Select(static (models, _) => new EquatableArray<(string Name, bool IsValue)>(
                models.Distinct().OrderBy(static model => model.Name, System.StringComparer.Ordinal)));
        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "Generated");
        context.RegisterSourceOutput(assemblyName.Combine(packetModels).WithTrackingName("PacketModels"), static (output, source) =>
        {
            var (name, models) = source;
            var packetTypes = models.Select(model => model.Name).Distinct()
                .OrderBy(type => type, System.StringComparer.Ordinal).ToList();
            var valueTypes = new System.Collections.Generic.HashSet<string>(
                models.Where(model => model.IsValue).Select(model => model.Name));
            output.AddSource(new SourceFile("PacketRegisterHelper.g.cs", PacketEmitter.Emit(packetTypes, name, valueTypes)));
        });
    }
}
