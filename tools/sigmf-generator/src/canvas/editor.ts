import type { SpectralPreview } from "../dsp/preview";
import {
  blockEndSample,
  frequencyBounds,
  newSignalId,
  type FmBlock,
  type SignalBlock,
  type SignalKind,
  type SignalProject,
} from "../model/project";
import { Viewport } from "./viewport";

export type EditorTool = "select" | SignalKind;

interface PlotRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

type DragMode = "create" | "move" | "resize-start" | "resize-end" | "resize-high" | "resize-low" | "pan";

interface DragState {
  mode: DragMode;
  pointerId: number;
  startX: number;
  startY: number;
  startSample: number;
  startFrequencyHz: number;
  original?: SignalBlock;
}

export interface CanvasEditorCallbacks {
  onProjectChanged(): void;
  onSelectionChanged(id: string | undefined): void;
}

const palette = {
  tone: "#5eead4",
  fm: "#a78bfa",
  selected: "#fbbf24",
};

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}

function frequencyEpsilon(sampleRateHz: number): number {
  return Math.max(1e-9, sampleRateHz * 1e-12);
}

function formatMetric(value: number, unit: string): string {
  const absolute = Math.abs(value);
  if (absolute >= 1e9) return `${(value / 1e9).toFixed(2)} G${unit}`;
  if (absolute >= 1e6) return `${(value / 1e6).toFixed(2)} M${unit}`;
  if (absolute >= 1e3) return `${(value / 1e3).toFixed(2)} k${unit}`;
  if (absolute > 0 && absolute < 1e-3) return `${(value * 1e6).toFixed(1)} µ${unit}`;
  if (absolute > 0 && absolute < 1) return `${(value * 1e3).toFixed(1)} m${unit}`;
  return `${value.toFixed(2)} ${unit}`;
}

export class CanvasEditor {
  readonly #canvas: HTMLCanvasElement;
  readonly #context: CanvasRenderingContext2D;
  readonly #callbacks: CanvasEditorCallbacks;
  readonly viewport = new Viewport();
  #project: SignalProject;
  #tool: EditorTool = "select";
  #selectedId: string | undefined;
  #drag: DragState | undefined;
  #preview: SpectralPreview | undefined;
  #previewCanvas: HTMLCanvasElement | undefined;
  #dirty = true;
  #spacePressed = false;

