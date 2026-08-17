import { describe, expect, it } from "vitest";
import { raisedCosineEnvelope } from "../src/dsp/envelope";
import { computeMasterGain, encodeCf32Le, mixChunk } from "../src/dsp/mixer";
import { createDefaultProject, type SignalProject } from "../src/model/project";

function toneProject(): SignalProject {
  return {
    ...createDefaultProject(), sampleRateHz: 4, totalSamples: 4, targetPeakDbfs: 0,
    signals: [{
      id: "tone", kind: "tone", startSample: 0, sampleCount: 4,
      centerFrequencyHz: 1, amplitudeDbfs: 0, phaseRad: 0, fadeSamples: 0,
    }],
  };
}

describe("DSP mixer", () => {
  it("generates a positive complex Fs/4 tone", () => {
    const iq = mixChunk(toneProject(), 0, 4);
    const rounded = [...iq].map((value) => Math.abs(value) < 1e-6 ? 0 : Math.round(value));
    expect(rounded).toEqual([1, 0, 0, 1, -1, 0, 0, -1]);
  });

  it("is byte-identical across chunk boundaries", () => {
    const project = toneProject();
    project.sampleRateHz = 32;
    project.totalSamples = 29;
    project.signals[0]!.sampleCount = 29;
    project.signals[0]!.centerFrequencyHz = 3.25;
    const full = encodeCf32Le(mixChunk(project, 0, 29));
    const parts = [mixChunk(project, 0, 7), mixChunk(project, 7, 11), mixChunk(project, 18, 11)];
    const joined = new Uint8Array(full.length);
    let offset = 0;
    for (const part of parts) {
      const bytes = encodeCf32Le(part);
      joined.set(bytes, offset);
      offset += bytes.length;
    }
    expect(joined).toEqual(full);
  });

  it("uses conservative overlap normalization", () => {
    const project = toneProject();
    project.targetPeakDbfs = -1;
    project.signals.push({ ...project.signals[0]!, id: "tone-2" });
    expect(computeMasterGain(project)).toBeCloseTo(10 ** (-1 / 20) / 2, 12);
  });

  it("keeps phase0 as the first FM sample phase", () => {
    const project = toneProject();
    project.signals = [{
      id: "fm", kind: "fm", startSample: 0, sampleCount: 4,
      centerFrequencyHz: 0, amplitudeDbfs: 0, phaseRad: Math.PI / 3, fadeSamples: 0,
      modulationFrequencyHz: 0.25, deviationHz: 0.5, modulationPhaseRad: 0.7,
    }];
    const iq = mixChunk(project, 0, 1);
    expect(iq[0]).toBeCloseTo(0.5, 6);
    expect(iq[1]).toBeCloseTo(Math.sqrt(3) / 2, 6);
  });

  it("creates a symmetric raised-cosine edge envelope", () => {
    const values = Array.from({ length: 9 }, (_, index) => raisedCosineEnvelope(index, 9, 3));
    expect(values[0]).toBe(0);
    expect(values[8]).toBe(0);
    expect(values[1]).toBeCloseTo(values[7]!, 12);
    expect(values[3]).toBe(1);
    expect(values[4]).toBe(1);
  });

  it("encodes IEEE-754 floats little-endian", () => {
    const bytes = encodeCf32Le(new Float32Array([1, -1]));
    expect([...bytes]).toEqual([0, 0, 128, 63, 0, 0, 128, 191]);
  });
});
