using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal readonly record struct SimdCapabilities(bool Avx2Fma);

internal static class SimdBackendResolver
{
    public static SimdPreference Resolve(SimdPreference preference) =>
        Resolve(preference, DetectCapabilities());

    internal static SimdPreference Resolve(SimdPreference preference, SimdCapabilities capabilities) =>
        preference switch
        {
            SimdPreference.Auto => capabilities.Avx2Fma ? SimdPreference.Avx2 : SimdPreference.Scalar,
            SimdPreference.Scalar => SimdPreference.Scalar,
            SimdPreference.Avx2 when capabilities.Avx2Fma => SimdPreference.Avx2,
            SimdPreference.Avx2 => throw new PlatformNotSupportedException(
                "The forced AVX2 backend requires both AVX2 and FMA support."),
            SimdPreference.Avx512 => throw new NotSupportedException(
                "The AVX-512 backend remains disabled until an AVX2 comparison demonstrates an end-to-end benefit."),
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };

    private static SimdCapabilities DetectCapabilities() => new(Avx2.IsSupported && Fma.IsSupported);
}
