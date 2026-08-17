import "./styles.css";

import { exportRecording, exportWave, type ExportSession } from "./app/exporter";
import { History } from "./app/history";
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
  type SignalProject,
} from "./model/project";
import { validateWavProject } from "./wav/writer";

type ExportFormat = "sigmf" | "wav";
type SampleRateUnit = 1 | 1_000 | 1_000_000;

interface EditorSnapshot {
  project: SignalProject;
  selectedIds: string[];
}

function element<T extends HTMLElement>(id: string): T {
  const value = document.getElementById(id);
  if (!value) throw new Error(`Missing element #${id}.`);
  return value as T;
}

function input(id: string): HTMLInputElement {
  return element<HTMLInputElement>(id);
}

function select(id: string): HTMLSelectElement {
  return element<HTMLSelectElement>(id);
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

function preferredSampleRateUnit(sampleRateHz: number): SampleRateUnit {
  if (sampleRateHz >= 1_000_000) return 1_000_000;
  if (sampleRateHz >= 1_000) return 1_000;
  return 1;
}

let project = createDefaultProject();
let sampleRateUnit: SampleRateUnit = 1_000_000;
let exportSession: ExportSession | undefined;
let previewSession: PreviewSession | undefined;
let previewTimer: number | undefined;
let exporting = false;

const history = new History<EditorSnapshot>(
  (left, right) => JSON.stringify(left) === JSON.stringify(right),
);
const canvas = element<HTMLCanvasElement>("signal-canvas");
const signalForm = element<HTMLFormElement>("signal-form");
const emptyInspector = element<HTMLDivElement>("empty-inspector");
const fmFields = element<HTMLDivElement>("fm-fields");
const fmRadioFields = element<HTMLDivElement>("fm-radio-fields");
const sigMfButton = element<HTMLButtonElement>("download");
const wavButton = element<HTMLButtonElement>("download-wav");
const cancelButton = element<HTMLButtonElement>("cancel-export");
const progressWrap = element<HTMLDivElement>("progress-wrap");
const progressBar = element<HTMLSpanElement>("progress-bar");
const progressLabel = element<HTMLSpanElement>("progress-label");
const contextMenu = element<HTMLDivElement>("canvas-context-menu");

const editor = new CanvasEditor(canvas, project, {
  onProjectChanged(): void {
    renderProjectState();
    renderInspector();
    schedulePreview();
  },
  onSelectionChanged(): void {
    renderInspector();
    renderHistoryButtons();
  },
  onEditStarted(): void {
    beginHistoryTransaction();
  },
  onEditCommitted(changed): void {
    if (changed) commitHistoryTransaction();
    else cancelHistoryTransaction();
  },
  onContextMenu(x, y): void {
    showContextMenu(x, y);
  },
  onViewportChanged(): void {
    schedulePreview();
  },
});

function currentSnapshot(): EditorSnapshot {
  return { project: cloneProject(project), selectedIds: [...editor.selectedIds] };
}

function beginHistoryTransaction(): void {
  history.begin(currentSnapshot());
  renderHistoryButtons();
}

function commitHistoryTransaction(): void {
  history.commit(currentSnapshot());
  renderHistoryButtons();
}

function cancelHistoryTransaction(): void {
  history.cancel();
  renderHistoryButtons();
}

function restoreSnapshot(snapshot: EditorSnapshot): void {
  project = cloneProject(snapshot.project);
  editor.setProject(project);
  editor.selectMany(snapshot.selectedIds.filter((id) => project.signals.some((signal) => signal.id === id)));
  renderTopFields();
  renderProjectState();
  renderInspector();
  schedulePreview();
}

function undo(): void {
  const snapshot = history.undo(currentSnapshot());
  if (snapshot) restoreSnapshot(snapshot);
  renderHistoryButtons();
}

function redo(): void {
  const snapshot = history.redo(currentSnapshot());
  if (snapshot) restoreSnapshot(snapshot);
  renderHistoryButtons();
}

function renderHistoryButtons(): void {
  element<HTMLButtonElement>("undo").disabled = !history.canUndo;
  element<HTMLButtonElement>("redo").disabled = !history.canRedo;
}

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

function renderProjectState(preserveStatus = false): void {
  element("sample-summary").textContent = `${project.totalSamples.toLocaleString()} samples`;
  element("size-summary").textContent = formatBytes(totalDataBytes(project));
  const issues = validateProject(project);
  const invalid = hasErrors(issues);
  const wavErrors = validateWavProject(project);
  sigMfButton.disabled = exporting || invalid;
  wavButton.disabled = exporting || invalid || wavErrors.length > 0;
  wavButton.title = wavErrors.join(" ");
  renderHistoryButtons();
  if (exporting || preserveStatus) return;
  const errors = issues.filter((issue) => issue.severity === "error");
  const warnings = issues.filter((issue) => issue.severity === "warning");
  if (errors.length > 0) setStatus(`${errors.length} validation error${errors.length === 1 ? "" : "s"}`, issueSummary(errors), "error");
  else if (warnings.length > 0) setStatus(`${warnings.length} warning${warnings.length === 1 ? "" : "s"}`, issueSummary(warnings), "warning");
  else if (project.signals.length === 0) setStatus("Ready", "Empty canvas will export zero IQ.");
  else setStatus("Ready to export", `${project.signals.length} signal block${project.signals.length === 1 ? "" : "s"} · SigMF cf32_le or stereo float32 WAV`);
  editor.invalidate();
}

function renderSampleRateField(): void {
  select("sample-rate-unit").value = String(sampleRateUnit);
  input("sample-rate").value = String(project.sampleRateHz / sampleRateUnit);
  input("sample-rate").step = sampleRateUnit === 1_000_000 ? "0.001" : "1";
}

function renderTopFields(chooseUnit = false): void {
  if (chooseUnit) sampleRateUnit = preferredSampleRateUnit(project.sampleRateHz);
  input("basename").value = project.basename;
  renderSampleRateField();
  input("duration").value = String(durationSeconds(project));
  input("rf-center").value = project.rfCenterHz === undefined ? "" : String(project.rfCenterHz);
}

function renderInspector(): void {
  const block = selectedBlock();
  const selectedCount = editor.selectedIds.length;
  signalForm.hidden = !block;
  emptyInspector.hidden = Boolean(block);
  if (selectedCount > 1) {
    element("selection-kind").textContent = `${selectedCount} SIGNALS`;
    element("empty-inspector-text").textContent = "Drag any selected block to move the group, or right-click to delete the selection.";
  } else if (!block) {
    element("selection-kind").textContent = "No selection";
    element("empty-inspector-text").textContent = "Drag a selection box, or draw a Tone or FM Radio block and select it to edit exact values.";
  } else {
    element("selection-kind").textContent = `${block.kind.toUpperCase()} · ${block.id.slice(0, 8)}`;
  }
  if (!block) return;
  input("signal-start").value = String(block.startSample / project.sampleRateHz);
  input("signal-duration").value = String(block.sampleCount / project.sampleRateHz);
  input("signal-center").value = String(block.centerFrequencyHz);
  input("signal-amplitude").value = String(block.amplitudeDbfs);
  input("signal-phase").value = String(degrees(block.phaseRad));
  input("signal-fade").value = String(block.fadeSamples / project.sampleRateHz * 1000);
  fmFields.hidden = block.kind !== "fm";
  fmRadioFields.hidden = block.kind !== "fm-radio";
  if (block.kind === "fm") {
    input("signal-fm").value = String(block.modulationFrequencyHz);
    input("signal-deviation").value = String(block.deviationHz);
    input("signal-mod-phase").value = String(degrees(block.modulationPhaseRad));
    const [low, high] = frequencyBounds(block);
    element("signal-bandwidth").textContent = `Carson bandwidth: ${occupiedBandwidthHz(block).toLocaleString()} Hz (${low.toLocaleString()} … ${high.toLocaleString()} Hz)`;
  }
  if (block.kind === "fm-radio") {
    input("signal-audio-bandwidth").value = String(block.audioBandwidthHz);
    input("signal-radio-deviation").value = String(block.deviationHz);
    input("signal-radio-seed").value = String(block.seed);
    const [low, high] = frequencyBounds(block);
    element("signal-radio-bandwidth").textContent = `Carson bandwidth: ${occupiedBandwidthHz(block).toLocaleString()} Hz (${low.toLocaleString()} … ${high.toLocaleString()} Hz)`;
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
    const session = requestSpectralPreview(cloneProject(project), Number(select("preview-fft-size").value), {
      sampleStart: editor.viewport.sampleStart,
      sampleEnd: editor.viewport.sampleEnd,
      frequencyLowHz: editor.viewport.frequencyLowHz,
      frequencyHighHz: editor.viewport.frequencyHighHz,
    });
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
    renderSampleRateField();
    setStatus("Invalid sample rate", "Enter a finite value greater than zero and no more than 1e12 Hz.", "error");
    return;
  }
  const previousRate = project.sampleRateHz;
  if (nextRate === previousRate) return;
  if (project.signals.length > 0 && !window.confirm("Change sample rate and preserve each block's physical time and frequency?")) {
    renderSampleRateField();
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
  input("signal-audio-bandwidth").addEventListener("input", () => updateSelected((block) => { if (block.kind === "fm-radio") block.audioBandwidthHz = Number(input("signal-audio-bandwidth").value); }));
  input("signal-radio-deviation").addEventListener("input", () => updateSelected((block) => { if (block.kind === "fm-radio") block.deviationHz = Number(input("signal-radio-deviation").value); }));
  input("signal-radio-seed").addEventListener("input", () => updateSelected((block) => { if (block.kind === "fm-radio") block.seed = Math.round(Number(input("signal-radio-seed").value)); }));
}

function setTool(tool: EditorTool): void {
  editor.setTool(tool);
  document.querySelectorAll<HTMLButtonElement>("[data-tool]").forEach((button) => button.classList.toggle("active", button.dataset.tool === tool));
}

function showContextMenu(x: number, y: number): void {
  if (editor.selectedIds.length === 0) {
    contextMenu.hidden = true;
    return;
  }
  element("context-delete-label").textContent = `Delete ${editor.selectedIds.length === 1 ? "signal" : `${editor.selectedIds.length} signals`}`;
  contextMenu.hidden = false;
  const width = 220;
  const height = 48;
  contextMenu.style.left = `${Math.min(x, window.innerWidth - width - 8)}px`;
  contextMenu.style.top = `${Math.min(y, window.innerHeight - height - 8)}px`;
}

async function startExport(format: ExportFormat): Promise<void> {
  const issues = validateProject(project);
  const wavErrors = format === "wav" ? validateWavProject(project) : [];
  if (hasErrors(issues) || wavErrors.length > 0 || exporting) {
    if (wavErrors.length > 0) setStatus("WAV export unavailable", wavErrors.join(" "), "error");
    return;
  }
  exporting = true;
  previewSession?.cancel();
  sigMfButton.disabled = true;
  wavButton.disabled = true;
  cancelButton.hidden = false;
  progressWrap.hidden = false;
  progressBar.style.width = "0%";
  progressLabel.textContent = "0%";
  const extension = format === "wav" ? "wav" : "sigmf";
  setStatus(`Generating ${extension.toUpperCase()}`, format === "wav" ? "Writing stereo float32 I/Q samples…" : "Writing interleaved cf32_le samples…");
  const callbacks = {
    onProgress(progress: number, masterGain: number): void {
      const percentage = Math.round(progress * 100);
      progressBar.style.width = `${percentage}%`;
      progressLabel.textContent = `${percentage}%`;
      element("status-detail").textContent = `Master gain ${masterGain.toFixed(6)} · ${Math.round(progress * project.totalSamples).toLocaleString()} / ${project.totalSamples.toLocaleString()} samples`;
    },
  };
  const session = format === "wav"
    ? exportWave(cloneProject(project), callbacks)
    : exportRecording(cloneProject(project), callbacks);
  exportSession = session;
  try {
    const gain = await session.result;
    setStatus("Export complete", `${project.basename}.${extension} · master gain ${gain.toFixed(6)}`);
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") setStatus("Export cancelled", "No completed file was written.", "warning");
    else setStatus("Export failed", error instanceof Error ? error.message : String(error), "error");
  } finally {
    exporting = false;
    exportSession = undefined;
    cancelButton.hidden = true;
    progressWrap.hidden = true;
    renderProjectState(true);
    schedulePreview();
  }
}

function bindHistoryInputs(): void {
  const selectors = [
    "#basename", "#sample-rate", "#duration", "#rf-center",
    "#signal-form input",
  ].join(",");
  document.querySelectorAll<HTMLInputElement>(selectors).forEach((control) => {
    control.addEventListener("focus", () => beginHistoryTransaction());
    control.addEventListener("blur", () => commitHistoryTransaction());
  });
}

document.querySelectorAll<HTMLButtonElement>("[data-tool]").forEach((button) => {
  button.addEventListener("click", () => setTool((button.dataset.tool ?? "select") as EditorTool));
});
element("undo").addEventListener("click", () => undo());
element("redo").addEventListener("click", () => redo());
element("reset-view").addEventListener("click", () => editor.resetViewport());
element("delete-signal").addEventListener("click", () => editor.deleteSelected());
element("inspector-delete").addEventListener("click", () => editor.deleteSelected());
element("context-delete").addEventListener("click", () => { contextMenu.hidden = true; editor.deleteSelected(); });
input("basename").addEventListener("input", () => { project.basename = input("basename").value; renderProjectState(); });
input("sample-rate").addEventListener("change", () => rescaleForSampleRate(Number(input("sample-rate").value) * sampleRateUnit));
select("sample-rate-unit").addEventListener("change", () => {
  sampleRateUnit = Number(select("sample-rate-unit").value) as SampleRateUnit;
  renderSampleRateField();
});
input("duration").addEventListener("change", () => changeDuration(Number(input("duration").value)));
input("rf-center").addEventListener("input", () => {
  const value = input("rf-center").value.trim();
  project.rfCenterHz = value === "" ? undefined : Number(value);
  renderProjectState();
  schedulePreview();
});
input("spectral-preview").addEventListener("change", () => schedulePreview());
select("preview-fft-size").addEventListener("change", () => schedulePreview());
element("save-project").addEventListener("click", () => downloadText(`${project.basename}.iqgen.json`, serializeProject(project)));
element("load-project").addEventListener("click", () => input("project-file").click());
input("project-file").addEventListener("change", async () => {
  const file = input("project-file").files?.[0];
  if (!file) return;
  beginHistoryTransaction();
  try {
    project = parseProject(await file.text());
    sampleRateUnit = preferredSampleRateUnit(project.sampleRateHz);
    editor.setProject(project, true);
    editor.select(undefined);
    renderTopFields();
    renderProjectState();
    renderInspector();
    schedulePreview();
    commitHistoryTransaction();
  } catch (error) {
    cancelHistoryTransaction();
    setStatus("Project load failed", error instanceof Error ? error.message : String(error), "error");
  } finally {
    input("project-file").value = "";
  }
});
sigMfButton.addEventListener("click", () => void startExport("sigmf"));
wavButton.addEventListener("click", () => void startExport("wav"));
cancelButton.addEventListener("click", () => exportSession?.cancel());
document.addEventListener("pointerdown", (event) => {
  if (!contextMenu.contains(event.target as Node)) contextMenu.hidden = true;
});
window.addEventListener("keydown", (event) => {
  const modifier = event.ctrlKey || event.metaKey;
  if (modifier && event.key.toLowerCase() === "z") {
    event.preventDefault();
    if (event.shiftKey) redo();
    else undo();
    return;
  }
  if (modifier && event.key.toLowerCase() === "y") {
    event.preventDefault();
    redo();
    return;
  }
  if (event.target instanceof HTMLInputElement || event.target instanceof HTMLSelectElement || event.target instanceof HTMLTextAreaElement) return;
  if (event.key.toLowerCase() === "v") setTool("select");
  if (event.key.toLowerCase() === "t") setTool("tone");
  if (event.key.toLowerCase() === "f") setTool("fm-radio");
});

bindInspector();
bindHistoryInputs();
renderTopFields();
renderProjectState();
renderInspector();
renderHistoryButtons();
setTool("select");