  constructor(canvas: HTMLCanvasElement, project: SignalProject, callbacks: CanvasEditorCallbacks) {
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Canvas 2D is unavailable.");
    this.#canvas = canvas;
    this.#context = context;
    this.#project = project;
    this.#callbacks = callbacks;
    this.viewport.reset(project);
    this.#bindEvents();
    new ResizeObserver(() => this.invalidate()).observe(canvas);
    requestAnimationFrame(() => this.#frame());
  }

  get selectedId(): string | undefined {
    return this.#selectedId;
  }

  setProject(project: SignalProject, resetViewport = false): void {
    this.#project = project;
    if (resetViewport) this.viewport.reset(project);
    else this.viewport.clamp(project);
    if (this.#selectedId && !project.signals.some((signal) => signal.id === this.#selectedId)) {
      this.select(undefined);
    }
    this.invalidate();
  }

  setTool(tool: EditorTool): void {
    this.#tool = tool;
    this.#canvas.dataset.tool = tool;
    this.invalidate();
  }

  setPreview(preview: SpectralPreview | undefined): void {
    this.#preview = preview;
    this.#previewCanvas = preview ? this.#makePreviewCanvas(preview) : undefined;
    this.invalidate();
  }

  resetViewport(): void {
    this.viewport.reset(this.#project);
    this.invalidate();
  }

  select(id: string | undefined): void {
    this.#selectedId = id;
    this.#callbacks.onSelectionChanged(id);
    this.invalidate();
  }

  deleteSelected(): void {
    if (!this.#selectedId) return;
    const index = this.#project.signals.findIndex((signal) => signal.id === this.#selectedId);
    if (index < 0) return;
    this.#project.signals.splice(index, 1);
    this.select(undefined);
    this.#callbacks.onProjectChanged();
  }

  invalidate(): void {
    this.#dirty = true;
  }

  #plotRect(): PlotRect {
    return { left: 76, top: 18, width: Math.max(1, this.#canvas.clientWidth - 92), height: Math.max(1, this.#canvas.clientHeight - 58) };
  }

  #syncCanvasSize(): void {
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(this.#canvas.clientWidth * ratio));
    const height = Math.max(1, Math.round(this.#canvas.clientHeight * ratio));
    if (this.#canvas.width !== width || this.#canvas.height !== height) {
      this.#canvas.width = width;
      this.#canvas.height = height;
    }
    this.#context.setTransform(ratio, 0, 0, ratio, 0, 0);
  }

  #frame(): void {
    if (this.#dirty) {
      this.#syncCanvasSize();
      this.#draw();
      this.#dirty = false;
    }
    requestAnimationFrame(() => this.#frame());
  }

  #draw(): void {
    const ctx = this.#context;
    const width = this.#canvas.clientWidth;
    const height = this.#canvas.clientHeight;
    const plot = this.#plotRect();
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = "#07111f";
    ctx.fillRect(0, 0, width, height);
    ctx.fillStyle = "#0b1627";
    ctx.fillRect(plot.left, plot.top, plot.width, plot.height);
    this.#drawPreview(plot);
    this.#drawGrid(plot);
    for (const block of this.#project.signals) this.#drawBlock(block, plot);
    if (this.#drag?.mode === "create") this.#drawCreationGhost();
    ctx.strokeStyle = "#334155";
    ctx.strokeRect(plot.left + 0.5, plot.top + 0.5, plot.width - 1, plot.height - 1);
  }

  #drawPreview(plot: PlotRect): void {
    if (!this.#preview || !this.#previewCanvas) return;
    const sourceX = this.viewport.sampleStart / this.#project.totalSamples * this.#preview.width;
    const sourceWidth = (this.viewport.sampleEnd - this.viewport.sampleStart) / this.#project.totalSamples * this.#preview.width;
    const nyquist = this.#project.sampleRateHz / 2;
    const sourceY = (nyquist - this.viewport.frequencyHighHz) / this.#project.sampleRateHz * this.#preview.height;
    const sourceHeight = (this.viewport.frequencyHighHz - this.viewport.frequencyLowHz) / this.#project.sampleRateHz * this.#preview.height;
    this.#context.save();
    this.#context.globalAlpha = 0.62;
    this.#context.imageSmoothingEnabled = true;
    this.#context.drawImage(this.#previewCanvas, sourceX, sourceY, sourceWidth, sourceHeight, plot.left, plot.top, plot.width, plot.height);
    this.#context.restore();
  }

  #drawGrid(plot: PlotRect): void {
    const ctx = this.#context;
    ctx.font = "11px Inter, system-ui, sans-serif";
    ctx.lineWidth = 1;
    for (let index = 0; index <= 10; index += 1) {
      const ratio = index / 10;
      const x = plot.left + ratio * plot.width;
      const sample = this.viewport.sampleStart + ratio * (this.viewport.sampleEnd - this.viewport.sampleStart);
      ctx.strokeStyle = index === 0 || index === 10 ? "#334155" : "rgba(71, 85, 105, .32)";
      ctx.beginPath();
      ctx.moveTo(x + 0.5, plot.top);
      ctx.lineTo(x + 0.5, plot.top + plot.height);
      ctx.stroke();
      ctx.fillStyle = "#94a3b8";
      ctx.textAlign = "center";
      ctx.fillText(formatMetric(sample / this.#project.sampleRateHz, "s"), x, plot.top + plot.height + 21);
    }
    for (let index = 0; index <= 8; index += 1) {
      const ratio = index / 8;
      const y = plot.top + ratio * plot.height;
      const frequency = this.viewport.frequencyHighHz - ratio * (this.viewport.frequencyHighHz - this.viewport.frequencyLowHz);
      ctx.strokeStyle = Math.abs(frequency) < this.#project.sampleRateHz / 10000 ? "rgba(94, 234, 212, .45)" : "rgba(71, 85, 105, .32)";
      ctx.beginPath();
      ctx.moveTo(plot.left, y + 0.5);
      ctx.lineTo(plot.left + plot.width, y + 0.5);
      ctx.stroke();
      ctx.fillStyle = "#94a3b8";
      ctx.textAlign = "right";
      ctx.fillText(formatMetric(frequency, "Hz"), plot.left - 8, y + 4);
    }
  }

  #blockRect(block: SignalBlock, plot: PlotRect): DOMRect {
    const left = this.viewport.xForSample(block.startSample, plot.left, plot.width);
    const right = this.viewport.xForSample(blockEndSample(block), plot.left, plot.width);
    if (block.kind === "tone") {
      const centerY = this.viewport.yForFrequency(block.centerFrequencyHz, plot.top, plot.height);
      return new DOMRect(left, centerY - 7, Math.max(2, right - left), 14);
    }
    const [low, high] = frequencyBounds(block);
    const top = this.viewport.yForFrequency(high, plot.top, plot.height);
    const bottom = this.viewport.yForFrequency(low, plot.top, plot.height);
    return new DOMRect(left, top, Math.max(2, right - left), Math.max(5, bottom - top));
  }

  #drawBlock(block: SignalBlock, plot: PlotRect): void {
    const rect = this.#blockRect(block, plot);
    if (rect.right < plot.left || rect.left > plot.left + plot.width || rect.bottom < plot.top || rect.top > plot.top + plot.height) return;
    const ctx = this.#context;
    const selected = block.id === this.#selectedId;
    ctx.save();
    ctx.beginPath();
    ctx.rect(plot.left, plot.top, plot.width, plot.height);
    ctx.clip();
    ctx.fillStyle = block.kind === "tone" ? "rgba(45, 212, 191, .28)" : "rgba(139, 92, 246, .28)";
    ctx.strokeStyle = selected ? palette.selected : palette[block.kind];
    ctx.lineWidth = selected ? 2 : 1.25;
    ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
    ctx.strokeRect(rect.x, rect.y, rect.width, rect.height);
    ctx.fillStyle = selected ? palette.selected : "#e2e8f0";
    ctx.font = "600 11px Inter, system-ui, sans-serif";
    ctx.textAlign = "left";
    ctx.fillText(block.kind.toUpperCase(), rect.x + 6, Math.max(plot.top + 12, rect.y + 13));
    if (selected) {
      ctx.fillStyle = palette.selected;
      ctx.fillRect(rect.x - 3, rect.y + rect.height / 2 - 5, 6, 10);
      ctx.fillRect(rect.right - 3, rect.y + rect.height / 2 - 5, 6, 10);
      if (block.kind === "fm") {
        ctx.fillRect(rect.x + rect.width / 2 - 5, rect.y - 3, 10, 6);
        ctx.fillRect(rect.x + rect.width / 2 - 5, rect.bottom - 3, 10, 6);
      }
    }
    ctx.restore();
  }

  #drawCreationGhost(): void {
    const drag = this.#drag;
    if (!drag) return;
    const current = this.#lastPointer;
    if (!current) return;
    const left = Math.min(drag.startX, current.x);
    const right = Math.max(drag.startX, current.x);
    const top = this.#tool === "tone" ? current.y - 7 : Math.min(drag.startY, current.y);
    const bottom = this.#tool === "tone" ? current.y + 7 : Math.max(drag.startY, current.y);
    this.#context.save();
    this.#context.setLineDash([6, 4]);
    this.#context.strokeStyle = palette[this.#tool === "fm" ? "fm" : "tone"];
    this.#context.fillStyle = "rgba(94, 234, 212, .12)";
    this.#context.fillRect(left, top, right - left, bottom - top);
    this.#context.strokeRect(left, top, right - left, bottom - top);
    this.#context.restore();
  }

  #lastPointer: { x: number; y: number } | undefined;

  #makePreviewCanvas(preview: SpectralPreview): HTMLCanvasElement {
    const canvas = document.createElement("canvas");
    canvas.width = preview.width;
    canvas.height = preview.height;
    const context = canvas.getContext("2d");
    if (!context) return canvas;
    const image = context.createImageData(preview.width, preview.height);
    for (let index = 0; index < preview.power.length; index += 1) {
      const value = (preview.power[index] ?? 0) / 255;
      const pixel = index * 4;
      image.data[pixel] = Math.round(20 + 235 * Math.max(0, (value - 0.55) / 0.45));
      image.data[pixel + 1] = Math.round(35 + 205 * value);
      image.data[pixel + 2] = Math.round(70 + 150 * (1 - Math.abs(value - 0.5) * 2));
      image.data[pixel + 3] = Math.round(35 + 220 * value);
    }
    context.putImageData(image, 0, 0);
    return canvas;
  }

  #eventPoint(event: PointerEvent | WheelEvent): { x: number; y: number } {
    const bounds = this.#canvas.getBoundingClientRect();
    return { x: event.clientX - bounds.left, y: event.clientY - bounds.top };
  }

  #hitTest(x: number, y: number, plot: PlotRect): { block: SignalBlock; mode: DragMode } | undefined {
    for (const block of [...this.#project.signals].reverse()) {
      const rect = this.#blockRect(block, plot);
      if (x < rect.left - 5 || x > rect.right + 5 || y < rect.top - 5 || y > rect.bottom + 5) continue;
      if (Math.abs(x - rect.left) <= 7) return { block, mode: "resize-start" };
      if (Math.abs(x - rect.right) <= 7) return { block, mode: "resize-end" };
      if (block.kind === "fm" && Math.abs(y - rect.top) <= 7) return { block, mode: "resize-high" };
      if (block.kind === "fm" && Math.abs(y - rect.bottom) <= 7) return { block, mode: "resize-low" };
      return { block, mode: "move" };
    }
    return undefined;
  }

  #bindEvents(): void {
    this.#canvas.addEventListener("pointerdown", (event) => this.#pointerDown(event));
    this.#canvas.addEventListener("pointermove", (event) => this.#pointerMove(event));
    this.#canvas.addEventListener("pointerup", (event) => this.#pointerUp(event));
    this.#canvas.addEventListener("pointercancel", (event) => this.#pointerUp(event, true));
    this.#canvas.addEventListener("wheel", (event) => this.#wheel(event), { passive: false });
    window.addEventListener("keydown", (event) => {
      if (event.code === "Space" && !(event.target instanceof HTMLInputElement)) {
        this.#spacePressed = true;
        event.preventDefault();
      }
      if ((event.key === "Delete" || event.key === "Backspace") && !(event.target instanceof HTMLInputElement)) {
        this.deleteSelected();
        event.preventDefault();
      }
      if (event.key === "Escape") this.#cancelDrag();
    });
    window.addEventListener("keyup", (event) => {
      if (event.code === "Space") this.#spacePressed = false;
    });
  }

  #pointerDown(event: PointerEvent): void {
    const point = this.#eventPoint(event);
    const plot = this.#plotRect();
    if (point.x < plot.left || point.x > plot.left + plot.width || point.y < plot.top || point.y > plot.top + plot.height) return;
    const sample = this.viewport.sampleAt(point.x, plot.left, plot.width);
    const frequency = this.viewport.frequencyAt(point.y, plot.top, plot.height);
    let mode: DragMode;
    let original: SignalBlock | undefined;
    if (event.button === 1 || this.#spacePressed) {
      mode = "pan";
    } else if (this.#tool === "select") {
      const hit = this.#hitTest(point.x, point.y, plot);
      if (!hit) {
        this.select(undefined);
        return;
      }
      this.select(hit.block.id);
      mode = hit.mode;
      original = structuredClone(hit.block);
    } else {
      mode = "create";
    }
    this.#drag = { mode, pointerId: event.pointerId, startX: point.x, startY: point.y, startSample: sample, startFrequencyHz: frequency, original };
    this.#lastPointer = point;
    this.#canvas.setPointerCapture(event.pointerId);
    this.invalidate();
  }

  #pointerMove(event: PointerEvent): void {
    const point = this.#eventPoint(event);
    this.#lastPointer = point;
    const drag = this.#drag;
    if (!drag || drag.pointerId !== event.pointerId) {
      this.invalidate();
      return;
    }
    const plot = this.#plotRect();
    const sample = Math.round(this.viewport.sampleAt(point.x, plot.left, plot.width));
    const frequency = this.viewport.frequencyAt(point.y, plot.top, plot.height);
    if (drag.mode === "pan") {
      const previousSample = this.viewport.sampleAt(drag.startX, plot.left, plot.width);
      const previousFrequency = this.viewport.frequencyAt(drag.startY, plot.top, plot.height);
      this.viewport.panSamples(previousSample - sample, this.#project);
      this.viewport.panFrequency(previousFrequency - frequency, this.#project);
      drag.startX = point.x;
      drag.startY = point.y;
      this.invalidate();
      return;
    }
    if (drag.mode === "create" || !drag.original) {
      this.invalidate();
      return;
    }
    const block = this.#project.signals.find((candidate) => candidate.id === drag.original?.id);
    if (!block) return;
    const original = drag.original;
    if (drag.mode === "move") {
      const deltaSamples = sample - Math.round(drag.startSample);
      block.startSample = clamp(original.startSample + deltaSamples, 0, this.#project.totalSamples - original.sampleCount);
      const deltaFrequency = frequency - drag.startFrequencyHz;
      const [low, high] = frequencyBounds(original);
      const nyquist = this.#project.sampleRateHz / 2;
      block.centerFrequencyHz = original.centerFrequencyHz + clamp(deltaFrequency, -nyquist - low, nyquist - high - frequencyEpsilon(this.#project.sampleRateHz));
    } else if (drag.mode === "resize-start") {
      const end = blockEndSample(original);
      block.startSample = clamp(sample, 0, end - 1);
      block.sampleCount = end - block.startSample;
      block.fadeSamples = Math.min(block.fadeSamples, Math.floor(block.sampleCount / 2));
    } else if (drag.mode === "resize-end") {
      const end = clamp(sample, original.startSample + 1, this.#project.totalSamples);
      block.startSample = original.startSample;
      block.sampleCount = end - original.startSample;
      block.fadeSamples = Math.min(block.fadeSamples, Math.floor(block.sampleCount / 2));
    } else if (block.kind === "fm" && original.kind === "fm") {
      this.#resizeFm(block, original, frequency, drag.mode);
    }
    this.#callbacks.onProjectChanged();
    this.invalidate();
  }

  #resizeFm(block: FmBlock, original: FmBlock, frequency: number, mode: DragMode): void {
    const [originalLow, originalHigh] = frequencyBounds(original);
    const nyquist = this.#project.sampleRateHz / 2;
    const minimumBandwidth = 2 * original.modulationFrequencyHz;
    let low = originalLow;
    let high = originalHigh;
    if (mode === "resize-high") high = clamp(frequency, low + minimumBandwidth, nyquist - frequencyEpsilon(this.#project.sampleRateHz));
    else low = clamp(frequency, -nyquist, high - minimumBandwidth);
    block.centerFrequencyHz = (low + high) / 2;
    block.deviationHz = Math.max(0, (high - low) / 2 - block.modulationFrequencyHz);
  }

  #pointerUp(event: PointerEvent, cancelled = false): void {
    const drag = this.#drag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    if (cancelled) {
      this.#cancelDrag();
      return;
    }
    if (drag.mode === "create") this.#commitCreation(event, drag);
    this.#drag = undefined;
    this.#lastPointer = undefined;
    this.#canvas.releasePointerCapture(event.pointerId);
    this.invalidate();
  }

  #commitCreation(event: PointerEvent, drag: DragState): void {
    const point = this.#eventPoint(event);
    const plot = this.#plotRect();
    const endSample = Math.round(this.viewport.sampleAt(point.x, plot.left, plot.width));
    const first = clamp(Math.min(Math.round(drag.startSample), endSample), 0, this.#project.totalSamples - 1);
    const end = clamp(Math.max(Math.round(drag.startSample), endSample), first + 1, this.#project.totalSamples);
    const currentFrequency = this.viewport.frequencyAt(point.y, plot.top, plot.height);
    const fadeSamples = Math.min(Math.round(this.#project.sampleRateHz * 0.001), Math.floor((end - first) / 2));
    let block: SignalBlock;
    if (this.#tool === "fm") {
      const nyquist = this.#project.sampleRateHz / 2;
      const bandwidth = Math.min(this.#project.sampleRateHz * 0.98, Math.max(Math.abs(currentFrequency - drag.startFrequencyHz), this.#project.sampleRateHz / 100));
      const center = clamp((currentFrequency + drag.startFrequencyHz) / 2, -nyquist + bandwidth / 2, nyquist - bandwidth / 2 - frequencyEpsilon(this.#project.sampleRateHz));
      block = {
        id: newSignalId(), kind: "fm", startSample: first, sampleCount: end - first,
        centerFrequencyHz: center, amplitudeDbfs: -10, phaseRad: 0, fadeSamples,
        modulationFrequencyHz: bandwidth / 4, deviationHz: bandwidth / 4, modulationPhaseRad: 0,
      };
    } else {
      block = {
        id: newSignalId(), kind: "tone", startSample: first, sampleCount: end - first,
        centerFrequencyHz: clamp((currentFrequency + drag.startFrequencyHz) / 2, -this.#project.sampleRateHz / 2, this.#project.sampleRateHz / 2 - frequencyEpsilon(this.#project.sampleRateHz)),
        amplitudeDbfs: -6, phaseRad: 0, fadeSamples,
      };
    }
    this.#project.signals.push(block);
    this.select(block.id);
    this.#callbacks.onProjectChanged();
  }

  #cancelDrag(): void {
    if (this.#drag?.original) {
      const index = this.#project.signals.findIndex((signal) => signal.id === this.#drag?.original?.id);
      if (index >= 0) this.#project.signals[index] = this.#drag.original;
      this.#callbacks.onProjectChanged();
    }
    this.#drag = undefined;
    this.#lastPointer = undefined;
    this.invalidate();
  }

  #wheel(event: WheelEvent): void {
    event.preventDefault();
    const point = this.#eventPoint(event);
    const plot = this.#plotRect();
    if (event.shiftKey) {
      this.viewport.panSamples(event.deltaY / plot.width * (this.viewport.sampleEnd - this.viewport.sampleStart), this.#project);
    } else if (event.altKey) {
      const frequency = this.viewport.frequencyAt(point.y, plot.top, plot.height);
      this.viewport.zoomFrequency(frequency, Math.exp(event.deltaY * 0.0015), this.#project);
    } else {
      const sample = this.viewport.sampleAt(point.x, plot.left, plot.width);
      this.viewport.zoomTime(sample, Math.exp(event.deltaY * 0.0015), this.#project);
    }
    this.invalidate();
  }
}
