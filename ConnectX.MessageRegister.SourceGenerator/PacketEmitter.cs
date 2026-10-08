using System.Collections.Generic;
using System.Linq;
using Corona.SourceGeneration;

namespace ConnectX.MessageRegister.SourceGenerator;

internal static class PacketEmitter
{
    public static string Emit(IReadOnlyList<string> packets, string assembly, ISet<string> valueTypes)
    {
        var name = assembly.Replace(".", "");
        var writer = new CodeWriter().Header();
        writer.Line("using Microsoft.Extensions.DependencyInjection;");
        writer.Open("namespace " + string.Join(".", assembly.Split('.').Select(CSharpNames.Identifier)))
            .Open("public static class " + CSharpNames.Identifier(name + "PacketRegisterHelper"));
        writer.Open($"public static IServiceCollection {CSharpNames.Identifier("Register" + name + "Packets")}(this IServiceCollection services)")
            .Line("services.Configure<global::Hive.Codec.Shared.PacketIdMapperOptions>(options =>")
            .Line("{");
        for (var i = 0; i < packets.Count; i++)
        {
            var type = packets[i];
            writer.Line($"    options.Register<{type}>();")
                .Line($"    global::MemoryPack.MemoryPackFormatterProvider.Register(new PacketFormatter{i}());")
                .Line($"    global::MemoryPack.MemoryPackFormatterProvider.Register<{type}>();");
        }
        writer.Line("});").Line("return services;").Close().Line();
        writer.Open($"public static void {CSharpNames.Identifier("Validate" + name + "PacketFormatters")}()");
        foreach (var type in packets)
            writer.Line($"global::MemoryPack.MemoryPackFormatterProvider.Register<{type}>();")
                .Line($"_ = global::MemoryPack.MemoryPackSerializer.Deserialize(typeof({type}), global::MemoryPack.MemoryPackSerializer.Serialize<{type}>(default));");
        writer.Close();
        for (var i = 0; i < packets.Count; i++)
        {
            var type = packets[i];
            var nullable = valueTypes.Contains(type) ? type : type + "?";
            writer.Line().Open($"private sealed class PacketFormatter{i} : global::MemoryPack.MemoryPackFormatter<{type}>")
                .Line($"public override void Serialize<TBufferWriter>(ref global::MemoryPack.MemoryPackWriter<TBufferWriter> writer, scoped ref {nullable} value)")
                .Line("    => writer.WritePackable(value);")
                .Line($"public override void Deserialize(ref global::MemoryPack.MemoryPackReader reader, scoped ref {nullable} value)")
                .Line("    => reader.ReadPackable(ref value);").Close();
        }
        return writer.Close().Close().ToString();
    }
}
