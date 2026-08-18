using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;
using System.Runtime.InteropServices;

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
    public void WritableNativeInputExecutesWithoutManagedInputStaging()
    {
        ComplexF[] input = [new(1, 2), new(-3, 1), new(0.5f, -2), new(4, 0)];
        var expected = new ComplexF[input.Length];
        var actual = new ComplexF[input.Length];
        ScalarDft.Backward(input, expected);
        using var plan = new FftwComplexPlan(input.Length, 1, FftwNative.Backward);

        input.CopyTo(plan.WritableInput);
        plan.ExecuteFromInput(actual);

        AssertComplexEqual(expected, actual, 2e-5f);
    }

    [TestCase(3, 1)]
    [TestCase(5, 2)]
    [TestCase(6, 3)]
    [TestCase(10, 2)]
    [TestCase(15, 4)]
    public void ContiguousPlanManySupportsSmoothCompositeLayouts(int length, int batches)
    {
        var input = Enumerable.Range(0, length * batches)
            .Select(index => new ComplexF((index % 7) - 3, (index % 5) - 2))
            .ToArray();
        var expected = new ComplexF[input.Length];
        for (var batch = 0; batch < batches; batch++)
        {
            ScalarDft.Forward(input.AsSpan(batch * length, length), expected.AsSpan(batch * length, length));
        }

        var actual = new ComplexF[input.Length];
        using var plan = new FftwComplexPlan(length, batches, FftwNative.Forward);
        plan.Execute(input, actual);

        AssertComplexEqual(expected, actual, 1e-4f);
    }

    [Test]
    public void InPlaceTransformMatchesScalarReference()
    {
        ComplexF[] input = [new(1, 2), new(-2, 0), new(3, -1), new(0.5f, 4), new(-1, 1)];
        var expected = new ComplexF[input.Length];
        ScalarDft.Forward(input, expected);
        var actual = new ComplexF[input.Length];

        using var plan = new FftwComplexPlan(input.Length, 1, FftwNative.Forward, inPlace: true);
        plan.Execute(input, actual);

        Assert.That(plan.IsInPlace, Is.True);
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
        Assert.That(plan.InputAlignmentClass, Is.EqualTo(plan.OutputAlignmentClass));
    }

    [Test]
    public void ReusableNativeBufferOwnsAlignedWritableStorage()
    {
        using var buffer = new FftwAlignedBuffer<ComplexF>(17);
        buffer.Span[3] = new ComplexF(4, -2);

        Assert.Multiple(() =>
        {
            Assert.That(buffer.ByteCount, Is.EqualTo((nuint)(17 * 8)));
            Assert.That(buffer.Address % 16, Is.EqualTo((nuint)0));
            Assert.That(buffer.ReadOnlySpan[3], Is.EqualTo(new ComplexF(4, -2)));
        });
    }

    [Test]
    public void RuntimeReportsBundledVersionAndActionablePlatformContract()
    {
        var info = FftwRuntime.Info;
        Assert.Multiple(() =>
        {
            Assert.That(info.Version, Does.StartWith("fftw-3.3.5"));
            Assert.That(info.LibraryName, Is.EqualTo("libfftw3f-3"));
            Assert.That(info.ProcessArchitecture, Is.EqualTo(Architecture.X64));
            Assert.That(info.ThreadingMode, Is.EqualTo("SingleThread"));
        });

        Assert.That(
            () => FftwRuntime.ValidatePlatform(isWindows: false, Architecture.X64),
            Throws.TypeOf<PlatformNotSupportedException>().With.Message.Contains("Windows x64"));
        Assert.That(
            () => FftwRuntime.ValidatePlatform(isWindows: true, Architecture.Arm64),
            Throws.TypeOf<PlatformNotSupportedException>().With.Message.Contains("x64"));
    }

    [Test]
    public void UnsupportedThreadCountIsRejectedByPlanningPolicy()
    {
        Assert.That(
            () => _ = new FftwPlanningPolicy(FftwPlanningMode.Estimate, 2),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("threads DLL"));
    }

    [Test]
    public void DisposedNativeOwnersRejectFurtherUse()
    {
        var buffer = new FftwAlignedBuffer<ComplexF>(4);
        buffer.Dispose();
        Assert.That(() => _ = buffer.Pointer, Throws.TypeOf<ObjectDisposedException>());

        var plan = new FftwComplexPlan(4, 1, FftwNative.Forward);
        plan.Dispose();
        Assert.That(
            () => plan.Execute(new ComplexF[4], new ComplexF[4]),
            Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void WisdomCanBeExportedForgottenAndImported()
    {
        FftwPlanCache.ClearIdle();
        using (var plan = new FftwComplexPlan(12, 2, FftwNative.Forward))
        {
            plan.Execute(new ComplexF[24], new ComplexF[24]);
        }

        var wisdom = FftwWisdom.ExportToString();
        Assert.That(wisdom, Is.Not.Empty);
        FftwPlanCache.ClearIdle();
        FftwWisdom.Forget();
        Assert.That(FftwWisdom.ImportFromString(wisdom), Is.True);
    }

    [Test]
    public void EquivalentPlansReuseCachedNativePlan()
    {
        FftwPlanCache.ClearIdle();
        var before = FftwPlanCache.CreatedPlanCount;
        using (var first = new FftwComplexPlan(9, 2, FftwNative.Forward))
        using (var second = new FftwComplexPlan(9, 2, FftwNative.Forward))
        {
            Assert.That(first.NativePlanAddress, Is.EqualTo(second.NativePlanAddress));
        }

        Assert.That(FftwPlanCache.CreatedPlanCount - before, Is.EqualTo(1));
        Assert.That(FftwPlanCache.IdlePlanCount, Is.GreaterThanOrEqualTo(1));
        FftwPlanCache.ClearIdle();
    }

    [Test]
    public void RepeatedCreateDisposeStressLeavesNoActiveLeases()
    {
        FftwPlanCache.ClearIdle();
        for (var iteration = 0; iteration < 200; iteration++)
        {
            using var plan = new FftwComplexPlan(7 + (iteration % 4), 1 + (iteration % 3), FftwNative.Backward);
            plan.Execute(new ComplexF[plan.ElementCount], new ComplexF[plan.ElementCount]);
        }

        Assert.That(FftwPlanCache.ActiveLeaseCount, Is.Zero);
        FftwPlanCache.ClearIdle();
        Assert.That(FftwPlanCache.CachedPlanCount, Is.Zero);
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
