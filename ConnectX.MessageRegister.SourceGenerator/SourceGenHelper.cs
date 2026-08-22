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

            // Merely storing typeof(T) does not trigger MemoryPack's generated
            // static constructor. NativeAOT can then trim its explicit
            // IMemoryPackFormatterRegister.RegisterFormatter implementation,
            // making runtime Type-based deserialization fail. Run the static
            // constructor while registering each packet so the formatter is
            // rooted and installed before the first packet arrives.
            result.Add(ParseStatement(
                $"RuntimeHelpers.RunClassConstructor(typeof({type}).TypeHandle);"));
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

    private static SyntaxList<MemberDeclarationSyntax> GetClassDecl(IEnumerable<string> packetTypes, string assemblyName)
    {
        return SingletonList<MemberDeclarationSyntax>(
            FileScopedNamespaceDeclaration(
                    IdentifierName(assemblyName))
                .WithMembers(
                    SingletonList<MemberDeclarationSyntax>(
                        ClassDeclaration($"{assemblyName.Replace(".", string.Empty)}PacketRegisterHelper")
                            .WithModifiers(
                                TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                            .WithMembers(GetMethodBody(packetTypes, assemblyName)))));
    }

    public static CompilationUnitSyntax GetCompleteDecl(IEnumerable<string> packetTypes, string assemblyName)
    {
        return CompilationUnit()
            .WithUsings(GetUsings())
            .WithMembers(GetClassDecl(packetTypes, assemblyName));
    }
}
