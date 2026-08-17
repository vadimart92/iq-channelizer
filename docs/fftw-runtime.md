# FFTW runtime, distribution, and licensing

## Bundled binary

The repository bundles the single-precision `libfftw3f-3.dll` from the official
FFTW 3.3.5 64-bit Windows archive:

- source archive: <https://fftw.org/pub/fftw/fftw-3.3.5-dll64.zip>;
- archive SHA-256: `CFD88DC0E8D7001115EA79E069A2C695D52C8947F5B4F3B7AC54A192756F439F`;
- bundled DLL SHA-256: `42CA18FFF35DD12890E04478BC990005B3969CB744F6843976BD436CCD7F0A4C`;
- runtime version string: `fftw-3.3.5-sse2-avx`;
- corresponding source release: <https://fftw.org/pub/fftw/fftw-3.3.5.tar.gz>.

The bundled DLL was verified byte-for-byte against the DLL in that official archive.
It is copied beside build and publish outputs by `IqChannelizer.csproj`.

Only a 64-bit Windows x64 process is supported by this distribution. Runtime startup
validates OS, process architecture, required native exports, and the FFTW version
symbol before allocating or planning. A missing, wrong-architecture, or incompatible
DLL produces an exception that names the expected file and deployment location.

## Planning, cache, buffers, and wisdom policy

- FFTW planning is serialized process-wide. It never occurs in `Process`.
- `FFTW_ESTIMATE` is the default until benchmark data justifies `FFTW_MEASURE` for a
  resolved deployment profile. Both modes are separate cache keys.
- The cache key includes transform length, direction, batch count, strides/distances,
  in-place mode, thread count, FFTW alignment class, and planning mode.
- Cached native plans are shared through reference-counted leases. Each plan wrapper
  owns separate FFTW-allocated input/output buffers and executes through the FFTW
  new-array API. Idle plans can be cleared explicitly and are cleared at process exit.
- Both out-of-place and in-place contiguous C2C layouts are supported. Current engines
  use out-of-place transforms.
- `fftwf_malloc` owns all native transform buffers. The wrapper requires at least
  16-byte address alignment for the current scalar/SSE2-capable runtime and also checks
  FFTW alignment-class compatibility before a cached plan is reused. A future manual
  AVX2/AVX-512 kernel must introduce and test its stronger alignment requirement rather
  than assuming that `fftwf_malloc` promises a fixed 32- or 64-byte address.
- Wisdom import/export and plan creation share the planning lock. Export uses FFTW's
  callback API so ownership of a C-runtime-allocated string never crosses the DLL
  boundary.
- The bundled package is operated with one FFTW thread. `ThreadCount > 1` is rejected:
  enabling it requires distributing the appropriate FFTW threads DLL, defining global
  initialization/cleanup ownership, and benchmarking the result first.

FFTW documents execution as the thread-safe part of its API. The channelizer's public
engine contract nevertheless does not permit concurrent calls on the same engine;
different plan wrappers use different input/output buffers.

## License decision and release obligation

FFTW is offered under GNU GPL version 2 or later, with alternative commercial licensing
available from MIT. The permissive notice at the top of `fftw3.h` explicitly applies
only to that header and must not be treated as the DLL's license.

Therefore a product that distributes this DLL must make an explicit release decision:

1. distribute the combined work under GPL-compatible terms and satisfy the applicable
   GPL notice, license-copy, corresponding-source, and redistribution obligations; or
2. obtain and comply with an alternative commercial FFTW license; or
3. do not distribute FFTW and require an independently supplied compatible runtime.

This repository records provenance and technical packaging but does not by itself make
a downstream product license-compliant. Release owners must retain the chosen license
evidence and the exact corresponding source/archive with release artifacts. Official
references: <https://fftw.org/download.html> and
<https://fftw.org/doc/License-and-Copyright.html>.
