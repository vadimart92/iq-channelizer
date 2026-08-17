import type { SignalProject } from "../model/project";

export type WorkerRequest =
  | { type: "generate"; project: SignalProject; chunkSamples?: number }
  | { type: "preview"; project: SignalProject; width?: number; fftSize?: number }
  | { type: "ack" }
  | { type: "cancel" };

export type WorkerResponse =
  | { type: "chunk"; firstSample: number; sampleCount: number; bytes: ArrayBuffer }
  | { type: "progress"; completed: number; total: number; masterGain: number }
  | { type: "done"; masterGain: number }
  | { type: "preview"; width: number; height: number; power: ArrayBuffer }
  | { type: "cancelled" }
  | { type: "error"; message: string };
