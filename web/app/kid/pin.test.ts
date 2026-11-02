import { describe, expect, it } from "vitest";
import { isKidFriendly, kidFriendlyFirst } from "./kid-recipes";
import { attemptsRemainingOf, clock, isPinWeak, pinProblem, retryAfterSeconds } from "./pin";

describe("PIN rules", () => {
  it("accepts four digits that are not obvious", () => {
    expect(pinProblem("4827")).toBeNull();
    expect(pinProblem("1357")).toBeNull();
  });

  it("refuses anything but exactly four digits", () => {
    for (const bad of ["", "123", "48271", "48a7", "4 27", "٤٨٢٧"]) expect(pinProblem(bad)).toBe("format");
  });

  it("refuses repeated digits and straight runs, like the server", () => {
    for (const weak of ["0000", "7777", "1234", "2345", "9876", "3210"]) {
      expect(isPinWeak(weak)).toBe(true);
      expect(pinProblem(weak)).toBe("weak");
    }
    expect(isPinWeak("1235")).toBe(false);
  });
});

describe("lockout helpers", () => {
  it("reads Retry-After seconds, rounding up, and nothing else", () => {
    expect(retryAfterSeconds("120")).toBe(120);
    expect(retryAfterSeconds(" 14.2 ")).toBe(15);
    expect(retryAfterSeconds(null)).toBeNull();
    expect(retryAfterSeconds("Wed, 21 Oct 2026 07:28:00 GMT")).toBeNull();
    expect(retryAfterSeconds("-3")).toBeNull();
  });

  it("writes a countdown as minutes and seconds", () => {
    expect(clock(120)).toBe("2:00");
    expect(clock(65)).toBe("1:05");
    expect(clock(9)).toBe("0:09");
    expect(clock(-4)).toBe("0:00");
  });

  it("reads attemptsRemaining from the problem body when it is a number", () => {
    expect(attemptsRemainingOf({ type: "x", attemptsRemaining: 3 })).toBe(3);
    expect(attemptsRemainingOf({ attemptsRemaining: "3" })).toBeNull();
    expect(attemptsRemainingOf(undefined)).toBeNull();
  });
});

describe("kid feed order (Q-020)", () => {
  const tag = (name: string, isGlobal = true) => ({ id: name, name, kind: "Tag" as const, isGlobal });
  const r = (id: string, ...tags: ReturnType<typeof tag>[]) => ({ id, tags });

  it("recognises the global Barnevennlig tag in both catalog languages, not a household tag of that name", () => {
    expect(isKidFriendly(r("a", tag("Barnevennlig")))).toBe(true);
    expect(isKidFriendly(r("a", tag("Kid friendly")))).toBe(true);
    expect(isKidFriendly(r("a", tag("Barnevennlig", false)))).toBe(false);
    expect(isKidFriendly(r("a", tag("Rask")))).toBe(false);
  });

  it("puts Barnevennlig dishes first and keeps the server's order inside each group", () => {
    const items = [r("1"), r("2", tag("Barnevennlig")), r("3"), r("4", tag("Kid friendly"))];
    expect(kidFriendlyFirst(items).map((x) => x.id)).toEqual(["2", "4", "1", "3"]);
  });
});
