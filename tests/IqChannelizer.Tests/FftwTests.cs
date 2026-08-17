using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;

namespace IqChannelizer.Tests;

[NonParallelizable]
public sealed class FftwTests
{
    [TestCase(FftwNative.Forward)]
    [TestCase(FftwNative.Backward)]
    public void SingleTransformMatchesScalarReference(int direction)
    {
        ComplexF[] input =
        [
            new(1, 2), new(-3, 1), new(0.5f, -2), new(4, 0),
            new(-1, -1), new(2, 3), new(0, 0.25f), new(-2, 1)
        ];
        var expected = new ComplexF[input.Length];
        var actual = new ComplexF[input.Length];
        if (direction == FftwNative.Forward)
        {
            ScalarDft.Forward(input, expected);
        }
        else
        {
            ScalarDft.Backward(input, expected);
        }

        using var plan = new FftwComplexPlan(input.Length, 1, direction);
        plan.Execute(input, actual);

        AssertComplexEqual(expected, actual, 2e-5f);
    }

    [Test]
    public void BatchedBackwardTransformMatchesIndependentTransforms()
    {
        const int length = 4;
        const int batches = 3;
        var input = Enumerable.Range(0, length * batches)
            .Select(index => new ComplexF(index - 2, (index % 3) - 1))
            .ToArray();
        var expected = new ComplexF[input.Length];
        for (var batch = 0; batch < batches; batch++)
        {
            ScalarDft.Backward(
                input.AsSpan(batch * length, length),
                expected.AsSpan(batch * length, length));
        }

        var actual = new ComplexF[input.Length];
        using var plan = new FftwComplexPlan(length, batches, FftwNative.Backward);
        plan.Execute(input, actual);

        AssertComplexEqual(expected, actual, 2e-5f);
    }

    [Test]
    public void ForwardThenBackwardUsesFftwUnnormalizedConvention()
    {
        ComplexF[] input = [new(1, 0), new(2, -1), new(-3, 2), new(0.5f, 4)];
        var spectrum = new ComplexF[input.Length];
        var restored = new ComplexF[input.Length];
        using var forward = new FftwComplexPlan(input.Length, 1, FftwNative.Forward);
        using var backward = new FftwComplexPlan(input.Length, 1, FftwNative.Backward);

        forward.Execute(input, spectrum);
        backward.Execute(spectrum, restored);

        for (var index = 0; index < input.Length; index++)
        {
            Assert.That(restored[index].Real, Is.EqualTo(input[index].Real * input.Length).Within(2e-5));
            Assert.That(restored[index].Imaginary, Is.EqualTo(input[index].Imaginary * input.Length).Within(2e-5));
        }
    }

    [Test]
    public void FftwOwnedBuffersAreAtLeastSixteenByteAligned()
    {
        using var plan = new FftwComplexPlan(16, 2, FftwNative.Forward);
        Assert.That(plan.InputAddress % 16, Is.EqualTo((nuint)0));
        Assert.That(plan.OutputAddress % 16, Is.EqualTo((nuint)0));
    }

    [Test]
    public void RepeatedExecutionDoesNotAllocateManagedMemory()
    {
        var input = new ComplexF[32];
        var output = new ComplexF[32];
        using var plan = new FftwComplexPlan(32, 1, FftwNative.Forward);
        plan.Execute(input, output);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 100; iteration++)
        {
            plan.Execute(input, output);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private static void AssertComplexEqual(ReadOnlySpan<ComplexF> expected, ReadOnlySpan<ComplexF> actual, float tolerance)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.That(actual[index].Real, Is.EqualTo(expected[index].Real).Within(tolerance), $"real[{index}]");
            Assert.That(actual[index].Imaginary, Is.EqualTo(expected[index].Imaginary).Within(tolerance), $"imaginary[{index}]");
        }
    }
}
