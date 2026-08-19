using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;

namespace IqChannelizer.Fdc;

internal sealed record PartitionedFdcChannelDesign(
    ComplexF[][] PartitionSpectralWindows,
    AliasedResponseResult AliasedResponse);

internal static class PartitionedFdcFilterDesign
{
    public static PartitionedFdcChannelDesign Complete(
        ChannelRequest channel,
        float[] paddedTaps,
        double inputSampleRateHz,
        int decimation,
        int partitionLength,
        int coarseBin,
        double residualFrequencyHz)
    {
        ArgumentNullException.ThrowIfNull(paddedTaps);
        if (partitionLength <= 0 || partitionLength % decimation != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionLength));
        }

        var transformLength = checked(2 * partitionLength);
        var shortLength = transformLength / decimation;
        var partitionCount = checked((paddedTaps.Length + partitionLength - 1) / partitionLength);
        var windows = new ComplexF[partitionCount][];
        var radiansPerSample = 2 * Math.PI * residualFrequencyHz / inputSampleRateHz;

        using var responsePlan = new FftwComplexPlan(transformLength, 1, FftwNative.Forward);
        for (var partitionIndex = 0; partitionIndex < partitionCount; partitionIndex++)
        {
            var input = responsePlan.WritableInput;
            input.Clear();
            var firstTap = checked(partitionIndex * partitionLength);
            var tapCount = Math.Min(partitionLength, paddedTaps.Length - firstTap);
            // Every input spectrum uses a local origin one partition earlier than
            // the preceding ring entry. Since N == 2L, restoring that coarse-bin
            // origin is exactly (-1)^(coarseBin * partitionIndex).
            var partitionOriginScale = (coarseBin & 1) == 0 || (partitionIndex & 1) == 0
                ? 1f
                : -1f;
            if (residualFrequencyHz == 0)
            {
                for (var localTap = 0; localTap < tapCount; localTap++)
                {
                    input[localTap] = new ComplexF(
                        paddedTaps[firstTap + localTap] * partitionOriginScale,
                        0);
                }
            }
            else
            {
                for (var localTap = 0; localTap < tapCount; localTap++)
                {
                    var absoluteTap = firstTap + localTap;
                    var (sine, cosine) = Math.SinCos(radiansPerSample * absoluteTap);
                    var tap = paddedTaps[absoluteTap] * partitionOriginScale;
                    input[localTap] = new ComplexF(
                        (float)(tap * cosine),
                        (float)(tap * sine));
                }
            }

            responsePlan.ExecuteFromInput();
            var spectrum = responsePlan.Output;
            // Store D alias bands in short-IFFT order. Folding those bands into
            // N/D output bins is the exact frequency-domain form of decimation;
            // retaining only the base band would be inaccurate for individual
            // short FIR partitions even when the complete FIR is narrow-band.
            var window = new ComplexF[transformLength];
            for (var alias = 0; alias < decimation; alias++)
            {
                var windowOffset = alias * shortLength;
                var spectralOffset = alias * shortLength;
                for (var shortBin = 0; shortBin < shortLength; shortBin++)
                {
                    var signedOffset = shortBin <= shortLength / 2
                        ? shortBin
                        : shortBin - shortLength;
                    window[windowOffset + shortBin] = spectrum[
                        FrequencyBinMath.Mod(signedOffset + spectralOffset, transformLength)];
                }
            }

            windows[partitionIndex] = window;
        }

        return new PartitionedFdcChannelDesign(
            windows,
            FdcFilterDesign.ValidateAliasedResponse(channel, paddedTaps, inputSampleRateHz, decimation));
    }
}
