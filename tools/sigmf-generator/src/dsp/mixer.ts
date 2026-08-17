import {
  blockEndSample,
  linearAmplitude,
  sortSignals,
  type FmBlock,
  type FmRadioBlock,
  type SignalBlock,
  type SignalProject,
} from "../model/project";
import { raisedCosineEnvelope } from "./envelope";

const TWO_PI = 2 * Math.PI;

interface RadioComponent {
  frequencyHz: number;
  phaseRad: number;
  weight: number;
}

interface RadioSegment {
  durationSeconds: number;
  phaseStartRad: number;
  components: RadioComponent[];
}

interface RadioProfile {
  segmentSamples: number;
  segments: RadioSegment[];
}

const radioProfileCache = new WeakMap<FmRadioBlock, { signature: string; profile: RadioProfile }>();

interface SweepEvent {
  sample: number;
  delta: number;
}

export function computeMasterGain(project: SignalProject): number {
  if (project.signals.length === 0) return 1;
  const events: SweepEvent[] = [];
  for (const block of project.signals) {
    const amplitude = linearAmplitude(block);
    events.push({ sample: block.startSample, delta: amplitude });
    events.push({ sample: blockEndSample(block), delta: -amplitude });
  }
  events.sort((left, right) => left.sample - right.sample || left.delta - right.delta);

  let current = 0;
  let maximum = 0;
  for (const event of events) {
    current += event.delta;
    maximum = Math.max(maximum, current);
  }
  const target = 10 ** (project.targetPeakDbfs / 20);
  return maximum > 0 ? Math.min(1, target / maximum) : 1;
}

function fmPhase(block: FmBlock, localSample: number, sampleRateHz: number): number {
  const beta = block.deviationHz / block.modulationFrequencyHz;
  const modulationPhase = TWO_PI * block.modulationFrequencyHz * localSample / sampleRateHz
    + block.modulationPhaseRad;
  return block.phaseRad
    + TWO_PI * block.centerFrequencyHz * localSample / sampleRateHz
    + beta * (Math.sin(modulationPhase) - Math.sin(block.modulationPhaseRad));
}

function hashUnit(seed: number, index: number): number {
  let value = (seed + Math.imul(index + 1, 0x9e3779b9)) >>> 0;
  value ^= value >>> 16;
  value = Math.imul(value, 0x21f0aaad);
  value ^= value >>> 15;
  value = Math.imul(value, 0x735a2d97);
  value ^= value >>> 15;
  return (value >>> 0) / 0x1_0000_0000;
}

function radioComponents(block: FmRadioBlock, segmentIndex: number): RadioComponent[] {
  const count = 12;
  const minimumHz = Math.min(80, block.audioBandwidthHz * 0.12);
  const ratio = block.audioBandwidthHz / minimumHz;
  const raw = Array.from({ length: count }, (_, index) => {
    const hashIndex = segmentIndex * count + index;
    const position = (index + 0.2 + hashUnit(block.seed, hashIndex) * 0.6) / count;
    const frequencyHz = minimumHz * ratio ** position;
    const weight = (0.35 + hashUnit(block.seed ^ 0xa511e9b3, hashIndex)) / Math.sqrt(frequencyHz);
    return {
      frequencyHz,
      phaseRad: TWO_PI * hashUnit(block.seed ^ 0x63d83595, hashIndex),
      weight,
    };
  });
  const totalWeight = raw.reduce((sum, component) => sum + component.weight, 0);
  const activity = segmentIndex % 7 === 5
    ? 0.06
    : 0.55 + 0.45 * hashUnit(block.seed ^ 0x37a49f21, segmentIndex);
  return raw.map((component) => ({ ...component, weight: activity * component.weight / totalWeight }));
}

function integratedSine(frequencyHz: number, phaseRad: number, seconds: number): number {
  if (Math.abs(frequencyHz) < 1e-9) return Math.sin(phaseRad) * seconds;
  return (Math.cos(phaseRad) - Math.cos(TWO_PI * frequencyHz * seconds + phaseRad))
    / (TWO_PI * frequencyHz);
}

function radioSegmentPhaseDelta(block: FmRadioBlock, segment: RadioSegment, seconds: number): number {
  const envelopeFrequencyHz = 1 / segment.durationSeconds;
  let integral = 0;
  for (const component of segment.components) {
    const { frequencyHz, phaseRad, weight } = component;
    // sin²(pi*t/T) burst envelope, expanded into three analytically integrable tones.
    integral += weight * (
      0.5 * integratedSine(frequencyHz, phaseRad, seconds)
      - 0.25 * integratedSine(frequencyHz + envelopeFrequencyHz, phaseRad, seconds)
      - 0.25 * integratedSine(frequencyHz - envelopeFrequencyHz, phaseRad, seconds)
    );
  }
  return TWO_PI * block.deviationHz * integral;
}

