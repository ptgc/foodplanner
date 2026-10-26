import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { useI18n, type MessageKey } from "../i18n";

const focusable =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

type Props = {
  title: string;
  body: string;
  confirmLabel: string;
  /** When set, the confirm button stays disabled until the user types exactly this text. */
  requireText?: string;
  requireTextLabel?: string;
  busy: boolean;
  error: MessageKey | null;
  onConfirm: () => void;
  onCancel: () => void;
};

/**
 * Modal confirmation for destructive actions. Accessible: role="dialog" with aria-modal, labelled by its title and
 * described by its body, focus starts on Cancel (or on the text field), Tab stays inside, Escape cancels, and focus
 * returns to the control that opened it. Confirming only calls `onConfirm`; the caller decides when to close.
 */
export function ConfirmDialog({ title, body, confirmLabel, requireText, requireTextLabel, busy, error, onConfirm, onCancel }: Props) {
  const { t } = useI18n();
  const titleId = useId();
  const bodyId = useId();
  const inputId = useId();
  const dialog = useRef<HTMLDivElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const [typed, setTyped] = useState("");
  const matches = requireText === undefined || typed.trim() === requireText;

  const latest = useRef({ busy, onCancel });
  useEffect(() => {
    latest.current = { busy, onCancel };
  });

  useEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    (input.current ?? cancel.current)?.focus();

    // On the document, not on the dialog: focus can briefly be on <body> (a button that just became disabled).
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        if (!latest.current.busy) latest.current.onCancel();
        return;
      }
      if (e.key !== "Tab" || !dialog.current) return;
      const items = [...dialog.current.querySelectorAll<HTMLElement>(focusable)];
      const first = items[0];
      const last = items[items.length - 1];
      if (!first || !last) return;
      const current = document.activeElement;
      if (!dialog.current.contains(current)) {
        e.preventDefault();
        first.focus();
      } else if (e.shiftKey && current === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && current === last) {
        e.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      if (opener?.isConnected) opener.focus();
    };
  }, []);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!busy && matches) onConfirm();
  };

  return (
    <div className="dialog-backdrop">
      <div
        ref={dialog}
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={bodyId}
      >
        <form onSubmit={submit} aria-busy={busy}>
          <h2 id={titleId}>{title}</h2>
          <p id={bodyId}>{body}</p>
          {requireText !== undefined && (
            <div className="field">
              <label htmlFor={inputId}>{requireTextLabel}</label>
              <input
                id={inputId}
                ref={input}
                type="text"
                value={typed}
                autoComplete="off"
                autoCapitalize="off"
                spellCheck={false}
                onChange={(e) => setTyped(e.target.value)}
              />
            </div>
          )}
          {error && (
            <p role="alert" className="form-error">
              {t(error)}
            </p>
          )}
          <div className="dialog-actions">
            <button ref={cancel} type="button" className="button button-outline" disabled={busy} onClick={onCancel}>
              {t("household.dialog.cancel")}
            </button>
            <button type="submit" className="button button-danger" disabled={busy || !matches}>
              {confirmLabel}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
