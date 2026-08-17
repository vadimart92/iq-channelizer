import {
  MAX_USTAR_FILE_BYTES,
  blockEndSample,
  frequencyBounds,
  totalDataBytes,
  type SignalBlock,
  type SignalProject,
} from "../model/project";

export type IssueSeverity = "error" | "warning";

export interface ValidationIssue {
  severity: IssueSeverity;
  path: string;
  message: string;
}

const BASENAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$/;

function finite(value: number): boolean {
  return Number.isFinite(value);
}

function validateBlock(
  block: SignalBlock,
  project: SignalProject,
  index: number,
): ValidationIssue[] {
  const issues: ValidationIssue[] = [];
  const path = `signals[${index}]`;
  const nyquist = project.sampleRateHz / 2;

  if (!Number.isSafeInteger(block.startSample) || block.startSample < 0) {
    issues.push({ severity: "error", path, message: "Start sample must be a non-negative safe integer." });
  }
  if (!Number.isSafeInteger(block.sampleCount) || block.sampleCount < 1) {
    issues.push({ severity: "error", path, message: "Signal length must be at least one sample." });
  }
  if (blockEndSample(block) > project.totalSamples) {
    issues.push({ severity: "error", path, message: "Signal extends past the recording duration." });
  }
  if (!finite(block.centerFrequencyHz)) {
    issues.push({ severity: "error", path, message: "Center frequency must be finite." });
  }
  if (!finite(block.amplitudeDbfs) || block.amplitudeDbfs > 0 || block.amplitudeDbfs < -160) {
    issues.push({ severity: "error", path, message: "Amplitude must be between -160 and 0 dBFS." });
  }
  if (!finite(block.phaseRad)) {
    issues.push({ severity: "error", path, message: "Phase must be finite." });
  }
  if (!Number.isSafeInteger(block.fadeSamples) || block.fadeSamples < 0 || block.fadeSamples > Math.floor(block.sampleCount / 2)) {
    issues.push({ severity: "error", path, message: "Fade must be between zero and half the signal length." });
  }

  if (block.kind === "fm") {
    if (!finite(block.modulationFrequencyHz) || block.modulationFrequencyHz <= 0) {
      issues.push({ severity: "error", path, message: "FM modulation frequency must be greater than zero." });
    }
    if (!finite(block.deviationHz) || block.deviationHz < 0) {
      issues.push({ severity: "error", path, message: "FM deviation must be non-negative." });
    }
    if (!finite(block.modulationPhaseRad)) {
      issues.push({ severity: "error", path, message: "FM modulation phase must be finite." });
    }
  }

  const [low, high] = frequencyBounds(block);
  if (!finite(low) || !finite(high) || low < -nyquist || high >= nyquist) {
    issues.push({ severity: "error", path, message: "Declared occupied band crosses a Nyquist edge." });
  } else if (block.kind === "fm") {
    const guard = Math.min(low + nyquist, nyquist - high);
    if (guard < project.sampleRateHz * 0.01) {
      issues.push({ severity: "warning", path, message: "FM spectral tails are close to a Nyquist edge." });
    }
  }

  return issues;
}

export function validateProject(project: SignalProject): ValidationIssue[] {
  const issues: ValidationIssue[] = [];
  if (project.schemaVersion !== 1) {
    issues.push({ severity: "error", path: "schemaVersion", message: "Unsupported project schema version." });
  }
  if (!BASENAME_PATTERN.test(project.basename)) {
    issues.push({ severity: "error", path: "basename", message: "Use 1-64 ASCII letters, digits, dots or hyphens." });
  }
  if (!finite(project.sampleRateHz) || project.sampleRateHz <= 0 || project.sampleRateHz > 1e12) {
    issues.push({ severity: "error", path: "sampleRateHz", message: "Sample rate must be greater than zero and no more than 1e12 Hz." });
  }
  if (!Number.isSafeInteger(project.totalSamples) || project.totalSamples < 1) {
    issues.push({ severity: "error", path: "totalSamples", message: "Total sample count must be a positive safe integer." });
  }
  const bytes = totalDataBytes(project);
  if (!Number.isSafeInteger(bytes) || bytes > MAX_USTAR_FILE_BYTES) {
    issues.push({ severity: "error", path: "totalSamples", message: "Recording exceeds the MVP ustar limit (8 GiB - 1 byte)." });
  }
  if (project.rfCenterHz !== undefined && (!finite(project.rfCenterHz) || Math.abs(project.rfCenterHz) > 1e12)) {
    issues.push({ severity: "error", path: "rfCenterHz", message: "RF center frequency must be finite and within ±1e12 Hz." });
  }
  if (!finite(project.targetPeakDbfs) || project.targetPeakDbfs > 0 || project.targetPeakDbfs < -60) {
    issues.push({ severity: "error", path: "targetPeakDbfs", message: "Target peak must be between -60 and 0 dBFS." });
  }
  project.signals.forEach((block, index) => issues.push(...validateBlock(block, project, index)));
  if (project.rfCenterHz !== undefined) {
    project.signals.forEach((block, index) => {
      const [low, high] = frequencyBounds(block);
      if (Math.abs(project.rfCenterHz! + low) > 1e12 || Math.abs(project.rfCenterHz! + high) > 1e12) {
        issues.push({ severity: "error", path: `signals[${index}]`, message: "Absolute RF annotation exceeds the SigMF frequency range." });
      }
    });
  }
  return issues;
}

export function hasErrors(issues: readonly ValidationIssue[]): boolean {
  return issues.some((issue) => issue.severity === "error");
}
