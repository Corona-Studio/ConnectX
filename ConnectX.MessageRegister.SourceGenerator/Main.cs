using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;

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
                return (Name: type.ToDisplayString(), IsValue: type.IsValueType);
            });
        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "Generated");
        context.RegisterSourceOutput(assemblyName.Combine(packetModels.Collect()), static (output, source) =>
        {
            var (name, models) = source;
            var packetTypes = models.Select(model => model.Name).Distinct()
                .OrderBy(type => type, System.StringComparer.Ordinal).ToList();
            var valueTypes = new System.Collections.Generic.HashSet<string>(
                models.Where(model => model.IsValue).Select(model => model.Name));
            var generated = SourceGenHelper.GetCompleteDecl(packetTypes, name, valueTypes);
            output.AddSource("PacketRegisterHelper.cs", SourceText.From(generated.NormalizeWhitespace().ToFullString(), Encoding.UTF8));
        });
    }
}
