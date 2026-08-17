using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;

namespace IqChannelizer.Tests;

public sealed class PfbMathTests
{
    [TestCase(0L)]
    [TestCase(3L)]
    [TestCase(17L)]
    [TestCase(-5L)]
    public void CircularShiftMatchesExplicitFrameCorrection(long anchor)
    {
        ComplexF[] input = [new(1, 2), new(-3, 1), new(0.5f, -2), new(4, 0)];
        var expected = new ComplexF[input.Length];
        var shifted = new ComplexF[input.Length];
        var actual = new ComplexF[input.Length];

        PfbMath.ApplyExplicitCorrection(input, anchor, expected);
        PfbMath.TransformWithCircularShift(input, anchor, shifted, actual);

        for (var i = 0; i < actual.Length; i++)
        {
            Assert.That(actual[i].Real, Is.EqualTo(expected[i].Real).Within(1e-5));
            Assert.That(actual[i].Imaginary, Is.EqualTo(expected[i].Imaginary).Within(1e-5));
        }
    }

    [Test]
    public void HalfHopFlipsOddBinCorrectionOnly()
    {
        const int k = 8;
        const int hop = k / 2;
        for (var bin = 0; bin < k; bin++)
        {
            var first = ComplexF.FromPolar(-2 * Math.PI * bin * 3 / k);
            var next = ComplexF.FromPolar(-2 * Math.PI * bin * (3 + hop) / k);
            var ratio = next * new ComplexF(first.Real, -first.Imaginary);
            Assert.That(ratio.Real, Is.EqualTo((bin & 1) == 0 ? 1 : -1).Within(1e-5));
            Assert.That(ratio.Imaginary, Is.EqualTo(0).Within(1e-5));
        }
    }
}
