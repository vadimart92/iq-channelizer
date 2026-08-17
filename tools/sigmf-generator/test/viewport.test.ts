import { describe, expect, it } from "vitest";
import { Viewport } from "../src/canvas/viewport";
import { createDefaultProject } from "../src/model/project";

describe("viewport", () => {
  it("round-trips screen and world coordinates", () => {
    const project = createDefaultProject();
    const viewport = new Viewport();
    viewport.reset(project);
    const x = viewport.xForSample(25_000, 70, 1000);
    expect(viewport.sampleAt(x, 70, 1000)).toBeCloseTo(25_000, 10);
    const y = viewport.yForFrequency(125_000, 20, 600);
    expect(viewport.frequencyAt(y, 20, 600)).toBeCloseTo(125_000, 10);
  });

  it("keeps the cursor anchor fixed while zooming", () => {
    const project = createDefaultProject();
    const viewport = new Viewport();
    viewport.reset(project);
    viewport.zoomTime(25_000, 0.5, project);
    expect(viewport.sampleStart).toBeCloseTo(12_500, 8);
    expect(viewport.sampleEnd).toBeCloseTo(62_500, 8);
  });
});
