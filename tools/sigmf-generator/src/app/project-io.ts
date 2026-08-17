import {
  PROJECT_SCHEMA_VERSION,
  type SignalProject,
} from "../model/project";
import { hasErrors, validateProject } from "./validation";

export function serializeProject(project: SignalProject): string {
  return `${JSON.stringify(project, null, 2)}\n`;
}

export function parseProject(text: string): SignalProject {
  const value: unknown = JSON.parse(text);
  if (typeof value !== "object" || value === null) {
    throw new Error("Project must be a JSON object.");
  }
  const candidate = value as Partial<SignalProject>;
  if (candidate.schemaVersion !== PROJECT_SCHEMA_VERSION) {
    throw new Error(`Unsupported project schema version: ${String(candidate.schemaVersion)}.`);
  }
  if (!Array.isArray(candidate.signals)) {
    throw new Error("Project signals must be an array.");
  }
  const project = candidate as SignalProject;
  const issues = validateProject(project);
  if (hasErrors(issues)) {
    throw new Error(issues.filter((issue) => issue.severity === "error").map((issue) => issue.message).join(" "));
  }
  return structuredClone(project);
}

export function downloadText(filename: string, text: string): void {
  const url = URL.createObjectURL(new Blob([text], { type: "application/json" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
