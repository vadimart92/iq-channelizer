# ADR 0001: scalar DSP conventions

The public complex layout is interleaved `float real, float imaginary` and is exactly 8 bytes.

The FDC correctness backend uses an unnormalized forward transform with the `-j` sign and an unnormalized backward transform with the `+j` sign. Its short inverse is normalized by the original forward length `N`, not by the short transform length.

The PFB correctness backend uses the unnormalized `+j` transform. For frame anchor `r`, it applies the canonical correction `exp(-j 2 pi k r / K)` by cyclically left-shifting the phase vector by `r mod K` before the transform. It never divides the result by `K`; prototype gain is responsible for amplitude normalization.

Every oscillator and frame correction derives phase from absolute input sample positions. Process-local frame numbers are not a phase origin.

This first implementation is deliberately scalar. FFTW and SIMD backends may replace internals later, but must preserve these conventions and the public streaming contract.
