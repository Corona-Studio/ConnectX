using System;
using System.Linq;
using Corona.SourceGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConnectX.MessageRegister.SourceGenerator;

[Generator]
public sealed class ActorHandlerGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidHandler = new(
        "CXACT001", "Invalid actor handler", "Actor handler '{0}' must be a non-static void/Task method with one MessageContext<T> parameter in a top-level, non-generic partial class",
        "ConnectX.Actors", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName("ConnectX.Actors.ActorMessageAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, token) =>
            {
                token.ThrowIfCancellationRequested();
                var method = (IMethodSymbol)ctx.TargetSymbol;
                var type = method.ContainingType;
                var valid = type.ContainingType is null && type.Arity == 0 && !type.IsStatic && !method.IsStatic && method.Arity == 0 &&
                    method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None &&
                    method.Parameters[0].Type is INamedTypeSymbol parameter &&
                    parameter.OriginalDefinition.ToDisplayString() == "Hive.Both.General.Dispatchers.MessageContext<T>" &&
                    (method.ReturnsVoid || method.ReturnType.ToDisplayString() == "System.Threading.Tasks.Task") && type.IsPartial(token);
                var identity = CSharpNames.MetadataName(type);
                if (!valid) return new ActorResult(identity, null, DiagnosticInfo.Create(InvalidHandler, method.Locations.FirstOrDefault(), method.Name));
                var contextType = (INamedTypeSymbol)method.Parameters[0].Type;
                return new ActorResult(identity, new(TypeDeclarationModel.From(type), CSharpNames.Identifier(type.Name),
                    method.Name, contextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    contextType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), method.ReturnsVoid,
                    type.InheritsFrom("Microsoft.Extensions.Hosting.BackgroundService")), null);
            });
        context.RegisterSourceOutput(results.Where(static result => result.Error is not null),
            static (output, result) => output.ReportDiagnostic(result.Error!.ToDiagnostic()));
        var models = results.Collect().SelectMany(static (items, _) => items.GroupBy(static item => item.Identity)
            .Where(static group => group.All(static item => item.Method is not null))
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new ActorModel(new(group.Select(static item => item.Method!)
                .OrderBy(static method => method.Name, StringComparer.Ordinal).ThenBy(static method => method.Parameter, StringComparer.Ordinal))))
            .ToArray()).WithTrackingName("ActorModels");
        context.RegisterSourceOutput(models, static (output, model) => output.AddSource(ActorEmitter.Emit(model)));
    }
}

internal sealed record ActorResult(string Identity, ActorMethod? Method, DiagnosticInfo? Error);
internal sealed record ActorMethod(TypeDeclarationModel Type, string OwnerName, string Name, string Parameter,
    string Message, bool ReturnsVoid, bool Background);
internal sealed record ActorModel(EquatableArray<ActorMethod> Methods);
