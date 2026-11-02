/** PIN rules the server enforces (T-026): exactly 4 digits, not all equal, not a straight run. Checked here only to save a round trip. */
export const PIN_LENGTH = 4;

export function isPinWeak(pin: string): boolean {
  if (pin.length !== PIN_LENGTH || !/^\d+$/.test(pin)) return false;
  const digits = [...pin].map(Number);
  if (digits.every((d) => d === digits[0])) return true;
  const steps = digits.slice(1).map((d, i) => d - digits[i]!);
  return steps.every((s) => s === 1) || steps.every((s) => s === -1);
}

export type PinProblem = "format" | "weak";

/** Null when the PIN is acceptable. */
export function pinProblem(pin: string): PinProblem | null {
  if (!/^\d{4}$/.test(pin)) return "format";
  return isPinWeak(pin) ? "weak" : null;
}

/** Seconds from a `Retry-After` header (delta seconds); null if absent or not a number. */
export function retryAfterSeconds(value: string | null): number | null {
  if (value === null) return null;
  const seconds = Number(value.trim());
  return Number.isFinite(seconds) && seconds >= 0 ? Math.ceil(seconds) : null;
}

/** "1:05" for 65 seconds. */
export function clock(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

/** The server's extension member `attemptsRemaining` (not part of the generated ProblemDetails type). */
export function attemptsRemainingOf(body: unknown): number | null {
  if (typeof body !== "object" || body === null) return null;
  const value = (body as Record<string, unknown>).attemptsRemaining;
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}
