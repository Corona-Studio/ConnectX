using System.Net;
using ConnectX.Shared.Formatters;
using ConnectX.Shared.Messages.P2P;
using MemoryPack;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class P2PContextSerializationTests
{
    [Test]
    public void ClosedContextHierarchyPreservesWireDiscriminatorsAndAddressFields()
    {
        MemoryPackFormatterProvider.Register(new IpAddressFormatter());
        var address = new P2PConContextInit
        {
            PublicAddress = IPAddress.Parse("203.0.113.17"),
            PublicPort = 25565,
            CurrentUsedPort = 32123,
            Diff = 8,
            SameIfNotConflict = true
        };
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        (P2PConContext Context, byte Tag)[] cases =
        [
            (new P2PConAccept(42, first, second, address), 0),
            (new P2PConReady(second, 123456, address) { Bargain = 42 }, 1),
            (new P2PConRequest(42, second, first, address), 2)
        ];

        foreach (var (context, tag) in cases)
        {
            var bytes = MemoryPackSerializer.Serialize(context);
            var restored = MemoryPackSerializer.Deserialize<P2PConContext>(bytes)!;
            Assert.Multiple(() =>
            {
                Assert.That(bytes[0], Is.EqualTo(tag));
                Assert.That(restored.GetType(), Is.EqualTo(context.GetType()));
                Assert.That(restored.PublicAddress, Is.EqualTo(address.PublicAddress));
                Assert.That(restored.PublicPort, Is.EqualTo(address.PublicPort));
                Assert.That(restored.CurrentUsedPort, Is.EqualTo(address.CurrentUsedPort));
                Assert.That(restored.Diff, Is.EqualTo(address.Diff));
                Assert.That(restored.SameIfNotConflict, Is.True);
            });
        }
    }

    [Test]
    public void ClosedContextHierarchyPreservesNullWireRepresentation()
    {
        var bytes = MemoryPackSerializer.Serialize<P2PConContext>(null);
        Assert.That(bytes, Is.EqualTo(new byte[] { 255 }));
        Assert.That(MemoryPackSerializer.Deserialize<P2PConContext>(bytes), Is.Null);
    }
}
