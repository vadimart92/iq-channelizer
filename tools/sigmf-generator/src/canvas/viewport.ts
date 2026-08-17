import type { SignalProject } from "../model/project";

export class Viewport {
  sampleStart = 0;
  sampleEnd = 1;
  frequencyLowHz = -0.5;
  frequencyHighHz = 0.5;

  reset(project: SignalProject): void {
    this.sampleStart = 0;
    this.sampleEnd = project.totalSamples;
    this.frequencyLowHz = -project.sampleRateHz / 2;
    this.frequencyHighHz = project.sampleRateHz / 2;
  }

  sampleAt(x: number, left: number, width: number): number {
    return this.sampleStart + (x - left) / width * (this.sampleEnd - this.sampleStart);
  }

  frequencyAt(y: number, top: number, height: number): number {
    return this.frequencyHighHz - (y - top) / height * (this.frequencyHighHz - this.frequencyLowHz);
  }

  xForSample(sample: number, left: number, width: number): number {
    return left + (sample - this.sampleStart) / (this.sampleEnd - this.sampleStart) * width;
  }

  yForFrequency(frequencyHz: number, top: number, height: number): number {
    return top + (this.frequencyHighHz - frequencyHz) / (this.frequencyHighHz - this.frequencyLowHz) * height;
  }

  zoomTime(anchorSample: number, factor: number, project: SignalProject): void {
    const minimumSpan = Math.min(project.totalSamples, 16);
    const currentSpan = this.sampleEnd - this.sampleStart;
    const span = Math.max(minimumSpan, Math.min(project.totalSamples, currentSpan * factor));
    const ratio = currentSpan > 0 ? (anchorSample - this.sampleStart) / currentSpan : 0.5;
    this.sampleStart = anchorSample - span * ratio;
    this.sampleEnd = this.sampleStart + span;
    this.clamp(project);
  }

  zoomFrequency(anchorHz: number, factor: number, project: SignalProject): void {
    const fullSpan = project.sampleRateHz;
    const currentSpan = this.frequencyHighHz - this.frequencyLowHz;
    const span = Math.max(fullSpan / 10_000, Math.min(fullSpan, currentSpan * factor));
    const ratio = currentSpan > 0 ? (anchorHz - this.frequencyLowHz) / currentSpan : 0.5;
    this.frequencyLowHz = anchorHz - span * ratio;
    this.frequencyHighHz = this.frequencyLowHz + span;
    this.clamp(project);
  }

  panSamples(delta: number, project: SignalProject): void {
    this.sampleStart += delta;
    this.sampleEnd += delta;
    this.clamp(project);
  }

  panFrequency(deltaHz: number, project: SignalProject): void {
    this.frequencyLowHz += deltaHz;
    this.frequencyHighHz += deltaHz;
    this.clamp(project);
  }

  clamp(project: SignalProject): void {
    const sampleSpan = Math.min(project.totalSamples, this.sampleEnd - this.sampleStart);
    this.sampleStart = Math.max(0, Math.min(project.totalSamples - sampleSpan, this.sampleStart));
    this.sampleEnd = this.sampleStart + sampleSpan;

    const nyquist = project.sampleRateHz / 2;
    const frequencySpan = Math.min(project.sampleRateHz, this.frequencyHighHz - this.frequencyLowHz);
    this.frequencyLowHz = Math.max(-nyquist, Math.min(nyquist - frequencySpan, this.frequencyLowHz));
    this.frequencyHighHz = this.frequencyLowHz + frequencySpan;
  }
}
