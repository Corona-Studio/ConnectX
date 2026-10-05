using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;

using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ConnectX.MessageRegister.SourceGenerator;

public static class SourceGenHelper
{
    private static SyntaxList<UsingDirectiveSyntax> GetUsings()
    {
        return List(
        [
            UsingDirective(
                QualifiedName(
                    QualifiedName(
                        IdentifierName("Microsoft"),
                        IdentifierName("Extensions")),
                    IdentifierName("DependencyInjection"))),
            UsingDirective(
                QualifiedName(
                    QualifiedName(
                        IdentifierName("Hive"),
                        IdentifierName("Codec")),
                    IdentifierName("Shared"))),
            UsingDirective(
                QualifiedName(
                    QualifiedName(
                        IdentifierName("System"),
                        IdentifierName("Runtime")),
                    IdentifierName("CompilerServices")))
        ]);
    }

    private static List<StatementSyntax> GetRegisterBlock(IEnumerable<string> packetTypes)
    {
        var result = new List<StatementSyntax>();

        var formatterIndex = 0;
        foreach (var type in packetTypes)
        {
            var statement = ExpressionStatement(
                InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind
                            .SimpleMemberAccessExpression,
                        IdentifierName(
                            "options"),
                        GenericName(
                                Identifier(
                                    "Register"))
                            .WithTypeArgumentList(
                                TypeArgumentList(SingletonSeparatedList<TypeSyntax>(IdentifierName(type)))))));

            result.Add(statement);

            // Non-generic formatter types give NativeAOT concrete dispatch targets
            // for every packet, including Type-based decoding and union members.
            result.Add(ParseStatement(
                $"MemoryPack.MemoryPackFormatterProvider.Register(new PacketFormatter{formatterIndex++}());"));
            // Call the generated registration interface directly so NativeAOT
            // roots each concrete formatter before Type-based decoding.
            result.Add(ParseStatement(
                $"MemoryPack.MemoryPackFormatterProvider.Register<{type}>();"));
        }

        return result;
    }

    private static SyntaxList<MemberDeclarationSyntax> GetMethodBody(IEnumerable<string> packetTypes, string assemblyName)
    {
        return SingletonList<MemberDeclarationSyntax>(
            GlobalStatement(
                LocalFunctionStatement(
                        IdentifierName("IServiceCollection"),
                        Identifier($"Register{assemblyName.Replace(".", string.Empty)}Packets"))
                    .WithModifiers(
                        TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                    .WithParameterList(
                        ParameterList(
                            SingletonSeparatedList(
                                Parameter(
                                        Identifier("services"))
                                    .WithModifiers(
                                        TokenList(
                                            Token(SyntaxKind.ThisKeyword)))
                                    .WithType(
                                        IdentifierName("IServiceCollection")))))
                    .WithBody(
                        Block(
                            ExpressionStatement(
                                InvocationExpression(
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            IdentifierName("services"),
                                            GenericName(
                                                    Identifier("Configure"))
                                                .WithTypeArgumentList(
                                                    TypeArgumentList(
                                                        SingletonSeparatedList<TypeSyntax>(
                                                            IdentifierName("PacketIdMapperOptions"))))))
                                    .WithArgumentList(
                                        ArgumentList(
                                            SingletonSeparatedList(
                                                Argument(
                                                    SimpleLambdaExpression(
                                                            Parameter(
                                                                Identifier("options")))
                                                        .WithBlock(
                                                            Block(GetRegisterBlock(packetTypes)
                                                                .ToArray()))))))),
                            ReturnStatement(
                                IdentifierName("services"))
                        ))));
    }

    private static SyntaxList<MemberDeclarationSyntax> GetClassDecl(IEnumerable<string> packetTypes, string assemblyName, ISet<string> valueTypes)
    {
        return SingletonList<MemberDeclarationSyntax>(
            FileScopedNamespaceDeclaration(
                    IdentifierName(assemblyName))
                .WithMembers(
                    SingletonList<MemberDeclarationSyntax>(
                        ClassDeclaration($"{assemblyName.Replace(".", string.Empty)}PacketRegisterHelper")
                            .WithModifiers(
                                TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                            .WithMembers(GetMethodBody(packetTypes, assemblyName)
                                .Add(GetValidationMethod(packetTypes, assemblyName))
                                .AddRange(GetFormatters(packetTypes, valueTypes))))));
    }

    private static MemberDeclarationSyntax GetValidationMethod(IEnumerable<string> packetTypes, string assemblyName)
    {
        var body = new System.Text.StringBuilder();
        foreach (var type in packetTypes)
        {
            body.AppendLine($"MemoryPack.MemoryPackFormatterProvider.Register<{type}>();");
            body.AppendLine($"_ = MemoryPack.MemoryPackSerializer.Deserialize(typeof({type}), MemoryPack.MemoryPackSerializer.Serialize<{type}>(default));");
        }

        return ParseMemberDeclaration($"public static void Validate{assemblyName.Replace(".", string.Empty)}PacketFormatters() {{ {body} }}")!;
    }

    private static IEnumerable<MemberDeclarationSyntax> GetFormatters(IEnumerable<string> packetTypes, ISet<string> valueTypes)
    {
        var index = 0;
        foreach (var type in packetTypes)
        {
            var valueType = valueTypes.Contains(type) ? type : type + "?";
            yield return ParseMemberDeclaration($@"
private sealed class PacketFormatter{index++} : MemoryPack.MemoryPackFormatter<{type}>
{{
    public override void Serialize<TBufferWriter>(ref MemoryPack.MemoryPackWriter<TBufferWriter> writer, scoped ref {valueType} value)
        => writer.WritePackable(value);
    public override void Deserialize(ref MemoryPack.MemoryPackReader reader, scoped ref {valueType} value)
        => reader.ReadPackable(ref value);
}}")!;
        }
    }

    public static CompilationUnitSyntax GetCompleteDecl(IEnumerable<string> packetTypes, string assemblyName, ISet<string> valueTypes)
    {
        return CompilationUnit()
            .WithUsings(GetUsings())
            .WithMembers(GetClassDecl(packetTypes, assemblyName, valueTypes));
    }
}
