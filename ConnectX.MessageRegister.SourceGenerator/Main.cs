using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace ConnectX.MessageRegister.SourceGenerator;

[Generator]
public class PacketRegisterSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classesWithAttribute = context.SyntaxProvider
            .CreateSyntaxProvider(IsCandidate, Transform)
            .Where(static m => m is not null);

        var compilationAndClasses = context.CompilationProvider.Combine(classesWithAttribute.Collect());

        context.RegisterSourceOutput(compilationAndClasses, (spc, source) =>
        {
            var (compilation, classes) = source;

            var packetTypes = classes
                .OfType<INamedTypeSymbol>()
                .Select(typeSymbol => typeSymbol.ToDisplayString())
                .ToList();

            var assemblyName = compilation.AssemblyName ?? "Generated";
            var generated = SourceGenHelper.GetCompleteDecl(packetTypes, assemblyName,
                new System.Collections.Generic.HashSet<string>(classes.OfType<INamedTypeSymbol>()
                    .Where(type => type.IsValueType).Select(type => type.ToDisplayString())));

            var codeString = generated.NormalizeWhitespace().ToFullString();
            spc.AddSource("PacketRegisterHelper.cs", SourceText.From(codeString, Encoding.UTF8));
        });
    }

    private static bool IsCandidate(SyntaxNode node, CancellationToken _) =>
        node is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax;

    private static INamedTypeSymbol? Transform(GeneratorSyntaxContext context, CancellationToken _)
    {
        var typeDeclaration = (TypeDeclarationSyntax)context.Node;

        foreach (var attributeList in typeDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                if (context.SemanticModel.GetSymbolInfo(attribute).Symbol is IMethodSymbol attributeSymbol)
                {
                    var attributeContainingType = attributeSymbol.ContainingType;
                    if (attributeContainingType.ToDisplayString() == "Hive.Codec.Shared.MessageDefineAttribute")
                    {
                        return context.SemanticModel.GetDeclaredSymbol(typeDeclaration);
                    }
                }
            }
        }

        return null;
    }
}