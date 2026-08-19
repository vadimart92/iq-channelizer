# Release policy

IqChannelizer is MIT-licensed. The repository is currently a source, test, and
benchmark deliverable; the library remains marked `IsPackable=false` until its
managed-only NuGet layout is implemented and verified.

The development build copies the pinned FFTW 3.3.5 Windows x64 DLL beside the managed
assembly so local tests and benchmarks are reproducible. This is expected. The DLL
and header are explicitly excluded from NuGet; consumers of the future package must
supply a compatible native FFTW runtime independently. FFTW's separate GPL/commercial
terms still apply to anyone who redistributes that native runtime.

Before enabling packaging, a release change must:

1. verify the `.nupkg` contains the MIT license and no FFTW DLL or header;
2. document how consumers provide and resolve the compatible native runtime;
3. test the package on a clean Windows x64 environment with an independently supplied DLL;
4. retain a package-content assertion in release automation; and
5. remove `IsPackable=false` only in the same reviewed change.

Application-local builds are not evidence that these distribution gates have been met.
No realtime-performance claim is permitted until a BenchmarkDotNet profile has been
recorded on the named target hardware.
