import { totalDataBytes, type SignalProject } from "../model/project";
import { TarWriter, type ByteSink } from "../sigmf/archive";
import { BlobSink, FileSystemSink, canStreamToFile } from "../sigmf/byte-sink";
import { encodeMetadata } from "../sigmf/metadata";
import { createWavHeader } from "../wav/writer";
import type { WorkerRequest, WorkerResponse } from "../worker/protocol";

export interface ExportCallbacks {
  onProgress(progress: number, masterGain: number): void;
}

export interface ExportSession {
  result: Promise<number>;
  cancel(): void;
}

interface GenerationTarget {
  write(chunk: Uint8Array): Promise<void>;
  finish(): Promise<void>;
  abort(reason?: unknown): Promise<void>;
}

function archiveEstimate(project: SignalProject, metadataBytes: number): number {
  const padded = (value: number): number => Math.ceil(value / 512) * 512;
  return 512 + padded(totalDataBytes(project)) + 512 + padded(metadataBytes) + 1024;
}

async function chooseSink(
  filename: string,
  estimatedBytes: number,
  description: string,
  mimeType: string,
  extension: string,
): Promise<ByteSink> {
  if (canStreamToFile()) return FileSystemSink.create(filename, description, mimeType, extension);
  return new BlobSink(filename, estimatedBytes, mimeType);
}

async function createSigMfTarget(project: SignalProject): Promise<GenerationTarget> {
  const metadata = encodeMetadata(project);
  const sink = await chooseSink(
    `${project.basename}.sigmf`,
    archiveEstimate(project, metadata.byteLength),
    "SigMF Archive",
    "application/x-tar",
    ".sigmf",
  );
  const writer = new TarWriter(sink);
  await writer.startStreamingFile(`${project.basename}.sigmf-data`, totalDataBytes(project));
  return {
    write: (chunk) => writer.write(chunk),
    async finish(): Promise<void> {
      await writer.endFile();
      await writer.writeFile(`${project.basename}.sigmf-meta`, metadata);
      await writer.finish();
    },
    abort: (reason) => writer.abort(reason),
  };
}

async function createWavTarget(project: SignalProject): Promise<GenerationTarget> {
  const header = createWavHeader(project);
  const sink = await chooseSink(
    `${project.basename}.wav`,
    header.byteLength + totalDataBytes(project),
    "Stereo float32 IQ WAV",
    "audio/wav",
    ".wav",
  );
  await sink.write(header);
  return {
    write: (chunk) => sink.write(chunk),
    finish: () => sink.close(),
    abort: (reason) => sink.abort(reason),
  };
}

function exportGenerated(
  project: SignalProject,
  callbacks: ExportCallbacks,
  targetFactory: (project: SignalProject) => Promise<GenerationTarget>,
): ExportSession {
  const worker = new Worker(new URL("../worker/generate.worker.ts", import.meta.url), { type: "module" });
  let target: GenerationTarget | undefined;
  let cancelled = false;

  const operation = (async (): Promise<number> => {
    target = await targetFactory(project);
    if (cancelled) {
      await target.abort(new DOMException("Export cancelled", "AbortError"));
      throw new DOMException("Export cancelled", "AbortError");
    }
    return new Promise<number>((resolve, reject) => {
      let writeChain = Promise.resolve();
      let settled = false;
      const fail = (error: unknown): void => {
        if (settled) return;
        settled = true;
        worker.terminate();
        void target?.abort(error).finally(() => reject(error));
      };

      worker.onmessage = (event: MessageEvent<WorkerResponse>): void => {
        const message = event.data;
        if (message.type === "chunk") {
          writeChain = writeChain.then(async () => {
            await target?.write(new Uint8Array(message.bytes));
            const acknowledgement: WorkerRequest = { type: "ack" };
            worker.postMessage(acknowledgement);
          }).catch(fail);
        } else if (message.type === "progress") {
          callbacks.onProgress(message.completed / message.total, message.masterGain);
        } else if (message.type === "done") {
          void writeChain.then(async () => {
            if (!target) throw new Error("Export target is unavailable.");
            await target.finish();
            settled = true;
            resolve(message.masterGain);
          }).catch(fail);
        } else if (message.type === "cancelled") {
          fail(new DOMException("Export cancelled", "AbortError"));
        } else if (message.type === "error") {
          fail(new Error(message.message));
        }
      };
      worker.onerror = (event): void => fail(new Error(event.message));
      const request: WorkerRequest = { type: "generate", project };
      worker.postMessage(request);
    });
  })();

  return {
    result: operation.finally(() => worker.terminate()),
    cancel(): void {
      if (cancelled) return;
      cancelled = true;
      const request: WorkerRequest = { type: "cancel" };
      worker.postMessage(request);
    },
  };
}

export function exportRecording(project: SignalProject, callbacks: ExportCallbacks): ExportSession {
  return exportGenerated(project, callbacks, createSigMfTarget);
}

export function exportWave(project: SignalProject, callbacks: ExportCallbacks): ExportSession {
  return exportGenerated(project, callbacks, createWavTarget);
}
