import "./styles.css";

import { exportRecording, type ExportSession } from "./app/exporter";
import { downloadText, parseProject, serializeProject } from "./app/project-io";
import { requestSpectralPreview, type PreviewSession } from "./app/spectral-preview";
import { hasErrors, validateProject, type ValidationIssue } from "./app/validation";
import { CanvasEditor, type EditorTool } from "./canvas/editor";
import {
  cloneProject,
  createDefaultProject,
  durationSeconds,
  frequencyBounds,
  occupiedBandwidthHz,
  totalDataBytes,
  type SignalBlock,
} from "./model/project";

function element<T extends HTMLElement>(id: string): T {
  const value = document.getElementById(id);
  if (!value) throw new Error(`Missing element #${id}.`);
  return value as T;
}

function input(id: string): HTMLInputElement {
  return element<HTMLInputElement>(id);
}

function formatBytes(bytes: number): string {
  const units = ["B", "KiB", "MiB", "GiB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toLocaleString(undefined, { maximumFractionDigits: unit === 0 ? 0 : 1 })} ${units[unit]}`;
}

function degrees(radians: number): number {
  return radians * 180 / Math.PI;
}

function radians(degreesValue: number): number {
  return degreesValue * Math.PI / 180;
}

let project = createDefaultProject();
let exportSession: ExportSession | undefined;
let previewSession: PreviewSession | undefined;
let previewTimer: number | undefined;
let exporting = false;

const canvas = element<HTMLCanvasElement>("signal-canvas");
const signalForm = element<HTMLFormElement>("signal-form");
const emptyInspector = element<HTMLDivElement>("empty-inspector");
const fmFields = element<HTMLDivElement>("fm-fields");
const downloadButton = element<HTMLButtonElement>("download");
const cancelButton = element<HTMLButtonElement>("cancel-export");
const progressWrap = element<HTMLDivElement>("progress-wrap");
const progressBar = element<HTMLSpanElement>("progress-bar");
const progressLabel = element<HTMLSpanElement>("progress-label");

const editor = new CanvasEditor(canvas, project, {
  onProjectChanged(): void {
    renderProjectState();
    renderInspector();
    schedulePreview();
  },
  onSelectionChanged(): void {
    renderInspector();
  },
});

function selectedBlock(): SignalBlock | undefined {
  return project.signals.find((signal) => signal.id === editor.selectedId);
}

function setStatus(title: string, detail: string, severity: "ok" | "warning" | "error" = "ok"): void {
  element("status-title").textContent = title;
  element("status-detail").textContent = detail;
  element("status-dot").className = `status-dot${severity === "ok" ? "" : ` ${severity}`}`;
}

function issueSummary(issues: readonly ValidationIssue[]): string {
  return issues.slice(0, 3).map((issue) => issue.message).join(" ");
}

function renderProjectState(): void {
  element("sample-summary").textContent = `${project.totalSamples.toLocaleString()} samples`;
  element("size-summary").textContent = formatBytes(totalDataBytes(project));
  const issues = validateProject(project);
  downloadButton.disabled = exporting || hasErrors(issues);
  if (exporting) return;
  const errors = issues.filter((issue) => issue.severity === "error");
  const warnings = issues.filter((issue) => issue.severity === "warning");
  if (errors.length > 0) setStatus(`${errors.length} validation error${errors.length === 1 ? "" : "s"}`, issueSummary(errors), "error");
  else if (warnings.length > 0) setStatus(`${warnings.length} warning${warnings.length === 1 ? "" : "s"}`, issueSummary(warnings), "warning");
  else if (project.signals.length === 0) setStatus("Ready", "Empty canvas will export zero IQ.");
  else setStatus("Ready to export", `${project.signals.length} signal block${project.signals.length === 1 ? "" : "s"} · cf32_le · SigMF 1.2.6`);
  editor.invalidate();
}

function renderTopFields(): void {
  input("basename").value = project.basename;
  input("sample-rate").value = String(project.sampleRateHz);
  input("duration").value = String(durationSeconds(project));
  input("rf-center").value = project.rfCenterHz === undefined ? "" : String(project.rfCenterHz);
}

function renderInspector(): void {
  const block = selectedBlock();
  signalForm.hidden = !block;
  emptyInspector.hidden = Boolean(block);
  element("selection-kind").textContent = block ? `${block.kind.toUpperCase()} · ${block.id.slice(0, 8)}` : "No selection";
  if (!block) return;
  input("signal-start").value = String(block.startSample / project.sampleRateHz);
  input("signal-duration").value = String(block.sampleCount / project.sampleRateHz);
  input("signal-center").value = String(block.centerFrequencyHz);
  input("signal-amplitude").value = String(block.amplitudeDbfs);
  input("signal-phase").value = String(degrees(block.phaseRad));
  input("signal-fade").value = String(block.fadeSamples / project.sampleRateHz * 1000);
  fmFields.hidden = block.kind !== "fm";
  if (block.kind === "fm") {
    input("signal-fm").value = String(block.modulationFrequencyHz);
    input("signal-deviation").value = String(block.deviationHz);
    input("signal-mod-phase").value = String(degrees(block.modulationPhaseRad));
    const [low, high] = frequencyBounds(block);
    element("signal-bandwidth").textContent = `Carson bandwidth: ${occupiedBandwidthHz(block).toLocaleString()} Hz (${low.toLocaleString()} … ${high.toLocaleString()} Hz)`;
  }
}

function updateSelected(mutator: (block: SignalBlock) => void): void {
  const block = selectedBlock();
  if (!block) return;
  mutator(block);
  block.fadeSamples = Math.max(0, Math.min(Math.floor(block.sampleCount / 2), Math.round(block.fadeSamples)));
  renderProjectState();
  editor.invalidate();
  schedulePreview();
}

function schedulePreview(): void {
  if (previewTimer !== undefined) window.clearTimeout(previewTimer);
  previewSession?.cancel();
  previewSession = undefined;
  if (!input("spectral-preview").checked || project.signals.length === 0 || hasErrors(validateProject(project))) {
    editor.setPreview(undefined);
    return;
  }
  previewTimer = window.setTimeout(() => {
    const session = requestSpectralPreview(cloneProject(project));
    previewSession = session;
    void session.result.then((preview) => {
      if (previewSession === session) editor.setPreview(preview);
    }).catch((error: unknown) => {
      if (!(error instanceof DOMException && error.name === "AbortError")) console.warn("Spectral preview failed", error);
    });
  }, 320);
}

function rescaleForSampleRate(nextRate: number): void {
  if (!Number.isFinite(nextRate) || nextRate <= 0 || nextRate > 1e12) {
    input("sample-rate").value = String(project.sampleRateHz);
    setStatus("Invalid sample rate", "Enter a finite value greater than zero and no more than 1e12 Hz.", "error");
    return;
  }
  const previousRate = project.sampleRateHz;
  if (nextRate === previousRate) return;
  if (project.signals.length > 0 && !window.confirm("Change sample rate and preserve each block's physical time and frequency?")) {
    input("sample-rate").value = String(previousRate);
    return;
  }
  const duration = Number(input("duration").value);
  project.sampleRateHz = nextRate;
  project.totalSamples = Math.max(1, Math.round(duration * nextRate));
  for (const block of project.signals) {
    block.startSample = Math.min(project.totalSamples - 1, Math.round(block.startSample / previousRate * nextRate));
    block.sampleCount = Math.max(1, Math.min(project.totalSamples - block.startSample, Math.round(block.sampleCount / previousRate * nextRate)));
    block.fadeSamples = Math.min(Math.floor(block.sampleCount / 2), Math.round(block.fadeSamples / previousRate * nextRate));
  }
  editor.setProject(project, true);
  renderTopFields();
  renderProjectState();
  renderInspector();
  schedulePreview();
}

function changeDuration(seconds: number): void {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    input("duration").value = String(durationSeconds(project));
    setStatus("Invalid duration", "Duration must be greater than zero.", "error");
    return;
  }
  const nextTotal = Math.max(1, Math.round(seconds * project.sampleRateHz));
  if (project.signals.length > 0 && nextTotal !== project.totalSamples && !window.confirm("Change duration and clamp blocks that no longer fit?")) {
    input("duration").value = String(durationSeconds(project));
    return;
  }
  project.totalSamples = nextTotal;
  for (const block of project.signals) {
    block.startSample = Math.min(block.startSample, nextTotal - 1);
    block.sampleCount = Math.max(1, Math.min(block.sampleCount, nextTotal - block.startSample));
    block.fadeSamples = Math.min(block.fadeSamples, Math.floor(block.sampleCount / 2));
  }
  editor.setProject(project, true);
  renderTopFields();
  renderProjectState();
  renderInspector();
  schedulePreview();
}

