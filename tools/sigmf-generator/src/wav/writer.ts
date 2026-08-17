import { totalDataBytes, type SignalProject } from "../model/project";

export const WAV_HEADER_BYTES = 44;
export const WAV_MAX_DATA_BYTES = 0xffff_ffff - 36;

function writeFourCc(view: DataView, offset: number, value: string): void {
  for (let index = 0; index < 4; index += 1) {
    view.setUint8(offset + index, value.charCodeAt(index));
  }
}

export function validateWavProject(project: SignalProject): string[] {
  const errors: string[] = [];
  if (!Number.isInteger(project.sampleRateHz) || project.sampleRateHz < 1 || project.sampleRateHz > 0xffff_ffff) {
    errors.push("WAV sample rate must be an integer between 1 and 4,294,967,295 Hz.");
  }
  if (project.sampleRateHz * 8 > 0xffff_ffff) {
    errors.push("WAV byte rate exceeds the classic RIFF uint32 field.");
  }
  if (totalDataBytes(project) > WAV_MAX_DATA_BYTES) {
    errors.push("Classic WAV export is limited to approximately 4 GiB of sample data.");
  }
  return errors;
}

export function createWavHeader(project: SignalProject): Uint8Array {
  const errors = validateWavProject(project);
  if (errors.length > 0) throw new Error(errors.join(" "));
  const dataBytes = totalDataBytes(project);
  const buffer = new ArrayBuffer(WAV_HEADER_BYTES);
  const view = new DataView(buffer);
  writeFourCc(view, 0, "RIFF");
  view.setUint32(4, 36 + dataBytes, true);
  writeFourCc(view, 8, "WAVE");
  writeFourCc(view, 12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 3, true); // WAVE_FORMAT_IEEE_FLOAT
  view.setUint16(22, 2, true); // I = left, Q = right
  view.setUint32(24, project.sampleRateHz, true);
  view.setUint32(28, project.sampleRateHz * 8, true);
  view.setUint16(32, 8, true);
  view.setUint16(34, 32, true);
  writeFourCc(view, 36, "data");
  view.setUint32(40, dataBytes, true);
  return new Uint8Array(buffer);
}
