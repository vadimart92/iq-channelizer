import { totalDataBytes, type SignalProject } from "../model/project";
import { TarWriter, type ByteSink } from "../sigmf/archive";
import { BlobSink, FileSystemSink, canStreamToFile } from "../sigmf/byte-sink";
import { encodeMetadata } from "../sigmf/metadata";
import type { WorkerRequest, WorkerResponse } from "../worker/protocol";

export interface ExportCallbacks {
  onProgress(progress: number, masterGain: number): void;
}

export interface ExportSession {
  result: Promise<number>;
  cancel(): void;
}

function archiveEstimate(project: SignalProject, metadataBytes: number): number {
  const padded = (value: number): number => Math.ceil(value / 512) * 512;
  return 512 + padded(totalDataBytes(project)) + 512 + padded(metadataBytes) + 1024;
}

async function chooseSink(project: SignalProject, metadataBytes: number): Promise<ByteSink> {
  const filename = `${project.basename}.sigmf`;
  if (canStreamToFile()) return FileSystemSink.create(filename);
  return new BlobSink(filename, archiveEstimate(project, metadataBytes));
}

export function exportRecording(
  project: SignalProject,
  callbacks: ExportCallbacks,
): ExportSession {
  const worker = new Worker(new URL("../worker/generate.worker.ts", import.meta.url), { type: "module" });
  let writer: TarWriter | undefined;
  let cancelled = false;

  const operation = (async (): Promise<number> => {
    const metadata = encodeMetadata(project);
    const sink = await chooseSink(project, metadata.byteLength);
    writer = new TarWriter(sink);
    await writer.startStreamingFile(`${project.basename}.sigmf-data`, totalDataBytes(project));

    return new Promise<number>((resolve, reject) => {
      let writeChain = Promise.resolve();
      const fail = (error: unknown): void => {
        worker.terminate();
        void writer?.abort(error).finally(() => reject(error));
      };

      worker.onmessage = (event: MessageEvent<WorkerResponse>): void => {
        const message = event.data;
        if (message.type === "chunk") {
          writeChain = writeChain.then(async () => {
            await writer?.write(new Uint8Array(message.bytes));
            const acknowledgement: WorkerRequest = { type: "ack" };
            worker.postMessage(acknowledgement);
          }).catch(fail);
        } else if (message.type === "progress") {
          callbacks.onProgress(message.completed / message.total, message.masterGain);
        } else if (message.type === "done") {
          writeChain.then(async () => {
            if (!writer) throw new Error("Archive writer is unavailable.");
            await writer.endFile();
            await writer.writeFile(`${project.basename}.sigmf-meta`, metadata);
            await writer.finish();
            worker.terminate();
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

  const result = operation.finally(() => worker.terminate());
  return {
    result,
    cancel(): void {
      if (cancelled) return;
      cancelled = true;
      const request: WorkerRequest = { type: "cancel" };
      worker.postMessage(request);
    },
  };
}
