using System.Runtime.InteropServices;

namespace IqChannelizer.Abstractions;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ComplexF(float real, float imaginary)
{
    public float Real = real;
    public float Imaginary = imaginary;

    public readonly float Magnitude => MathF.Sqrt((Real * Real) + (Imaginary * Imaginary));

    public static ComplexF operator +(ComplexF left, ComplexF right) =>
        new(left.Real + right.Real, left.Imaginary + right.Imaginary);

    public static ComplexF operator *(ComplexF value, float scale) =>
        new(value.Real * scale, value.Imaginary * scale);

    public static ComplexF operator *(ComplexF left, ComplexF right) =>
        new(
            left.Real * right.Real - left.Imaginary * right.Imaginary,
            left.Real * right.Imaginary + left.Imaginary * right.Real);

    internal static ComplexF FromPolar(double phase) =>
        new((float)Math.Cos(phase), (float)Math.Sin(phase));
}
