import { describe, expect, it } from "vitest";
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmdirSync, unlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createDefaultProject } from "../src/model/project";
import { TarWriter, type ByteSink, createTarHeader } from "../src/sigmf/archive";
import { buildMetadata, encodeMetadata } from "../src/sigmf/metadata";

class MemorySink implements ByteSink {
  readonly chunks: Uint8Array[] = [];
  closed = false;

  async write(chunk: Uint8Array): Promise<void> { this.chunks.push(chunk.slice()); }
  async close(): Promise<void> { this.closed = true; }
  async abort(): Promise<void> { this.chunks.length = 0; }

  bytes(): Uint8Array {
    const length = this.chunks.reduce((sum, chunk) => sum + chunk.length, 0);
    const result = new Uint8Array(length);
    let offset = 0;
    for (const chunk of this.chunks) { result.set(chunk, offset); offset += chunk.length; }
    return result;
  }
}

function field(bytes: Uint8Array, offset: number, length: number): string {
  return new TextDecoder().decode(bytes.subarray(offset, offset + length)).replace(/\0.*$/, "").trim();
}

describe("SigMF metadata", () => {
  it("maps baseband annotations to absolute RF when a center is present", () => {
    const project = createDefaultProject();
    project.rfCenterHz = 100_000_000;
    project.signals.push({
      id: "tone", kind: "tone", startSample: 20_000, sampleCount: 60_000,
      centerFrequencyHz: 100_000, amplitudeDbfs: -6, phaseRad: 0, fadeSamples: 1000,
    });
    const metadata = buildMetadata(project);
    expect(metadata.global["core:datatype"]).toBe("cf32_le");
    expect(metadata.global["core:version"]).toBe("1.2.6");
    expect(metadata.captures[0]?.["core:frequency"]).toBe(100_000_000);
    expect(metadata.annotations[0]?.["core:freq_lower_edge"]).toBe(100_100_000);
    expect(metadata.annotations[0]?.["core:freq_upper_edge"]).toBe(100_100_000);
  });

  it("always emits the three required top-level objects", () => {
    const parsed = JSON.parse(new TextDecoder().decode(encodeMetadata(createDefaultProject()))) as Record<string, unknown>;
    expect(Object.keys(parsed)).toEqual(["global", "captures", "annotations"]);
  });
});

describe("ustar writer", () => {
  it("writes a valid header checksum", () => {
    const header = createTarHeader("recording.sigmf-data", 16);
    const expected = Number.parseInt(field(header, 148, 8), 8);
    const checksumHeader = header.slice();
    checksumHeader.fill(0x20, 148, 156);
    const actual = checksumHeader.reduce((sum, byte) => sum + byte, 0);
    expect(expected).toBe(actual);
    expect(field(header, 257, 6)).toBe("ustar");
  });

  it("writes two padded files and final zero blocks", async () => {
    const sink = new MemorySink();
    const writer = new TarWriter(sink);
    const data = new Uint8Array(20).fill(0x5a);
    const metadata = new TextEncoder().encode("{}\n");
    await writer.startStreamingFile("recording.sigmf-data", data.length);
    await writer.write(data.subarray(0, 7));
    await writer.write(data.subarray(7));
    await writer.endFile();
    await writer.writeFile("recording.sigmf-meta", metadata);
    await writer.finish();

    const archive = sink.bytes();
    expect(sink.closed).toBe(true);
    expect(archive.length).toBe(512 + 512 + 512 + 512 + 1024);
    expect(field(archive, 0, 100)).toBe("recording.sigmf-data");
    expect(field(archive, 1024, 100)).toBe("recording.sigmf-meta");
    expect(archive.slice(-1024).every((byte) => byte === 0)).toBe(true);
  });

  it("is accepted by the system tar reader", async () => {
    const sink = new MemorySink();
    const writer = new TarWriter(sink);
    await writer.writeFile("recording.sigmf-data", new Uint8Array(16));
    await writer.writeFile("recording.sigmf-meta", new TextEncoder().encode("{}\n"));
    await writer.finish();

    const directory = mkdtempSync(join(tmpdir(), "iqgen-tar-"));
    const archivePath = join(directory, "recording.sigmf");
    try {
      writeFileSync(archivePath, sink.bytes());
      const listing = execFileSync("tar", ["-tf", archivePath], { encoding: "utf8" });
      expect(listing.replaceAll("\r", "").trim().split("\n")).toEqual([
        "recording.sigmf-data",
        "recording.sigmf-meta",
      ]);
    } finally {
      unlinkSync(archivePath);
      rmdirSync(directory);
    }
  });
});
