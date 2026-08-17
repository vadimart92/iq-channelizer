using System.Runtime.CompilerServices;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Tests;

public sealed class ContractTests
{
    [Test]
    public void ComplexFHasFftwCompatibleSize() => Assert.That(Unsafe.SizeOf<ComplexF>(), Is.EqualTo(8));

    [Test]
    public void RationalOffsetsAreNormalized()
    {
        var value = new RationalSampleOffset(-6, -8);
        Assert.That(value, Is.EqualTo(new RationalSampleOffset(3, 4)));
    }

    [Test]
    public void DuplicateChannelIdsAreRejected()
    {
        var channels = new[] { Channel(7, 0), Channel(7, 100) };
        Assert.That(() => ChannelizerFactory.Create(Request(ChannelizerStrategy.Fdc, channels)), Throws.ArgumentException);
    }

    [Test]
    public void AutoIsExplicitlyUnsupported()
    {
        Assert.That(() => ChannelizerFactory.Create(Request(ChannelizerStrategy.Auto, [Channel(1, 0)])), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void ForcedSimdIsRejectedForScalarFoundation()
    {
        var request = Request(ChannelizerStrategy.Fdc, [Channel(1, 0)]) with
        {
            Hints = new ChannelizerImplementationHints(Simd: SimdPreference.Avx2)
        };
        Assert.That(() => ChannelizerFactory.Create(request), Throws.TypeOf<NotSupportedException>());
    }

    internal static ChannelizerRequest Request(ChannelizerStrategy strategy, IReadOnlyList<ChannelRequest> channels) =>
        new(1024, channels, strategy, new InputBlockConstraints(16, 32));

    internal static ChannelRequest Channel(int id, double center) => new(id, center, 20, 10);
}
