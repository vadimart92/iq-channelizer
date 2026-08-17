/// <reference lib="webworker" />

import { computeMasterGain, encodeCf32Le, mixChunk } from "../dsp/mixer";
import { computeSpectralPreview } from "../dsp/preview";
import type { WorkerRequest, WorkerResponse } from "./protocol";

const scope = self as DedicatedWorkerGlobalScope;
let cancelled = false;
let busy = false;
let acknowledge: (() => void) | undefined;

function send(message: WorkerResponse, transfer: Transferable[] = []): void {
  scope.postMessage(message, transfer);
}

async function generate(request: Extract<WorkerRequest, { type: "generate" }>): Promise<void> {
  const chunkSamples = request.chunkSamples ?? 65_536;
  const gain = computeMasterGain(request.project);
  for (let firstSample = 0; firstSample < request.project.totalSamples; firstSample += chunkSamples) {
    if (cancelled) {
      send({ type: "cancelled" });
      return;
    }
    const count = Math.min(chunkSamples, request.project.totalSamples - firstSample);
    const bytes = encodeCf32Le(mixChunk(request.project, firstSample, count, gain));
    const transferable = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(transferable).set(bytes);
    send({ type: "chunk", firstSample, sampleCount: count, bytes: transferable }, [transferable]);
    await new Promise<void>((resolve) => {
      acknowledge = resolve;
    });
    acknowledge = undefined;
    if (cancelled) {
      send({ type: "cancelled" });
      return;
    }
    send({ type: "progress", completed: firstSample + count, total: request.project.totalSamples, masterGain: gain });
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  send({ type: "done", masterGain: gain });
}

async function preview(request: Extract<WorkerRequest, { type: "preview" }>): Promise<void> {
  const result = await computeSpectralPreview(
    request.project,
    request.width,
    request.fftSize,
    request.region,
    () => cancelled,
  );
  const buffer = new ArrayBuffer(result.power.byteLength);
  new Uint8Array(buffer).set(result.power);
  send({ type: "preview", width: result.width, height: result.height, power: buffer, region: result.region }, [buffer]);
}

scope.addEventListener("message", (event: MessageEvent<WorkerRequest>) => {
  if (event.data.type === "cancel") {
    cancelled = true;
    acknowledge?.();
    return;
  }
  if (event.data.type === "ack") {
    acknowledge?.();
    return;
  }
  if (busy) return;
  busy = true;
  cancelled = false;
  const operation = event.data.type === "generate" ? generate(event.data) : preview(event.data);
  void operation.catch((error: unknown) => {
    if (error instanceof DOMException && error.name === "AbortError") send({ type: "cancelled" });
    else send({ type: "error", message: error instanceof Error ? error.message : String(error) });
  }).finally(() => {
    busy = false;
  });
});
