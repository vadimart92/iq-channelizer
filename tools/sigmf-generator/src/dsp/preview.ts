import type { SignalProject } from "../model/project";
import { computeMasterGain, mixChunk } from "./mixer";

export interface SpectralPreview {
  width: number;
  height: number;
  power: Uint8Array;
}

export async function computeSpectralPreview(
  project: SignalProject,
  width = 192,
  fftSize = 128,
  shouldCancel: () => boolean = () => false,
): Promise<SpectralPreview> {
  const powerDb = new Float64Array(width * fftSize);
  let maximumDb = -Infinity;
  const gain = computeMasterGain(project);
  const maxStart = Math.max(0, project.totalSamples - fftSize);

  for (let column = 0; column < width; column += 1) {
    if (shouldCancel()) throw new DOMException("Cancelled", "AbortError");
    const center = Math.round((column + 0.5) * project.totalSamples / width);
    const start = Math.max(0, Math.min(maxStart, center - Math.floor(fftSize / 2)));
    const available = Math.min(fftSize, project.totalSamples - start);
    const iq = mixChunk(project, start, available, gain);

    for (let displayRow = 0; displayRow < fftSize; displayRow += 1) {
      const bin = (displayRow + fftSize / 2) % fftSize;
      let real = 0;
      let imaginary = 0;
      for (let sample = 0; sample < fftSize; sample += 1) {
        const window = 0.5 - 0.5 * Math.cos(2 * Math.PI * sample / (fftSize - 1));
        const inputReal = (iq[sample * 2] ?? 0) * window;
        const inputImaginary = (iq[sample * 2 + 1] ?? 0) * window;
        const angle = -2 * Math.PI * bin * sample / fftSize;
        real += inputReal * Math.cos(angle) - inputImaginary * Math.sin(angle);
        imaginary += inputReal * Math.sin(angle) + inputImaginary * Math.cos(angle);
      }
      const db = 10 * Math.log10(real * real + imaginary * imaginary + 1e-18);
      const outputRow = fftSize - 1 - displayRow;
      powerDb[outputRow * width + column] = db;
      maximumDb = Math.max(maximumDb, db);
    }
    if (column % 8 === 0) await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }

  const power = new Uint8Array(width * fftSize);
  const floor = maximumDb - 70;
  for (let index = 0; index < power.length; index += 1) {
    power[index] = Math.round(255 * Math.max(0, Math.min(1, ((powerDb[index] ?? floor) - floor) / 70)));
  }
  return { width, height: fftSize, power };
}
