using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal static class ScalarDft
{
    public static void Forward(ReadOnlySpan<ComplexF> input, Span<ComplexF> output) => Transform(input, output, -1);

    public static void Backward(ReadOnlySpan<ComplexF> input, Span<ComplexF> output) => Transform(input, output, 1);

    private static void Transform(ReadOnlySpan<ComplexF> input, Span<ComplexF> output, int sign)
    {
        if (output.Length != input.Length)
        {
            throw new ArgumentException("DFT input and output lengths must match.");
        }

        for (var k = 0; k < output.Length; k++)
        {
            double real = 0;
            double imaginary = 0;
            for (var n = 0; n < input.Length; n++)
            {
                var phase = sign * 2 * Math.PI * k * n / input.Length;
                var cosine = Math.Cos(phase);
                var sine = Math.Sin(phase);
                real += (input[n].Real * cosine) - (input[n].Imaginary * sine);
                imaginary += (input[n].Real * sine) + (input[n].Imaginary * cosine);
            }

            output[k] = new ComplexF((float)real, (float)imaginary);
        }
    }
}
