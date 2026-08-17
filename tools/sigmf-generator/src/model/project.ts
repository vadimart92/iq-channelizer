export const PROJECT_SCHEMA_VERSION = 1 as const;
export const SIGMF_VERSION = "1.2.6";
export const BYTES_PER_COMPLEX_SAMPLE = 8;
export const MAX_USTAR_FILE_BYTES = 8 * 1024 ** 3 - 1;
export const DEFAULT_BLOB_LIMIT_BYTES = 512 * 1024 ** 2;

export type SignalKind = "tone" | "fm";

export interface BaseBlock {
  id: string;
  kind: SignalKind;
  startSample: number;
  sampleCount: number;
  centerFrequencyHz: number;
  amplitudeDbfs: number;
  phaseRad: number;
  fadeSamples: number;
}

export interface ToneBlock extends BaseBlock {
  kind: "tone";
}

export interface FmBlock extends BaseBlock {
  kind: "fm";
  modulationFrequencyHz: number;
  deviationHz: number;
  modulationPhaseRad: number;
}

export type SignalBlock = ToneBlock | FmBlock;

export interface SignalProject {
  schemaVersion: typeof PROJECT_SCHEMA_VERSION;
  basename: string;
  sampleRateHz: number;
  totalSamples: number;
  rfCenterHz?: number;
  targetPeakDbfs: number;
  signals: SignalBlock[];
}

export function createDefaultProject(): SignalProject {
  return {
    schemaVersion: PROJECT_SCHEMA_VERSION,
    basename: "recording",
    sampleRateHz: 1_000_000,
    totalSamples: 100_000,
    targetPeakDbfs: -1,
    signals: [],
  };
}

export function durationSeconds(project: SignalProject): number {
  return project.totalSamples / project.sampleRateHz;
}

export function totalDataBytes(project: SignalProject): number {
  return project.totalSamples * BYTES_PER_COMPLEX_SAMPLE;
}

export function blockEndSample(block: SignalBlock): number {
  return block.startSample + block.sampleCount;
}

export function linearAmplitude(block: SignalBlock): number {
  return 10 ** (block.amplitudeDbfs / 20);
}

export function occupiedBandwidthHz(block: SignalBlock): number {
  return block.kind === "tone"
    ? 0
    : 2 * (block.deviationHz + block.modulationFrequencyHz);
}

export function frequencyBounds(block: SignalBlock): [number, number] {
  const halfBandwidth = occupiedBandwidthHz(block) / 2;
  return [
    block.centerFrequencyHz - halfBandwidth,
    block.centerFrequencyHz + halfBandwidth,
  ];
}

export function newSignalId(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  return `signal-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

export function cloneProject(project: SignalProject): SignalProject {
  return structuredClone(project);
}

export function sortSignals(signals: readonly SignalBlock[]): SignalBlock[] {
  return [...signals].sort(
    (left, right) =>
      left.startSample - right.startSample || left.id.localeCompare(right.id),
  );
}
