import type { PreviewRegion, SpectralPreview } from "../dsp/preview";
import type { SignalProject } from "../model/project";
import type { WorkerRequest, WorkerResponse } from "../worker/protocol";

export interface PreviewSession {
  result: Promise<SpectralPreview>;
  cancel(): void;
}

export function requestSpectralPreview(project: SignalProject, fftSize = 256, region?: PreviewRegion): PreviewSession {
  const worker = new Worker(new URL("../worker/generate.worker.ts", import.meta.url), { type: "module" });
  const result = new Promise<SpectralPreview>((resolve, reject) => {
    worker.onmessage = (event: MessageEvent<WorkerResponse>): void => {
      const message = event.data;
      if (message.type === "preview") {
        worker.terminate();
        resolve({ width: message.width, height: message.height, power: new Uint8Array(message.power), region: message.region });
      } else if (message.type === "error") {
        worker.terminate();
        reject(new Error(message.message));
      } else if (message.type === "cancelled") {
        worker.terminate();
        reject(new DOMException("Preview cancelled", "AbortError"));
      }
    };
    worker.onerror = (event): void => {
      worker.terminate();
      reject(new Error(event.message));
    };
    const request: WorkerRequest = { type: "preview", project, fftSize, region };
    worker.postMessage(request);
  });
  return {
    result,
    cancel(): void {
      const request: WorkerRequest = { type: "cancel" };
      worker.postMessage(request);
    },
  };
}
