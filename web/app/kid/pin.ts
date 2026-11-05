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

/**
 * The tries left, from the `attemptsRemaining` member of a `403 pin-incorrect` problem (typed in the generated client
 * as PinIncorrectProblem). Takes any error body of the operation and narrows it, no cast; null if the server did not say.
 */
export function attemptsRemainingOf(error: object | undefined): number | null {
  if (!error || !("attemptsRemaining" in error)) return null;
  const value = error.attemptsRemaining;
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}
