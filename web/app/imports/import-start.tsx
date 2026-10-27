import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { Link } from "react-router";
import { useI18n } from "../i18n";
import { createImport, importFailureText, isNoRecipeFound, listImports, type ImportDraft, type ImportFailure, type ImportSummary } from "./import-api";

export const maxTextLength = 100_000;

type Tab = "url" | "text";

/** Step 1: paste a web address or the recipe text, get a draft. */
export function ImportStart({ householdId, onCreated }: { householdId: string; onCreated: (draft: ImportDraft) => void }) {
  const { t } = useI18n();
  const [tab, setTab] = useState<Tab>("url");
  const [url, setUrl] = useState("");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);
  const [failure, setFailure] = useState<ImportFailure | null>(null);
  const urlRef = useRef<HTMLInputElement>(null);
  const textRef = useRef<HTMLTextAreaElement>(null);
  const tabRefs = useRef<Record<Tab, HTMLButtonElement | null>>({ url: null, text: null });
  const base = useId();
  const fieldError = `${base}-error`;

  const choose = (next: Tab, focusTab = false) => {
    setTab(next);
    setLocalError(null);
    setFailure(null);
    if (focusTab) tabRefs.current[next]?.focus();
  };

  const onTabKey = (event: KeyboardEvent) => {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight" || event.key === "Home" || event.key === "End") {
      event.preventDefault();
      choose(tab === "url" ? "text" : "url", true);
    }
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setFailure(null);
    const value = tab === "url" ? url.trim() : text.trim();
    if (value === "") {
      setLocalError(t(tab === "url" ? "import.error.urlRequired" : "import.error.textRequired"));
      (tab === "url" ? urlRef : textRef).current?.focus();
      return;
    }
    if (tab === "text" && value.length > maxTextLength) {
      setLocalError(t("import.error.textTooLong", { max: maxTextLength }));
      textRef.current?.focus();
      return;
    }
    setLocalError(null);
    setBusy(true);
    const result = await createImport(householdId, tab === "url" ? { url: value } : { text: value });
    setBusy(false);
    if (result.ok) onCreated(result.value);
    else setFailure(result.failure);
  };

  const switchToText = () => {
    choose("text");
    setTimeout(() => textRef.current?.focus(), 0);
  };

  return (
    <section aria-labelledby="import-start-title" className="import-start">
      <h2 id="import-start-title" className="visually-hidden">
        {t("import.title")}
      </h2>
      <p>{t("import.intro")}</p>
      <div role="tablist" aria-label={t("import.tabs")} className="import-tabs">
        {(["url", "text"] as const).map((name) => (
          <button
            key={name}
            ref={(el) => {
              tabRefs.current[name] = el;
            }}
            type="button"
            role="tab"
            id={`${base}-tab-${name}`}
            aria-selected={tab === name}
            aria-controls={`${base}-panel`}
            tabIndex={tab === name ? 0 : -1}
            className="import-tab"
            onClick={() => choose(name)}
            onKeyDown={onTabKey}
          >
            {t(name === "url" ? "import.tabUrl" : "import.tabText")}
          </button>
        ))}
      </div>
      <form
        id={`${base}-panel`}
        role="tabpanel"
        aria-labelledby={`${base}-tab-${tab}`}
        onSubmit={(e) => void submit(e)}
        noValidate
        aria-busy={busy}
        className="recipe-form"
      >
        <div className="form-field">
          {tab === "url" ? (
            <>
              <label htmlFor={`${base}-url`}>{t("import.urlLabel")}</label>
              <input
                ref={urlRef}
                id={`${base}-url`}
                type="url"
                inputMode="url"
                autoComplete="off"
                value={url}
                placeholder={t("import.urlHint")}
                aria-invalid={localError ? true : undefined}
                aria-describedby={localError ? fieldError : undefined}
                onChange={(e) => setUrl(e.target.value)}
              />
            </>
          ) : (
            <>
              <label htmlFor={`${base}-text`}>{t("import.textLabel")}</label>
              <textarea
                ref={textRef}
                id={`${base}-text`}
                rows={10}
                value={text}
                placeholder={t("import.textHint")}
                aria-invalid={localError ? true : undefined}
                aria-describedby={localError ? fieldError : undefined}
                onChange={(e) => setText(e.target.value)}
              />
            </>
          )}
          {localError && (
            <p id={fieldError} className="field-error">
              {localError}
            </p>
          )}
        </div>
        {busy && <p role="status">{t("import.reading")}</p>}
        {failure && (
          <div role="alert" className="import-problem">
            <p>{importFailureText(t, failure)}</p>
            {tab === "url" && isNoRecipeFound(failure) && (
              <button type="button" className="button button-outline" onClick={switchToText}>
                {t("import.switchToText")}
              </button>
            )}
          </div>
        )}
        <div className="form-actions form-submit">
          <button type="submit" className="button" disabled={busy}>
            {busy ? t("import.reading") : t("import.submit")}
          </button>
        </div>
      </form>
    </section>
  );
}

/** The open drafts of the household, to resume one. Shows nothing while loading, when empty, or on failure's quiet message. */
export function OpenDrafts({ householdId }: { householdId: string }) {
  const { t, formatDayMonth } = useI18n();
  const [state, setState] = useState<{ status: "loading" } | { status: "error" } | { status: "ok"; items: ImportSummary[] }>({ status: "loading" });

  useEffect(() => {
    let cancelled = false;
    void listImports(householdId).then((result) => {
      if (cancelled) return;
      setState(result.ok ? { status: "ok", items: result.value } : { status: "error" });
    });
    return () => {
      cancelled = true;
    };
  }, [householdId]);

  if (state.status === "loading") return null;
  if (state.status === "error") return <p role="alert">{t("import.draftsFailed")}</p>;
  if (state.items.length === 0) return null;
  return (
    <section aria-labelledby="import-drafts-title" className="import-drafts">
      <h2 id="import-drafts-title">{t("import.drafts")}</h2>
      <ul className="row-list">
        {state.items.map((item) => {
          const title = item.title.trim() === "" ? t("import.draftUntitled") : item.title;
          return (
            <li key={item.id} className="row-card import-draft" data-testid="import-draft">
              <div>
                <strong>{title}</strong>
                <p className="import-draft-meta">
                  {item.source === "Url" ? t("import.draftFromUrl") : t("import.draftFromText")} &middot; {formatDayMonth(new Date(item.createdAt))}
                </p>
              </div>
              <Link
                to={`/recipes/import?draft=${item.id}`}
                className="button button-outline"
                aria-label={t("import.draftContinueLabel", { title })}
              >
                {t("import.draftContinue")}
              </Link>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
