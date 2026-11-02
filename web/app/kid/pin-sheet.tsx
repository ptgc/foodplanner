import { useCallback, useEffect, useId, useRef, useState } from "react";
import { useI18n, type MessageKey } from "../i18n";
import { useSession } from "../session";
import { BackspaceIcon, CrossIcon, KeyIcon, PadlockIcon } from "./icons";
import { unlockWithPin } from "./kid-api";
import { clock, PIN_LENGTH } from "./pin";
import { useModal } from "./use-modal";

/** The lockout the server applies (Auth:PinLockoutMinutes = 2) when it does not say how long. */
const defaultLockSeconds = 120;
/** Wrong tries before the lock (Auth:PinMaxFailedAttempts); the pips only draw this many. */
const maxAttempts = 5;

const digitKeys = ["1", "2", "3", "4", "5", "6", "7", "8", "9"] as const;

type State = "idle" | "checking" | "wrong" | "locked" | "error";

/**
 * Back to an adult (T-091): a 4-digit keypad with big keys. Feedback is visual first: the dots shake and turn red on a wrong
 * PIN, the tries left show as pips, a lock shows a padlock and a countdown. The same facts are in a visually hidden live
 * region for screen readers. A correct PIN makes the server drop kid mode; the session is then read again.
 */
export function PinSheet({ onClose }: { onClose: () => void }) {
  const { t } = useI18n();
  const { reloadSession, signOut } = useSession();
  const sheet = useRef<HTMLDivElement>(null);
  const headingId = useId();
  const [digits, setDigits] = useState("");
  const [state, setState] = useState<State>("idle");
  const [attempts, setAttempts] = useState<number | null>(null);
  const [lockedUntil, setLockedUntil] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [errorKey, setErrorKey] = useState<MessageKey | null>(null);
  const [shakes, setShakes] = useState(0);
  const busy = useRef(false);
  // The digits as typed so far, readable at once (state lags a render behind a fast tap).
  const typed = useRef("");
  const setTyped = (value: string) => {
    typed.current = value;
    setDigits(value);
  };

  useModal(sheet, onClose);

  // Countdown while locked; when it reaches zero the keypad opens again.
  useEffect(() => {
    if (state !== "locked" || lockedUntil === null) return;
    const timer = setInterval(() => {
      const current = Date.now();
      setNow(current);
      if (current >= lockedUntil) {
        setState("idle");
        setLockedUntil(null);
      }
    }, 1000);
    return () => clearInterval(timer);
  }, [state, lockedUntil]);

  const submit = useCallback(
    async (pin: string) => {
      if (busy.current) return;
      busy.current = true;
      setState("checking");
      const result = await unlockWithPin(pin);
      if (result.kind === "ok") {
        const read = await reloadSession();
        busy.current = false;
        if (!read) {
          setTyped("");
          setErrorKey("problem.unreachable");
          setState("error");
        }
        return; // success: the kid shell is replaced by the adult app
      }
      busy.current = false;
      setTyped("");
      if (result.kind === "wrong") {
        setAttempts(result.attemptsRemaining);
        setShakes((n) => n + 1);
        setState("wrong");
      } else if (result.kind === "locked") {
        const seconds = result.seconds ?? defaultLockSeconds;
        setNow(Date.now());
        setLockedUntil(Date.now() + seconds * 1000);
        setState("locked");
      } else {
        setErrorKey(result.key);
        setState("error");
      }
    },
    [reloadSession],
  );

  // The fourth digit sends the PIN; there is no OK key to find.
  const press = useCallback(
    (digit: string) => {
      if (state === "checking" || state === "locked" || typed.current.length >= PIN_LENGTH) return;
      setState((s) => (s === "wrong" || s === "error" ? "idle" : s));
      const next = typed.current + digit;
      typed.current = next;
      setDigits(next);
      if (next.length === PIN_LENGTH) void submit(next);
    },
    [state, submit],
  );

  const erase = useCallback(() => {
    if (state === "checking" || state === "locked") return;
    const next = typed.current.slice(0, -1);
    typed.current = next;
    setDigits(next);
  }, [state]);

  // A physical keyboard works too (desktop, tablets with a keyboard).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      if (/^[0-9]$/.test(e.key)) press(e.key);
      else if (e.key === "Backspace") erase();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [press, erase]);

  const remainingSeconds = lockedUntil === null ? 0 : Math.max(0, Math.ceil((lockedUntil - now) / 1000));
  const locked = state === "locked";
  const announce =
    state === "wrong"
      ? attempts !== null
        ? `${t("kid.pin.wrong")} ${t("kid.pin.triesLeft", { count: attempts })}`
        : t("kid.pin.wrong")
      : locked
        ? t("kid.pin.locked", { time: clock(remainingSeconds) })
        : state === "error" && errorKey
          ? t(errorKey)
          : "";

  return (
    <div className="kid-backdrop">
      <div ref={sheet} className="kid-sheet kid-pin" role="dialog" aria-modal="true" aria-labelledby={headingId} tabIndex={-1} data-state={state}>
        <h2 id={headingId} className="visually-hidden">
          {t("kid.pin.title")}
        </h2>
        <button type="button" className="kid-close" aria-label={t("kid.close")} onClick={onClose}>
          <CrossIcon />
        </button>

        <div className="kid-pin-icon" aria-hidden="true">
          {locked ? <PadlockIcon /> : <KeyIcon />}
        </div>

        {locked ? (
          <p className="kid-pin-timer" data-testid="pin-locked-timer" aria-hidden="true">
            {clock(remainingSeconds)}
          </p>
        ) : (
          <div
            key={shakes}
            className={state === "wrong" ? "kid-pin-dots kid-shake" : "kid-pin-dots"}
            role="img"
            aria-label={t("kid.pin.entered", { count: digits.length, total: PIN_LENGTH })}
            data-testid="pin-dots"
          >
            {Array.from({ length: PIN_LENGTH }, (_, i) => (
              <span key={i} className={i < digits.length ? "kid-pin-dot kid-pin-dot-on" : "kid-pin-dot"} data-wrong={state === "wrong"} />
            ))}
          </div>
        )}

        {state === "wrong" && attempts !== null && (
          <div className="kid-pin-pips" aria-hidden="true" data-testid="pin-pips" data-remaining={attempts}>
            {Array.from({ length: maxAttempts }, (_, i) => (
              <span key={i} className={i < attempts ? "kid-pip kid-pip-on" : "kid-pip"} />
            ))}
          </div>
        )}
        {state === "error" && errorKey && (
          <p className="kid-pin-error" role="alert">
            {t(errorKey)}
          </p>
        )}

        <p role="status" className="visually-hidden" data-testid="pin-status">
          {announce}
        </p>

        <div className="kid-keypad" role="group" aria-label={t("kid.pin.keypad")}>
          {digitKeys.map((digit) => (
            <button key={digit} type="button" className="kid-key" disabled={locked} onClick={() => press(digit)}>
              {digit}
            </button>
          ))}
          <span aria-hidden="true" />
          <button type="button" className="kid-key" disabled={locked} onClick={() => press("0")}>
            0
          </button>
          <button type="button" className="kid-key kid-key-icon" disabled={locked} aria-label={t("kid.pin.erase")} onClick={erase}>
            <BackspaceIcon />
          </button>
        </div>

        {locked && (
          <button type="button" className="button button-outline kid-pin-password" onClick={() => void signOut()}>
            {t("kid.pin.passwordSignIn")}
          </button>
        )}
      </div>
    </div>
  );
}
