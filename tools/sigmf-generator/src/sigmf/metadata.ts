import {
  SIGMF_VERSION,
  frequencyBounds,
  sortSignals,
  type SignalBlock,
  type SignalProject,
} from "../model/project";

export interface SigMfAnnotation {
  "core:sample_start": number;
  "core:sample_count": number;
  "core:freq_lower_edge": number;
  "core:freq_upper_edge": number;
  "core:label": string;
  "core:generator": string;
  "core:comment": string;
}

export interface SigMfMetadata {
  global: Record<string, string | number>;
  captures: Array<Record<string, number>>;
  annotations: SigMfAnnotation[];
}

function annotationComment(block: SignalBlock): string {
  if (block.kind === "tone") {
    return `Complex tone: fc=${block.centerFrequencyHz} Hz, amplitude=${block.amplitudeDbfs} dBFS, phase=${block.phaseRad} rad.`;
  }
  if (block.kind === "fm-radio") {
    return `Synthetic program FM: fc=${block.centerFrequencyHz} Hz, audio bandwidth=${block.audioBandwidthHz} Hz, deviation=${block.deviationHz} Hz, seed=${block.seed}, amplitude=${block.amplitudeDbfs} dBFS.`;
  }
  return `Single-tone FM: fc=${block.centerFrequencyHz} Hz, fm=${block.modulationFrequencyHz} Hz, deviation=${block.deviationHz} Hz, amplitude=${block.amplitudeDbfs} dBFS.`;
}

export function buildMetadata(project: SignalProject): SigMfMetadata {
  const global: Record<string, string | number> = {
    "core:datatype": "cf32_le",
    "core:sample_rate": project.sampleRateHz,
    "core:version": SIGMF_VERSION,
    "core:recorder": "IqChannelizer SigMF Generator",
    "core:description": "Synthetic complex IQ recording composed in the browser.",
  };
  const capture: Record<string, number> = { "core:sample_start": 0 };
  if (project.rfCenterHz !== undefined) capture["core:frequency"] = project.rfCenterHz;

  const annotations = sortSignals(project.signals).map((block): SigMfAnnotation => {
    const [basebandLow, basebandHigh] = frequencyBounds(block);
    const offset = project.rfCenterHz ?? 0;
    return {
      "core:sample_start": block.startSample,
      "core:sample_count": block.sampleCount,
      "core:freq_lower_edge": basebandLow + offset,
      "core:freq_upper_edge": basebandHigh + offset,
      "core:label": block.kind,
      "core:generator": "IqChannelizer SigMF Generator",
      "core:comment": annotationComment(block),
    };
  });

  return { global, captures: [capture], annotations };
}

export function encodeMetadata(project: SignalProject): Uint8Array {
  return new TextEncoder().encode(`${JSON.stringify(buildMetadata(project), null, 2)}\n`);
}
