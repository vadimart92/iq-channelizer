import { describe, expect, it } from "vitest";
import { parseProject, serializeProject } from "../src/app/project-io";
import { hasErrors, validateProject } from "../src/app/validation";
import {
  createDefaultProject,
  frequencyBounds,
  totalDataBytes,
  type FmBlock,
  type FmRadioBlock,
} from "../src/model/project";

describe("project model", () => {
  it("derives exact cf32 data size", () => {
    const project = createDefaultProject();
    expect(project.totalSamples).toBe(100_000);
    expect(totalDataBytes(project)).toBe(800_000);
  });

  it("derives Carson frequency bounds", () => {
    const block: FmBlock = {
      id: "fm-1", kind: "fm", startSample: 0, sampleCount: 100,
      centerFrequencyHz: -150_000, amplitudeDbfs: -10, phaseRad: 0, fadeSamples: 0,
      modulationFrequencyHz: 5_000, deviationHz: 25_000, modulationPhaseRad: 0,
    };
    expect(frequencyBounds(block)).toEqual([-180_000, -120_000]);
  });

  it("derives Carson bounds for program-audio FM", () => {
    const block: FmRadioBlock = {
      id: "radio-1", kind: "fm-radio", startSample: 0, sampleCount: 100,
      centerFrequencyHz: 100_000, amplitudeDbfs: -10, phaseRad: 0, fadeSamples: 0,
      audioBandwidthHz: 15_000, deviationHz: 75_000, seed: 1,
    };
    expect(frequencyBounds(block)).toEqual([10_000, 190_000]);
  });

  it("round-trips a valid project", () => {
    const project = createDefaultProject();
    const parsed = parseProject(serializeProject(project));
    expect(parsed).toEqual(project);
    expect(parsed).not.toBe(project);
  });

  it("rejects invalid Nyquist occupancy", () => {
    const project = createDefaultProject();
    project.signals.push({
      id: "bad-tone", kind: "tone", startSample: 0, sampleCount: 10,
      centerFrequencyHz: project.sampleRateHz / 2, amplitudeDbfs: -6,
      phaseRad: 0, fadeSamples: 0,
    });
    const issues = validateProject(project);
    expect(hasErrors(issues)).toBe(true);
    expect(issues.some((issue) => issue.message.includes("Nyquist"))).toBe(true);
  });

  it("rejects unsafe archive basenames", () => {
    const project = createDefaultProject();
    project.basename = "../recording";
    expect(hasErrors(validateProject(project))).toBe(true);
  });
});
