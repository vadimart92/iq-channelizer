import { describe, expect, it } from "vitest";
import { createDefaultProject } from "../src/model/project";
import { createWavHeader, validateWavProject, WAV_HEADER_BYTES } from "../src/wav/writer";

function fourCc(bytes: Uint8Array, offset: number): string {
  return new TextDecoder().decode(bytes.subarray(offset, offset + 4));
}

describe("stereo float32 IQ WAV", () => {
  it("writes a canonical 44-byte IEEE-float WAVE header", () => {
    const project = createDefaultProject();
    project.sampleRateHz = 48_000;
    project.totalSamples = 100;
    const header = createWavHeader(project);
    const view = new DataView(header.buffer);

    expect(header.byteLength).toBe(WAV_HEADER_BYTES);
    expect(fourCc(header, 0)).toBe("RIFF");
    expect(view.getUint32(4, true)).toBe(36 + 800);
    expect(fourCc(header, 8)).toBe("WAVE");
    expect(fourCc(header, 12)).toBe("fmt ");
    expect(view.getUint16(20, true)).toBe(3);
    expect(view.getUint16(22, true)).toBe(2);
    expect(view.getUint32(24, true)).toBe(48_000);
    expect(view.getUint32(28, true)).toBe(48_000 * 8);
    expect(view.getUint16(32, true)).toBe(8);
    expect(view.getUint16(34, true)).toBe(32);
    expect(fourCc(header, 36)).toBe("data");
    expect(view.getUint32(40, true)).toBe(800);
  });

  it("rejects fractional sample rates", () => {
    const project = createDefaultProject();
    project.sampleRateHz = 44_100.5;
    expect(validateWavProject(project)).toContain("WAV sample rate must be an integer between 1 and 4,294,967,295 Hz.");
  });

  it("uses the same 8-byte sample layout as cf32", () => {
    const project = createDefaultProject();
    project.totalSamples = 123;
    const view = new DataView(createWavHeader(project).buffer);
    expect(view.getUint32(40, true)).toBe(123 * 8);
  });
});
