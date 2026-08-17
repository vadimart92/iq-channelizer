using System.Runtime.InteropServices;

namespace IqChannelizer.Fftw;

internal static partial class FftwNative
{
    internal const int Forward = -1;
    internal const int Backward = 1;
    internal const uint Measure = 0;
    internal const uint Estimate = 1U << 6;

    internal const string LibraryName = "libfftw3f-3";

    [LibraryImport(LibraryName, EntryPoint = "fftwf_malloc")]
    internal static partial nint Malloc(nuint byteCount);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_free")]
    internal static partial void Free(nint pointer);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_plan_dft_1d")]
    internal static partial nint PlanDft1D(
        int length,
        nint input,
        nint output,
        int direction,
        uint flags);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_plan_many_dft")]
    internal static unsafe partial nint PlanManyDft(
        int rank,
        int* lengths,
        int batchCount,
        nint input,
        int* inputEmbed,
        int inputStride,
        int inputDistance,
        nint output,
        int* outputEmbed,
        int outputStride,
        int outputDistance,
        int direction,
        uint flags);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_execute")]
    internal static partial void Execute(nint plan);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_execute_dft")]
    internal static partial void ExecuteDft(nint plan, nint input, nint output);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_destroy_plan")]
    internal static partial void DestroyPlan(nint plan);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_alignment_of")]
    internal static partial int AlignmentOf(nint pointer);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_export_wisdom")]
    internal static partial void ExportWisdom(nint writeCharacter, nint context);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_import_wisdom_from_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ImportWisdomFromString(string wisdom);

    [LibraryImport(LibraryName, EntryPoint = "fftwf_forget_wisdom")]
    internal static partial void ForgetWisdom();
}