function radioProfile(block: FmRadioBlock, sampleRateHz: number): RadioProfile {
  const signature = `${sampleRateHz}:${block.sampleCount}:${block.audioBandwidthHz}:${block.deviationHz}:${block.seed}`;
  const cached = radioProfileCache.get(block);
  if (cached?.signature === signature) return cached.profile;
  const segmentSamples = Math.max(1, Math.round(sampleRateHz * 0.024));
  const segmentCount = Math.ceil(block.sampleCount / segmentSamples);
  const segments: RadioSegment[] = [];
  let phaseStartRad = 0;
  for (let index = 0; index < segmentCount; index += 1) {
    const samples = Math.min(segmentSamples, block.sampleCount - index * segmentSamples);
    const segment: RadioSegment = {
      durationSeconds: samples / sampleRateHz,
      phaseStartRad,
      components: radioComponents(block, index),
    };
    segments.push(segment);
    phaseStartRad += radioSegmentPhaseDelta(block, segment, segment.durationSeconds);
  }
  const profile = { segmentSamples, segments };
  radioProfileCache.set(block, { signature, profile });
  return profile;
}

function fmRadioPhase(
  block: FmRadioBlock,
  profile: RadioProfile,
  localSample: number,
  sampleRateHz: number,
): number {
  const time = localSample / sampleRateHz;
  const segmentIndex = Math.min(profile.segments.length - 1, Math.floor(localSample / profile.segmentSamples));
  const segment = profile.segments[segmentIndex];
  if (!segment) return block.phaseRad + TWO_PI * block.centerFrequencyHz * time;
  const segmentTime = (localSample - segmentIndex * profile.segmentSamples) / sampleRateHz;
  const modulationPhase = segment.phaseStartRad + radioSegmentPhaseDelta(block, segment, segmentTime);
  return block.phaseRad + TWO_PI * block.centerFrequencyHz * time + modulationPhase;
}

function phaseAt(
  block: SignalBlock,
  localSample: number,
  sampleRateHz: number,
  profile?: RadioProfile,
): number {
  if (block.kind === "fm") return fmPhase(block, localSample, sampleRateHz);
  if (block.kind === "fm-radio") return fmRadioPhase(block, profile ?? radioProfile(block, sampleRateHz), localSample, sampleRateHz);
  return block.phaseRad + TWO_PI * block.centerFrequencyHz * localSample / sampleRateHz;
}

export function mixChunk(
  project: SignalProject,
  firstSample: number,
  sampleCount: number,
  masterGain = computeMasterGain(project),
): Float32Array {
  if (!Number.isSafeInteger(firstSample) || !Number.isSafeInteger(sampleCount) || sampleCount < 0) {
    throw new RangeError("Chunk bounds must be safe integers.");
  }
  const output = new Float32Array(sampleCount * 2);
  const chunkEnd = firstSample + sampleCount;

  for (const block of sortSignals(project.signals)) {
    const overlapStart = Math.max(firstSample, block.startSample);
    const overlapEnd = Math.min(chunkEnd, blockEndSample(block));
    if (overlapStart >= overlapEnd) continue;
    const amplitude = linearAmplitude(block) * masterGain;
    const profile = block.kind === "fm-radio" ? radioProfile(block, project.sampleRateHz) : undefined;
    for (let absoluteSample = overlapStart; absoluteSample < overlapEnd; absoluteSample += 1) {
      const localSample = absoluteSample - block.startSample;
      const outputIndex = (absoluteSample - firstSample) * 2;
      const envelope = raisedCosineEnvelope(localSample, block.sampleCount, block.fadeSamples);
      const magnitude = amplitude * envelope;
      const phase = phaseAt(block, localSample, project.sampleRateHz, profile);
      output[outputIndex] = (output[outputIndex] ?? 0) + magnitude * Math.cos(phase);
      output[outputIndex + 1] = (output[outputIndex + 1] ?? 0) + magnitude * Math.sin(phase);
    }
  }
  return output;
}

export function isLittleEndian(): boolean {
  const buffer = new ArrayBuffer(4);
  new Uint32Array(buffer)[0] = 0x01020304;
  return new Uint8Array(buffer)[0] === 0x04;
}

export function encodeCf32Le(iq: Float32Array): Uint8Array {
  if (isLittleEndian()) {
    return new Uint8Array(iq.buffer, iq.byteOffset, iq.byteLength);
  }
  const bytes = new Uint8Array(iq.byteLength);
  const view = new DataView(bytes.buffer);
  for (let index = 0; index < iq.length; index += 1) {
    view.setFloat32(index * 4, iq[index] ?? 0, true);
  }
  return bytes;
}
