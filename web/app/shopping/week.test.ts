import { describe, expect, it } from "vitest";
import { addWeeks, isMonday, isoWeek, mondayOf } from "./week";

describe("week helpers", () => {
  it("finds the Monday of a date", () => {
    expect(mondayOf(new Date(2026, 9, 24))).toBe("2026-10-19"); // Saturday
    expect(mondayOf(new Date(2026, 9, 19))).toBe("2026-10-19");
    expect(mondayOf(new Date(2026, 9, 25))).toBe("2026-10-19"); // Sunday
  });

  it("validates Mondays", () => {
    expect(isMonday("2026-10-19")).toBe(true);
    expect(isMonday("2026-10-20")).toBe(false);
    expect(isMonday("2026-02-30")).toBe(false);
    expect(isMonday(null)).toBe(false);
  });

  it("adds weeks across year ends", () => {
    expect(addWeeks("2026-12-28", 1)).toBe("2027-01-04");
    expect(addWeeks("2026-10-19", -1)).toBe("2026-10-12");
  });

  it("computes ISO week numbers", () => {
    expect(isoWeek("2026-10-19")).toEqual({ week: 43, year: 2026 });
    expect(isoWeek("2026-12-28")).toEqual({ week: 53, year: 2026 });
    expect(isoWeek("2025-12-29")).toEqual({ week: 1, year: 2026 });
  });
});
