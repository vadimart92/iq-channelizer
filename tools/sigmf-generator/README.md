# SigMF Signal Composer

Browser-only MVP for composing deterministic complex IQ recordings on a time/frequency canvas and exporting a SigMF 1.2.6 archive.

## Run

```powershell
cd tools/sigmf-generator
pnpm install
pnpm dev
```

Open the URL printed by Vite. Production output is static:

```powershell
pnpm build
pnpm preview
```

Tests:

```powershell
pnpm test
```

## Workflow

1. Set filename, sample rate in Hz/kHz/MHz, duration and optional RF center frequency.
2. Select **Tone** or **FM Radio** and drag on the canvas.
3. Return to **Select** to move/resize blocks or edit exact values in the inspector.
4. Keep **Computed spectral preview** enabled to compare the actual coarse STFT with the design blocks.
   Select an FFT size from 64 to 8192: larger values improve frequency resolution, while smaller values improve time resolution and update faster. Preview columns follow the canvas pixel width, FFT windows are centered across the visible time viewport, and only visible frequency bins are returned.
5. Download `basename.sigmf` or a stereo float32 `basename.wav`.

The archive is an uncompressed POSIX ustar file containing:

```text
basename.sigmf-data  # interleaved IEEE-754 I,Q float32 little-endian
basename.sigmf-meta  # UTF-8 SigMF metadata
```

Chromium-based browsers in a secure context use streaming file output. Other browsers use a Blob fallback with a 512 MiB memory guard.

WAV export uses `WAVE_FORMAT_IEEE_FLOAT`, 32 bits, two channels: `I = left`, `Q = right`. It has the same interleaved sample payload as `cf32_le`, preceded by a canonical 44-byte RIFF/WAVE header.

## Canvas controls

- wheel: zoom time around the cursor;
- `Alt + wheel`: zoom frequency;
- `Shift + wheel`: pan time;
- middle drag or `Space + drag`: pan both axes;
- `V`, `T`, `F`: Select, Tone, FM Radio;
- drag empty canvas in Select mode: marquee/multi-selection;
- `Ctrl/Cmd + click`: toggle one signal in the selection;
- drag any selected signal: move the complete multi-selection;
- `Ctrl/Cmd + Z`: undo; `Ctrl/Cmd + Shift + Z` or `Ctrl/Cmd + Y`: redo;
- right-click a signal or selection: context menu;
- Delete/Backspace: remove the selected signal(s);
- Escape: cancel the current drag.

Time is persisted as integer sample indices. The vertical axis is always baseband offset; when RF center is set, SigMF annotation edges are exported as absolute RF frequencies.

## Signal semantics

Tone:

```text
phase(k) = phase0 + 2*pi*fc*k/Fs
```

FM Radio uses a deterministic built-in speech/music-like modulation source. It generates changing 24 ms audio bursts with smooth envelopes, seeded spectral content and occasional quiet intervals, then integrates that program into a continuous carrier phase. Change **Program seed** for a different waveform while keeping exports reproducible.

Legacy project files containing single-tone FM remain supported:

```text
beta = deviation/fm
phase(k) = phase0 + 2*pi*fc*k/Fs
           + beta*(sin(2*pi*fm*k/Fs + modulationPhase) - sin(modulationPhase))
```

Blocks use a symmetric raised-cosine edge fade. Overlapping signals add linearly. A conservative master gain keeps the maximum sum of active block amplitudes at the configured target (currently -1 dBFS) without a limiter or waveform distortion.

FM block height represents Carson's estimated occupied bandwidth, `2 * (deviation + audio bandwidth)`. Real FM has spectral tails beyond that rectangle; the computed preview shows this distinction.

## Project files

**Save project** downloads a versioned `basename.iqgen.json`. It contains the complete editor state and can be restored with **Load project**. Generator-specific machine fields are deliberately kept out of SigMF metadata; exported annotations use only standard `core:*` fields.

## MVP limits

- one complex channel;
- `cf32_le` only;
- Tone and FM Radio with a built-in synthetic program source; imported audio is not yet supported;
- raw dataset at most `8 GiB - 1 byte` for the simple ustar writer;
- classic WAV sample payload at most approximately 4 GiB; WAV sample rate must be an integer;
- Blob fallback at most 512 MiB;
- coarse preview is intentionally bounded and is not a full-resolution spectrogram;
- SHA-512 is not emitted because `core:sha512` is optional and portable streaming digest is outside this MVP.

See [implementation-plan.md](implementation-plan.md) for the design decisions and follow-up phases.
