export function raisedCosineEnvelope(
  localSample: number,
  sampleCount: number,
  fadeSamples: number,
): number {
  if (fadeSamples <= 0) return 1;
  if (localSample < fadeSamples) {
    return 0.5 - 0.5 * Math.cos(Math.PI * localSample / fadeSamples);
  }
  const fromEnd = sampleCount - 1 - localSample;
  if (fromEnd < fadeSamples) {
    return 0.5 - 0.5 * Math.cos(Math.PI * fromEnd / fadeSamples);
  }
  return 1;
}
