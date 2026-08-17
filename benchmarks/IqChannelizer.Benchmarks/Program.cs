using IqChannelizer;
using IqChannelizer.Abstractions;

Console.WriteLine("IqChannelizer benchmark host skeleton");
Console.WriteLine("Current backend: FFTW single-precision C2C with scalar surrounding DSP kernels.");
Console.WriteLine("BenchmarkDotNet profiles arrive with the SIMD/performance phase.");
Console.WriteLine($"ComplexF size contract: {System.Runtime.CompilerServices.Unsafe.SizeOf<ComplexF>()} bytes");
