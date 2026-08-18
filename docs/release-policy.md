# Release policy

The repository is currently a source, test, and benchmark deliverable. The
`IqChannelizer` library project is intentionally marked `IsPackable=false`; a NuGet
package or other redistributable binary release is not an approved output yet.

The development build copies the pinned FFTW 3.3.5 Windows x64 DLL beside the managed
assembly so local tests and benchmarks are reproducible. Distributing that combined
output requires the release owner to choose and record one of the licensing paths in
[`fftw-runtime.md`](fftw-runtime.md): GPL-compatible distribution, an applicable
commercial FFTW license, or a packaging design that does not redistribute FFTW.

Before enabling packaging, a release change must:

1. record the chosen licensing path and approver;
2. include the required license notices and corresponding-source evidence, or the
   commercial-license evidence, as applicable;
3. define the supported runtime identifier and native-asset layout;
4. test the packed/published artifact on a clean Windows x64 environment; and
5. remove `IsPackable=false` only in the same reviewed change.

Application-local builds are not evidence that these distribution gates have been met.
No realtime-performance claim is permitted until a BenchmarkDotNet profile has been
recorded on the named target hardware.
