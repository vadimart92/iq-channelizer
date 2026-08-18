namespace IqChannelizer.Dsp;

public readonly record struct AliasedResponseResult(
    int AliasImageCount,
    double WorstAliasMagnitude,
    double WorstAliasAttenuationDb,
    double WorstBasebandFrequencyHz);

public static class AliasedResponseEvaluator
{
    public static AliasedResponseResult EvaluateConservative(
        DenseFrequencyResponse response,
        int decimationFactor,
        double outputPassbandEdgeHz,
        int evaluationPointCount)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (decimationFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decimationFactor));
        }

        var outputSampleRate = response.SampleRateHz / decimationFactor;
        if (!double.IsFinite(outputPassbandEdgeHz) ||
            outputPassbandEdgeHz < 0 ||
            outputPassbandEdgeHz >= outputSampleRate / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(outputPassbandEdgeHz));
        }

        if (evaluationPointCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(evaluationPointCount));
        }

        var responseDrivenPointCount = outputPassbandEdgeHz == 0
            ? 2
            : checked((int)Math.Ceiling((2 * outputPassbandEdgeHz / response.FrequencyStepHz) * 2) + 1);
        evaluationPointCount = Math.Min(16_385, Math.Max(evaluationPointCount, responseDrivenPointCount));

        double worstMagnitude = 0;
        double worstFrequency = 0;
        for (var point = 0; point < evaluationPointCount; point++)
        {
            var basebandFrequency = -outputPassbandEdgeHz +
                                    (point * 2 * outputPassbandEdgeHz / (evaluationPointCount - 1));
            double foldedMagnitude = 0;
            for (var image = 1; image < decimationFactor; image++)
            {
                // Sum magnitudes so acceptance never relies on phase cancellation between alias images.
                foldedMagnitude += response.ConservativeMagnitudeAt(basebandFrequency + (image * outputSampleRate));
            }

            if (foldedMagnitude > worstMagnitude)
            {
                worstMagnitude = foldedMagnitude;
                worstFrequency = basebandFrequency;
            }
        }

        var attenuation = worstMagnitude > 0
            ? -20 * Math.Log10(worstMagnitude)
            : double.PositiveInfinity;
        return new AliasedResponseResult(decimationFactor - 1, worstMagnitude, attenuation, worstFrequency);
    }
}