function bindInspector(): void {
  input("signal-start").addEventListener("input", () => updateSelected((block) => {
    block.startSample = Math.max(0, Math.min(project.totalSamples - block.sampleCount, Math.round(Number(input("signal-start").value) * project.sampleRateHz)));
  }));
  input("signal-duration").addEventListener("input", () => updateSelected((block) => {
    block.sampleCount = Math.max(1, Math.min(project.totalSamples - block.startSample, Math.round(Number(input("signal-duration").value) * project.sampleRateHz)));
  }));
  input("signal-center").addEventListener("input", () => updateSelected((block) => { block.centerFrequencyHz = Number(input("signal-center").value); }));
  input("signal-amplitude").addEventListener("input", () => updateSelected((block) => { block.amplitudeDbfs = Number(input("signal-amplitude").value); }));
  input("signal-phase").addEventListener("input", () => updateSelected((block) => { block.phaseRad = radians(Number(input("signal-phase").value)); }));
  input("signal-fade").addEventListener("input", () => updateSelected((block) => { block.fadeSamples = Math.round(Number(input("signal-fade").value) / 1000 * project.sampleRateHz); }));
  input("signal-fm").addEventListener("input", () => updateSelected((block) => { if (block.kind === "fm") block.modulationFrequencyHz = Number(input("signal-fm").value); }));
  input("signal-deviation").addEventListener("input", () => updateSelected((block) => { if (block.kind === "fm") block.deviationHz = Number(input("signal-deviation").value); }));
  input("signal-mod-phase").addEventListener("input", () => updateSelected((block) => { if (block.kind === "fm") block.modulationPhaseRad = radians(Number(input("signal-mod-phase").value)); }));
}

