using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class PfbAlgebraTests
{
    [Test]
    public void PhaseFirMatchesHandCheckablePGreaterThanOneEquation()
    {
        const int fftSize = 4;
        const long absoluteStart = 10;
        const long anchor = 17;
        var input = Enumerable.Range(0, 8)
            .Select(index => new ComplexF(index + 1, -(index + 1) * 0.25f))
            .ToArray();
        float[] prototype = [0.02f, 0.04f, 0.06f, 0.08f, 0.1f, 0.12f, 0.14f, 0.16f];
        var actual = new ComplexF[fftSize];

        PfbMath.ComputePhaseVector(input, absoluteStart, anchor, prototype, fftSize, actual);

        for (var phase = 0; phase < fftSize; phase++)
        {
            var expected = (input[7 - phase] * prototype[phase]) +
                           (input[3 - phase] * prototype[phase + fftSize]);
            AssertComplex(expected, actual[phase], 1e-7);
        }
    }

    [TestCase(4)]
    [TestCase(2)]
    [TestCase(3)]
    public void PhaseVectorExplicitCorrectionAndCircularShiftMatchIndependentDirectFirDft(int hopSize)
    {
        const int fftSize = 4;
        const int tapsPerPhase = 3;
        const int frames = 3;
        const long firstNew = 17;
        var prototype = Enumerable.Range(1, fftSize * tapsPerPhase)
            .Select(index => (float)index)
            .ToArray();
        var sum = prototype.Sum(value => (double)value);
        for (var index = 0; index < prototype.Length; index++)
        {
            prototype[index] /= (float)sum;
        }

        var history = prototype.Length - 1;
        var spanStart = firstNew - history;
        var input = Enumerable.Range(0, history + (frames * hopSize))
            .Select(index => ComplexF.FromPolar(0.173 * (spanStart + index)) * (0.5f + (0.01f * index)))
            .ToArray();
        var phaseVector = new ComplexF[fftSize];
        var explicitlyCorrected = new ComplexF[fftSize];
        var shifted = new ComplexF[fftSize];
        var shiftedOutput = new ComplexF[fftSize];

        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = firstNew + ((long)(frame + 1) * hopSize) - 1;
            PfbMath.ComputePhaseVector(input, spanStart, anchor, prototype, fftSize, phaseVector);
            PfbMath.ApplyExplicitCorrection(phaseVector, anchor, explicitlyCorrected);
            PfbMath.TransformWithCircularShift(phaseVector, anchor, shifted, shiftedOutput);
            var direct = PfbDirectReference.Evaluate(input, spanStart, anchor, prototype, fftSize);

            for (var bin = 0; bin < fftSize; bin++)
            {
                AssertComplex(explicitlyCorrected[bin], shiftedOutput[bin], 2e-5);
                Assert.That(
                    new Complex(explicitlyCorrected[bin].Real, explicitlyCorrected[bin].Imaginary),
                    Is.EqualTo(direct[bin]).Using(ComplexWithin(2e-5)),
                    $"frame {frame}, signed bin {(bin <= fftSize / 2 ? bin : bin - fftSize)}");
            }
        }
    }

    [Test]
    public void ProductionPfbUsesConservativePGreaterThanOnePrototypeAcrossPartitions()
    {
        var channel = ContractTests.Channel(5, 128) with { PreferredOutputSampleRateHz = 256 };
        var request = ContractTests.Request(ChannelizerStrategy.Pfb, [channel]) with
        {
            InputBlocks = new InputBlockConstraints(8, 8),
            Hints = new ChannelizerImplementationHints(
                PfbFftSize: 8,
                PfbHopSize: 4,
                PfbFramesPerBatch: 2,
                Simd: SimdPreference.Scalar)
        };
        using var engine = ChannelizerFactory.Create(request);
        var fine = PfbFineStageDesigner.Design(channel, 1024d / 4, 2);
        var warmupBlocks = 2 + ((fine.Taps.Length - 1 + 1) / 2);
        const long initialFirstNew = 29;
        var sink = new TestSink();
        for (var block = 0; block < warmupBlocks + 2; block++)
        {
            var firstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var input = DeterministicSignals.Tone(
                engine.InputRequirements.InputSize,
                128,
                1024,
                firstNew - engine.InputRequirements.HistorySize,
                0.75,
                0.3);
            engine.Process(input, firstNew, block >= warmupBlocks ? sink : new TestSink());
        }

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.TapsPerPhase, Is.GreaterThan(1));
            Assert.That(engine.Plan.FilterDesignMode, Is.EqualTo("KaiserConservative"));
            Assert.That(engine.InputRequirements.HistorySize,
                Is.EqualTo((engine.Plan.TapsPerPhase!.Value * 8) - 1));
            Assert.That(sink.Blocks, Has.Count.EqualTo(2));
            Assert.That(sink.Blocks.SelectMany(block => block.Samples)
                .All(sample => Math.Abs(sample.Magnitude - 0.75) < 4e-4), Is.True);
        });
    }

    private static IEqualityComparer<Complex> ComplexWithin(double tolerance) =>
        new ComplexToleranceComparer(tolerance);

    private static void AssertComplex(ComplexF expected, ComplexF actual, double tolerance)
    {
        Assert.That(actual.Real, Is.EqualTo(expected.Real).Within(tolerance));
        Assert.That(actual.Imaginary, Is.EqualTo(expected.Imaginary).Within(tolerance));
    }

    private sealed class ComplexToleranceComparer(double tolerance) : IEqualityComparer<Complex>
    {
        public bool Equals(Complex left, Complex right) => (left - right).Magnitude <= tolerance;
        public int GetHashCode(Complex value) => value.GetHashCode();
    }
}
