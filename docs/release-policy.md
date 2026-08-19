# Release policy

IqChannelizer is MIT-licensed. The library is packable as a managed-only NuGet:
the package carries `LICENSE`, `README.md`, and the managed `IqChannelizer.dll`,
but no FFTW binary or header.

The development build copies the pinned FFTW 3.3.5 Windows x64 DLL beside the managed
assembly so local tests and benchmarks are reproducible. This is expected. The DLL
and header are explicitly excluded from NuGet; consumers of the future package must
supply a compatible native FFTW runtime independently. FFTW's separate GPL/commercial
terms still apply to anyone who redistributes that native runtime.

Every release must run:

```powershell
./build/verify-package.ps1 -FftwRuntimePath <path-to-independently-supplied-libfftw3f-3.dll>
```

The verification:

1. verify the `.nupkg` contains the MIT license and no FFTW DLL or header;
2. document how consumers provide and resolve the compatible native runtime;
3. test the package on a clean Windows x64 environment with an independently supplied DLL;
4. retain a package-content assertion in release automation; and
5. runs a real FDC request from the clean consumer.

The retained result is `artifacts/package-validation.json`. Application-local builds alone
are not evidence that these distribution gates have been met.
No realtime-performance claim is permitted until a BenchmarkDotNet profile has been
recorded on the named target hardware.
