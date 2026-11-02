import { useId, useState, type FormEvent } from "react";
import { useI18n, type MessageKey } from "../i18n";
import { setPin } from "./kid-api";
import { pinProblem, PIN_LENGTH } from "./pin";

/**
 * Set or change the signed-in adult's own PIN (T-026: one PIN per account, 4 digits, obvious ones refused). Changing an
 * existing PIN needs the current one; the API says so with a 400 on a PIN-less request, and the field is then required.
 */
export function PinSetup({ onDone }: { onDone: () => void }) {
  const { t } = useI18n();
  const newId = useId();
  const currentId = useId();
  const [pin, setNewPin] = useState("");
  const [current, setCurrent] = useState("");
  const [needsCurrent, setNeedsCurrent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const digitsOnly = (value: string) => value.replace(/\D/g, "").slice(0, PIN_LENGTH);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const problem = pinProblem(pin);
    if (problem) {
      setError(problem === "weak" ? "kid.pinSetup.weak" : "kid.pinSetup.format");
      return;
    }
    if (needsCurrent && !/^\d{4}$/.test(current)) {
      setError("kid.pinSetup.currentFormat");
      return;
    }
    setBusy(true);
    setError(null);
    const result = await setPin(pin, current === "" ? undefined : current);
    setBusy(false);
    switch (result.kind) {
      case "ok":
        setNewPin("");
        setCurrent("");
        onDone();
        return;
      case "needs-current":
        setNeedsCurrent(true);
        setError("kid.pinSetup.needsCurrent");
        return;
      case "wrong-current":
        setNeedsCurrent(true);
        setError("kid.pinSetup.wrongCurrent");
        return;
      case "locked":
        setError("kid.pinSetup.locked");
        return;
      case "invalid":
        setError("kid.pinSetup.weak");
        return;
      case "failed":
        setError(result.key);
    }
  };

  return (
    <form className="kid-pin-setup" onSubmit={(e) => void submit(e)} aria-busy={busy} data-testid="pin-setup">
      <div className="field">
        <label htmlFor={newId}>{t("kid.pinSetup.new")}</label>
        <input
          id={newId}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={PIN_LENGTH}
          value={pin}
          onChange={(e) => setNewPin(digitsOnly(e.target.value))}
          aria-describedby={`${newId}-hint`}
        />
        <p id={`${newId}-hint`} className="kid-hint">
          {t("kid.pinSetup.hint")}
        </p>
      </div>
      <div className="field">
        <label htmlFor={currentId}>{needsCurrent ? t("kid.pinSetup.current") : t("kid.pinSetup.currentOptional")}</label>
        <input
          id={currentId}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={PIN_LENGTH}
          value={current}
          required={needsCurrent}
          onChange={(e) => setCurrent(digitsOnly(e.target.value))}
        />
      </div>
      {error && (
        <p role="alert" className="form-error" data-testid="pin-setup-error">
          {t(error)}
        </p>
      )}
      <button type="submit" className="button" disabled={busy}>
        {t("kid.pinSetup.save")}
      </button>
    </form>
  );
}
