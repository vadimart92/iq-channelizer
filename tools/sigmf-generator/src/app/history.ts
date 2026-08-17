export class History<T> {
  readonly #undo: T[] = [];
  readonly #redo: T[] = [];
  readonly #equals: (left: T, right: T) => boolean;
  readonly #limit: number;
  #pending: T | undefined;

  constructor(equals: (left: T, right: T) => boolean, limit = 100) {
    this.#equals = equals;
    this.#limit = limit;
  }

  get canUndo(): boolean {
    return this.#pending !== undefined || this.#undo.length > 0;
  }

  get canRedo(): boolean {
    return this.#redo.length > 0;
  }

  begin(snapshot: T): void {
    this.#pending ??= snapshot;
  }

  commit(current: T): void {
    if (this.#pending === undefined) return;
    const before = this.#pending;
    this.#pending = undefined;
    if (this.#equals(before, current)) return;
    this.#undo.push(before);
    if (this.#undo.length > this.#limit) this.#undo.shift();
    this.#redo.length = 0;
  }

  cancel(): void {
    this.#pending = undefined;
  }

  undo(current: T): T | undefined {
    if (this.#pending !== undefined) {
      const before = this.#pending;
      this.#pending = undefined;
      if (!this.#equals(before, current)) {
        this.#redo.push(current);
        return before;
      }
    }
    const previous = this.#undo.pop();
    if (previous === undefined) return undefined;
    this.#redo.push(current);
    return previous;
  }

  redo(current: T): T | undefined {
    this.#pending = undefined;
    const next = this.#redo.pop();
    if (next === undefined) return undefined;
    this.#undo.push(current);
    return next;
  }

  clear(): void {
    this.#undo.length = 0;
    this.#redo.length = 0;
    this.#pending = undefined;
  }
}