function setTool(tool: EditorTool): void {
  editor.setTool(tool);
  document.querySelectorAll<HTMLButtonElement>("[data-tool]").forEach((button) => button.classList.toggle("active", button.dataset.tool === tool));
}

async function startExport(): Promise<void> {
  const issues = validateProject(project);
  if (hasErrors(issues) || exporting) return;
  exporting = true;
  previewSession?.cancel();
  downloadButton.disabled = true;
  cancelButton.hidden = false;
  progressWrap.hidden = false;
  progressBar.style.width = "0%";
  progressLabel.textContent = "0%";
  setStatus("Generating recording", "Writing interleaved cf32_le samples…");
  const session = exportRecording(cloneProject(project), {
    onProgress(progress, masterGain): void {
      const percentage = Math.round(progress * 100);
      progressBar.style.width = `${percentage}%`;
      progressLabel.textContent = `${percentage}%`;
      element("status-detail").textContent = `Master gain ${masterGain.toFixed(6)} · ${Math.round(progress * project.totalSamples).toLocaleString()} / ${project.totalSamples.toLocaleString()} samples`;
    },
  });
  exportSession = session;
  try {
    const gain = await session.result;
    setStatus("Export complete", `${project.basename}.sigmf · master gain ${gain.toFixed(6)}`);
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") setStatus("Export cancelled", "No completed archive was written.", "warning");
    else setStatus("Export failed", error instanceof Error ? error.message : String(error), "error");
  } finally {
    exporting = false;
    exportSession = undefined;
    cancelButton.hidden = true;
    progressWrap.hidden = true;
    renderProjectState();
    schedulePreview();
  }
}

document.querySelectorAll<HTMLButtonElement>("[data-tool]").forEach((button) => {
  button.addEventListener("click", () => setTool((button.dataset.tool ?? "select") as EditorTool));
});
element("reset-view").addEventListener("click", () => editor.resetViewport());
element("delete-signal").addEventListener("click", () => editor.deleteSelected());
element("inspector-delete").addEventListener("click", () => editor.deleteSelected());
input("basename").addEventListener("input", () => { project.basename = input("basename").value; renderProjectState(); });
input("sample-rate").addEventListener("change", () => rescaleForSampleRate(Number(input("sample-rate").value)));
input("duration").addEventListener("change", () => changeDuration(Number(input("duration").value)));
input("rf-center").addEventListener("input", () => {
  const value = input("rf-center").value.trim();
  project.rfCenterHz = value === "" ? undefined : Number(value);
  renderProjectState();
  schedulePreview();
});
input("spectral-preview").addEventListener("change", () => schedulePreview());
element("save-project").addEventListener("click", () => downloadText(`${project.basename}.iqgen.json`, serializeProject(project)));
element("load-project").addEventListener("click", () => input("project-file").click());
input("project-file").addEventListener("change", async () => {
  const file = input("project-file").files?.[0];
  if (!file) return;
  try {
    project = parseProject(await file.text());
    editor.setProject(project, true);
    renderTopFields();
    renderProjectState();
    renderInspector();
    schedulePreview();
  } catch (error) {
    setStatus("Project load failed", error instanceof Error ? error.message : String(error), "error");
  } finally {
    input("project-file").value = "";
  }
});
downloadButton.addEventListener("click", () => void startExport());
cancelButton.addEventListener("click", () => exportSession?.cancel());
window.addEventListener("keydown", (event) => {
  if (event.target instanceof HTMLInputElement) return;
  if (event.key.toLowerCase() === "v") setTool("select");
  if (event.key.toLowerCase() === "t") setTool("tone");
  if (event.key.toLowerCase() === "f") setTool("fm");
});

bindInspector();
renderTopFields();
renderProjectState();
renderInspector();
setTool("select");
