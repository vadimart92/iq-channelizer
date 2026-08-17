import WebFFT from "webfft";
import type { SignalProject } from "../model/project";
import { computeMasterGain, mixChunk } from "./mixer";

export interface PreviewRegion {
  sampleStart: number;
  sampleEnd: number;
  frequencyLowHz: number;
  frequencyHighHz: number;
}

export interface SpectralPreview {
  width: number;
  height: number;
  power: Uint8Array;
  region: PreviewRegion;
}

export async function computeSpectralPreview(
  project: SignalProject,
  width = 192,
  fftSize = 256,
  requestedRegion?: PreviewRegion,
  shouldCancel: () => boolean = () => false,
): Promise<SpectralPreview> {
  if (!Number.isSafeInteger(fftSize) || fftSize < 32 || fftSize > 8_192 || (fftSize & (fftSize - 1)) !== 0) {
    throw new RangeError("FFT size must be a power of two from 32 to 8192.");
  }
  const nyquist = project.sampleRateHz / 2;
  const sampleStart = Math.max(0, Math.min(project.totalSamples - 1, Math.floor(requestedRegion?.sampleStart ?? 0)));
  const sampleEnd = Math.max(sampleStart + 1, Math.min(project.totalSamples, Math.ceil(requestedRegion?.sampleEnd ?? project.totalSamples)));
  const requestedLow = Math.max(-nyquist, requestedRegion?.frequencyLowHz ?? -nyquist);
  const requestedHigh = Math.min(nyquist, requestedRegion?.frequencyHighHz ?? nyquist);
  const binWidthHz = project.sampleRateHz / fftSize;
  const firstDisplayBin = Math.max(0, Math.min(fftSize - 1, Math.floor((requestedLow + nyquist) / binWidthHz)));
  const displayBinEnd = Math.max(firstDisplayBin + 1, Math.min(fftSize, Math.ceil((requestedHigh + nyquist) / binWidthHz)));
  const height = displayBinEnd - firstDisplayBin;
  const region: PreviewRegion = {
    sampleStart,
    sampleEnd,
    frequencyLowHz: -nyquist + firstDisplayBin * binWidthHz,
    frequencyHighHz: -nyquist + displayBinEnd * binWidthHz,
  };
  const powerDb = new Float64Array(width * height);
  let maximumDb = -Infinity;
  const gain = computeMasterGain(project);
  const maxStart = Math.max(sampleStart, sampleEnd - fftSize);
  const fft = new WebFFT(fftSize, "kissWasm", false);

  try {
    for (let column = 0; column < width; column += 1) {
      if (shouldCancel()) throw new DOMException("Cancelled", "AbortError");
      const center = Math.round(sampleStart + (column + 0.5) * (sampleEnd - sampleStart) / width);
      const start = Math.max(sampleStart, Math.min(maxStart, center - Math.floor(fftSize / 2)));
      const available = Math.min(fftSize, sampleEnd - start);
      const iq = mixChunk(project, start, available, gain);
      const input = new Float32Array(fftSize * 2);
      for (let sample = 0; sample < fftSize; sample += 1) {
        const window = 0.5 - 0.5 * Math.cos(2 * Math.PI * sample / (fftSize - 1));
        input[sample * 2] = (iq[sample * 2] ?? 0) * window;
        input[sample * 2 + 1] = (iq[sample * 2 + 1] ?? 0) * window;
      }
      const spectrum = fft.fft(input);
      for (let displayBin = firstDisplayBin; displayBin < displayBinEnd; displayBin += 1) {
        const bin = (displayBin + fftSize / 2) % fftSize;
        const binReal = spectrum[bin * 2] ?? 0;
        const binImaginary = spectrum[bin * 2 + 1] ?? 0;
        const db = 10 * Math.log10(binReal * binReal + binImaginary * binImaginary + 1e-18);
        const outputRow = displayBinEnd - 1 - displayBin;
        powerDb[outputRow * width + column] = db;
        maximumDb = Math.max(maximumDb, db);
      }
      if (column % 8 === 0) await new Promise<void>((resolve) => setTimeout(resolve, 0));
    }
  } finally {
    fft.dispose();
  }

  const power = new Uint8Array(width * height);
  const floor = maximumDb - 70;
  for (let index = 0; index < power.length; index += 1) {
    power[index] = Math.round(255 * Math.max(0, Math.min(1, ((powerDb[index] ?? floor) - floor) / 70)));
  }
  return { width, height, power, region };
}
