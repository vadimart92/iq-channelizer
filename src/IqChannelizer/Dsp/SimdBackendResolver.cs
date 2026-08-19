using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal readonly record struct SimdCapabilities(bool Avx2Fma, bool Avx512F);

internal static class SimdBackendResolver
{
    public static SimdPreference Resolve(SimdPreference preference, bool autoPreferAvx512) =>
        Resolve(preference, DetectCapabilities(), autoPreferAvx512);

    internal static SimdPreference Resolve(
        SimdPreference preference,
        SimdCapabilities capabilities,
        bool autoPreferAvx512 = true) =>
        preference switch
        {
            SimdPreference.Auto => autoPreferAvx512 && capabilities.Avx512F
                ? SimdPreference.Avx512
                : capabilities.Avx2Fma
                    ? SimdPreference.Avx2
                    : capabilities.Avx512F ? SimdPreference.Avx512 : SimdPreference.Scalar,
            SimdPreference.Scalar => SimdPreference.Scalar,
            SimdPreference.Avx2 when capabilities.Avx2Fma => SimdPreference.Avx2,
            SimdPreference.Avx2 => throw new PlatformNotSupportedException(
                "The forced AVX2 backend requires both AVX2 and FMA support."),
            SimdPreference.Avx512 when capabilities.Avx512F => SimdPreference.Avx512,
            SimdPreference.Avx512 => throw new PlatformNotSupportedException(
                "The forced AVX-512 backend requires AVX-512F support from both the CPU and operating system."),
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };

    private static SimdCapabilities DetectCapabilities() =>
        new(Avx2.IsSupported && Fma.IsSupported, Avx512F.IsSupported);
}
