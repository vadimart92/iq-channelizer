import { describe, expect, it } from "vitest";
import { History } from "../src/app/history";

describe("history transactions", () => {
  it("coalesces many edits into one undo step", () => {
    const history = new History<number>((left, right) => left === right);
    history.begin(1);
    history.begin(2);
    history.begin(3);
    history.commit(4);
    expect(history.undo(4)).toBe(1);
    expect(history.canUndo).toBe(false);
    expect(history.redo(1)).toBe(4);
  });

  it("does not retain unchanged transactions", () => {
    const history = new History<string>((left, right) => left === right);
    history.begin("same");
    history.commit("same");
    expect(history.canUndo).toBe(false);
  });

  it("undoes an in-progress input transaction", () => {
    const history = new History<number>((left, right) => left === right);
    history.begin(10);
    expect(history.undo(11)).toBe(10);
    expect(history.redo(10)).toBe(11);
  });
});
