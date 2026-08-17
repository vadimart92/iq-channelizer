using System.Reflection;
using System.Runtime.InteropServices;

namespace IqChannelizer.Fftw;

internal sealed record FftwRuntimeInfo(
    string LibraryName,
    string Version,
    Architecture ProcessArchitecture,
    string ThreadingMode);

internal static class FftwRuntime
{
    private static readonly Lazy<FftwRuntimeInfo> RuntimeInfo = new(LoadAndValidate, LazyThreadSafetyMode.ExecutionAndPublication);

    public static FftwRuntimeInfo Info => RuntimeInfo.Value;

    internal static void ValidatePlatform(bool isWindows, Architecture architecture)
    {
        if (!isWindows || architecture != Architecture.X64 || IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException(
                $"The bundled FFTW runtime supports only 64-bit Windows x64 processes. " +
                $"Current platform: OS={(isWindows ? "Windows" : "non-Windows")}, architecture={architecture}, pointerSize={IntPtr.Size}.");
        }
    }

    private static FftwRuntimeInfo LoadAndValidate()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        ValidatePlatform(OperatingSystem.IsWindows(), architecture);

        nint handle;
        try
        {
            if (!NativeLibrary.TryLoad(
                    FftwNative.LibraryName,
                    typeof(FftwRuntime).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies,
                    out handle))
            {
                throw MissingLibraryException();
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new PlatformNotSupportedException(
                $"The bundled {FftwNative.LibraryName} is not a valid Windows x64 native library. " +
                "Ensure the application is published for win-x64 and the DLL is copied beside the managed assembly.",
                exception);
        }

        try
        {
            string[] requiredExports =
            [
                "fftwf_malloc", "fftwf_free", "fftwf_plan_dft_1d", "fftwf_plan_many_dft",
                "fftwf_execute_dft", "fftwf_destroy_plan", "fftwf_alignment_of",
                "fftwf_export_wisdom", "fftwf_import_wisdom_from_string", "fftwf_forget_wisdom"
            ];
            foreach (var export in requiredExports)
            {
                if (!NativeLibrary.TryGetExport(handle, export, out _))
                {
                    throw new EntryPointNotFoundException(
                        $"The bundled {FftwNative.LibraryName} does not export required symbol '{export}'. " +
                        "Replace it with the documented single-precision FFTW build.");
                }
            }

            var versionAddress = NativeLibrary.GetExport(handle, "fftwf_version");
            var version = Marshal.PtrToStringAnsi(versionAddress);
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidOperationException($"Could not read fftwf_version from {FftwNative.LibraryName}.");
            }

            return new FftwRuntimeInfo(FftwNative.LibraryName, version, architecture, "SingleThread");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static DllNotFoundException MissingLibraryException()
    {
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        return new DllNotFoundException(
            $"Required bundled FFTW library '{FftwNative.LibraryName}' could not be loaded. " +
            $"Expected a Windows x64 DLL beside the application or managed assembly (searched from '{assemblyDirectory}'). " +
            "Verify that native assets were copied during build/publish and that the process architecture is x64.");
    }
}
