using IqChannelizer.Abstractions;

namespace IqChannelizer.Tests;

internal sealed class TestSink : IChannelOutputSink
{
    public List<(int ChannelId, ComplexF[] Samples)> Blocks { get; } = [];

    public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
        Blocks.Add((channelId, samples.ToArray()));
}
