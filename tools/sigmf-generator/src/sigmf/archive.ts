export interface ByteSink {
  write(chunk: Uint8Array): Promise<void>;
  rewriteStart(chunk: Uint8Array): Promise<void>;
  close(): Promise<void>;
  abort(reason?: unknown): Promise<void>;
}

const TAR_BLOCK_SIZE = 512;
const textEncoder = new TextEncoder();

function writeAscii(target: Uint8Array, offset: number, length: number, value: string): void {
  const bytes = textEncoder.encode(value);
  if (bytes.length > length) throw new RangeError(`Tar field is too long: ${value}`);
  target.set(bytes, offset);
}

function writeOctal(target: Uint8Array, offset: number, length: number, value: number): void {
  const octal = Math.trunc(value).toString(8).padStart(length - 1, "0");
  if (octal.length > length - 1) throw new RangeError("Value does not fit in tar octal field.");
  writeAscii(target, offset, length - 1, octal);
  target[offset + length - 1] = 0;
}

export function createTarHeader(name: string, size: number): Uint8Array {
  const header = new Uint8Array(TAR_BLOCK_SIZE);
  writeAscii(header, 0, 100, name);
  writeOctal(header, 100, 8, 0o644);
  writeOctal(header, 108, 8, 0);
  writeOctal(header, 116, 8, 0);
  writeOctal(header, 124, 12, size);
  writeOctal(header, 136, 12, 0);
  header.fill(0x20, 148, 156);
  header[156] = "0".charCodeAt(0);
  writeAscii(header, 257, 6, "ustar\0");
  writeAscii(header, 263, 2, "00");
  writeAscii(header, 265, 32, "iqgen");
  writeAscii(header, 297, 32, "iqgen");
  let checksum = 0;
  for (const byte of header) checksum += byte;
  const checksumText = checksum.toString(8).padStart(6, "0");
  writeAscii(header, 148, 6, checksumText);
  header[154] = 0;
  header[155] = 0x20;
  return header;
}

export class TarWriter {
  readonly #sink: ByteSink;
  #remaining = 0;
  #declaredSize = 0;
  #openName = "";
  #open = false;
  #finished = false;

  constructor(sink: ByteSink) {
    this.#sink = sink;
  }

  async startFile(name: string, size: number): Promise<void> {
    if (this.#finished || this.#open) throw new Error("Tar writer is not ready for a new file.");
    await this.#sink.write(createTarHeader(name, size));
    this.#remaining = size;
    this.#declaredSize = size;
    this.#openName = name;
    this.#open = true;
  }

  async write(chunk: Uint8Array): Promise<void> {
    if (!this.#open || chunk.byteLength > this.#remaining) throw new Error("Tar file payload exceeds its declared size.");
    await this.#sink.write(chunk);
    this.#remaining -= chunk.byteLength;
  }

  async endFile(): Promise<void> {
    if (!this.#open || this.#remaining !== 0) throw new Error("Tar file payload is incomplete.");
    const padding = (TAR_BLOCK_SIZE - (this.#remainingFileSize % TAR_BLOCK_SIZE)) % TAR_BLOCK_SIZE;
    if (padding > 0) await this.#sink.write(new Uint8Array(padding));
    this.#open = false;
    this.#remainingFileSize = 0;
  }

  async endFileEarly(actualSize: number): Promise<void> {
    const written = this.#declaredSize - this.#remaining;
    if (!this.#open || actualSize !== written) throw new Error("Tar partial file size does not match the written payload.");
    await this.#sink.rewriteStart(createTarHeader(this.#openName, actualSize));
    this.#remaining = 0;
    this.#remainingFileSize = actualSize;
    await this.endFile();
  }

  #remainingFileSize = 0;

  async writeFile(name: string, data: Uint8Array): Promise<void> {
    await this.startFile(name, data.byteLength);
    this.#remainingFileSize = data.byteLength;
    await this.write(data);
    await this.endFile();
  }

  async startStreamingFile(name: string, size: number): Promise<void> {
    await this.startFile(name, size);
    this.#remainingFileSize = size;
  }

  async finish(): Promise<void> {
    if (this.#open || this.#finished) throw new Error("Tar writer cannot be finished in its current state.");
    await this.#sink.write(new Uint8Array(TAR_BLOCK_SIZE * 2));
    await this.#sink.close();
    this.#finished = true;
  }

  async abort(reason?: unknown): Promise<void> {
    this.#finished = true;
    await this.#sink.abort(reason);
  }
}
