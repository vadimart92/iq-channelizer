import { DEFAULT_BLOB_LIMIT_BYTES } from "../model/project";
import type { ByteSink } from "./archive";

declare global {
  interface Window {
    showSaveFilePicker?: (options?: {
      suggestedName?: string;
      types?: Array<{ description: string; accept: Record<string, string[]> }>;
    }) => Promise<FileSystemFileHandle>;
  }
}

export class BlobSink implements ByteSink {
  readonly #filename: string;
  readonly #mimeType: string;
  readonly #limit: number;
  readonly #parts: BlobPart[] = [];
  #bytes = 0;

  constructor(filename: string, estimatedBytes: number, mimeType = "application/octet-stream", limit = DEFAULT_BLOB_LIMIT_BYTES) {
    if (estimatedBytes > limit) {
      throw new Error(`Portable download is limited to ${Math.round(limit / 1024 ** 2)} MiB. Use a Chromium browser with file streaming or reduce the recording.`);
    }
    this.#filename = filename;
    this.#mimeType = mimeType;
    this.#limit = limit;
  }

  async write(chunk: Uint8Array): Promise<void> {
    this.#bytes += chunk.byteLength;
    if (this.#bytes > this.#limit) throw new Error("Blob download exceeded its memory guard.");
    this.#parts.push(chunk.slice().buffer);
  }

  async close(): Promise<void> {
    const url = URL.createObjectURL(new Blob(this.#parts, { type: this.#mimeType }));
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = this.#filename;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }

  async abort(): Promise<void> {
    this.#parts.length = 0;
  }
}

export class FileSystemSink implements ByteSink {
  readonly #stream: FileSystemWritableFileStream;

  private constructor(stream: FileSystemWritableFileStream) {
    this.#stream = stream;
  }

  static async create(
    filename: string,
    description: string,
    mimeType: string,
    extension: string,
  ): Promise<FileSystemSink> {
    if (!window.showSaveFilePicker) throw new Error("Streaming file picker is unavailable.");
    const handle = await window.showSaveFilePicker({
      suggestedName: filename,
      types: [{ description, accept: { [mimeType]: [extension] } }],
    });
    return new FileSystemSink(await handle.createWritable());
  }

  async write(chunk: Uint8Array): Promise<void> {
    await this.#stream.write(chunk as unknown as ArrayBuffer);
  }

  async close(): Promise<void> {
    await this.#stream.close();
  }

  async abort(reason?: unknown): Promise<void> {
    await this.#stream.abort(reason);
  }
}

export function canStreamToFile(): boolean {
  return window.isSecureContext && typeof window.showSaveFilePicker === "function";
}
