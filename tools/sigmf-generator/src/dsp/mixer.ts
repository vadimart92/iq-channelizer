import {
  blockEndSample,
  linearAmplitude,
  sortSignals,
  type FmBlock,
  type SignalBlock,
  type SignalProject,
} from "../model/project";
import { raisedCosineEnvelope } from "./envelope";

const TWO_PI = 2 * Math.PI;

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

function phaseAt(block: SignalBlock, localSample: number, sampleRateHz: number): number {
  if (block.kind === "fm") return fmPhase(block, localSample, sampleRateHz);
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
    for (let absoluteSample = overlapStart; absoluteSample < overlapEnd; absoluteSample += 1) {
      const localSample = absoluteSample - block.startSample;
      const outputIndex = (absoluteSample - firstSample) * 2;
      const envelope = raisedCosineEnvelope(localSample, block.sampleCount, block.fadeSamples);
      const magnitude = amplitude * envelope;
      const phase = phaseAt(block, localSample, project.sampleRateHz);
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
