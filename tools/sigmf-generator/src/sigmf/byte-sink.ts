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
  readonly #parts: Uint8Array<ArrayBuffer>[] = [];
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
    const copy = new Uint8Array(chunk.byteLength);
    copy.set(chunk);
    this.#parts.push(copy);
  }

  async rewriteStart(chunk: Uint8Array): Promise<void> {
    if (chunk.byteLength > this.#bytes) throw new Error("Cannot rewrite beyond the generated download.");
    let sourceOffset = 0;
    for (const part of this.#parts) {
      const count = Math.min(part.byteLength, chunk.byteLength - sourceOffset);
      if (count <= 0) break;
      part.set(chunk.subarray(sourceOffset, sourceOffset + count));
      sourceOffset += count;
    }
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
  #position = 0;

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
    this.#position += chunk.byteLength;
  }

  async rewriteStart(chunk: Uint8Array): Promise<void> {
    if (chunk.byteLength > this.#position) throw new Error("Cannot rewrite beyond the generated file.");
    const restorePosition = this.#position;
    await this.#stream.seek(0);
    await this.#stream.write(chunk as unknown as ArrayBuffer);
    await this.#stream.seek(restorePosition);
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
