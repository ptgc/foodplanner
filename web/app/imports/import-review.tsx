import { useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router";
import { useI18n, type I18n } from "../i18n";
import { ErrorText, fieldProps } from "../recipes/form-parts";
import { emptyStep, fieldId, limits, newKey, orderedKeys, type FieldErrors, type StepRow } from "../recipes/recipe-form";
import { StepRows } from "../recipes/step-rows";
import { useUnits } from "../recipes/use-catalog";
import {
  discardImport,
  getImport,
  importFailureText,
  saveImport,
  updateImport,
  type ImportDraft,
  type ImportFailure,
  type ImportLine,
  type LineEdit,
} from "./import-api";
import { attentionIndexes, lineOfKey, mapImportErrors, removalEdit } from "./import-lines";
import { LineCard } from "./line-card";

type Loaded = { status: "loading" } | { status: "error"; failure: ImportFailure } | { status: "gone" } | { status: "ok"; draft: ImportDraft };

/** Step 2 loader: reads the draft (so a reload or "Continue" from the list works the same) and shows the review. */
export function ImportReview({ householdId, draftId }: { householdId: string; draftId: string }) {
  const { t } = useI18n();
  const [state, setState] = useState<Loaded>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    void getImport(householdId, draftId).then((result) => {
      if (cancelled) return;
      if (result.ok) setState(result.value.status === "Draft" ? { status: "ok", draft: result.value } : { status: "gone" });
      else if (result.failure.kind === "problem" && (result.failure.status === 404 || result.failure.status === 409)) setState({ status: "gone" });
      else setState({ status: "error", failure: result.failure });
    });
    return () => {
      cancelled = true;
    };
  }, [householdId, draftId, attempt]);

  if (state.status === "loading") return <p role="status">{t("import.reviewLoading")}</p>;
  if (state.status === "gone") {
    return (
      <div role="alert">
        <p>{t("import.reviewGone")}</p>
        <Link to="/recipes/import" className="button">
          {t("import.startOver")}
        </Link>
      </div>
    );
  }
  if (state.status === "error") {
    return (
      <div role="alert">
        <p>{importFailureText(t, state.failure)}</p>
        <button type="button" className="button" onClick={() => setAttempt((n) => n + 1)}>
          {t("recipes.retry")}
        </button>
      </div>
    );
  }
  return <ReviewForm key={state.draft.id} householdId={householdId} initial={state.draft} />;
}

const whole = (text: string): number | null => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : null);

/** The same limits as the API for the part of the draft that is not an ingredient line. */
export function validateHeader(form: { title: string; servings: string; prepMinutes: string; cookMinutes: string; steps: StepRow[] }): FieldErrors {
  const errors: FieldErrors = {};
  const title = form.title.trim();
  if (title.length < 1 || title.length > limits.titleMax || /\p{Cc}/u.test(title)) {
    errors.title = { key: "recipe.error.title", params: { max: limits.titleMax } };
  }
  const servings = whole(form.servings);
  if (servings === null || servings < limits.servingsMin || servings > limits.servingsMax) {
    errors.servings = { key: "recipe.error.servings", params: { min: limits.servingsMin, max: limits.servingsMax } };
  }
  for (const field of ["prepMinutes", "cookMinutes"] as const) {
    if (form[field].trim() === "") continue;
    const minutes = whole(form[field]);
    if (minutes === null || minutes > limits.minutesMax) errors[field] = { key: "recipe.error.minutes", params: { max: limits.minutesMax } };
  }
  form.steps.forEach((step, i) => {
    const text = step.text.trim();
    if (text === "") errors[`steps[${i}].text`] = { key: "import.error.stepEmpty" };
    else if (text.length > limits.stepMax) errors[`steps[${i}].text`] = { key: "recipe.error.step", params: { max: limits.stepMax } };
  });
  return errors;
}

function labelFor(t: I18n["t"], key: string): string | null {
  const line = /^lines\[(\d+)\]/.exec(key);
  if (line) return t("import.lineN", { n: Number(line[1]) + 1 });
  const step = /^steps\[(\d+)\]/.exec(key);
  if (step) return t("recipe.stepN", { n: Number(step[1]) + 1 });
  const names: Record<string, Parameters<I18n["t"]>[0]> = {
    title: "recipe.fieldTitle",
    servings: "recipe.fieldServings",
    prepMinutes: "recipe.fieldPrep",
    cookMinutes: "recipe.fieldCook",
    steps: "recipe.stepsHeading",
  };
  const name = names[key];
  return name ? t(name) : null;
}

function focusKey(key: string) {
  const row = lineOfKey(key);
  const el = document.getElementById(fieldId(key)) ?? (row !== null ? document.getElementById(`import-line-${row}`) : null);
  el?.focus();
}

function withoutLineErrors(errors: FieldErrors, row?: number): FieldErrors {
  return Object.fromEntries(Object.entries(errors).filter(([key]) => { const r = lineOfKey(key); return r === null || (row !== undefined && r !== row); }));
}

function ReviewForm({ householdId, initial }: { householdId: string; initial: ImportDraft }) {
  const { t } = useI18n();
  const navigate = useNavigate();
  const units = useUnits();
  const draftId = initial.id;
  const [lines, setLines] = useState<ImportLine[]>(initial.lines);
  const [canSave, setCanSave] = useState(initial.canSave);
  const [form, setForm] = useState(() => ({
    title: initial.title,
    servings: String(initial.servings ?? 4),
    prepMinutes: initial.prepMinutes === null ? "" : String(initial.prepMinutes),
    cookMinutes: initial.cookMinutes === null ? "" : String(initial.cookMinutes),
    steps: initial.steps.map((text) => ({ key: newKey(), text })) as StepRow[],
  }));
  const [errors, setErrors] = useState<FieldErrors>({});
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [saving, setSaving] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [summaryShown, setSummaryShown] = useState(0);
  const summaryRef = useRef<HTMLDivElement>(null);
  const linesHeadingRef = useRef<HTMLHeadingElement>(null);
  const confirmRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (summaryShown > 0) summaryRef.current?.focus();
  }, [summaryShown]);
  useEffect(() => {
    if (confirming) confirmRef.current?.focus();
  }, [confirming]);

  const attention = attentionIndexes(lines);
  const set = (patch: Partial<typeof form>) => setForm((f) => ({ ...f, ...patch }));
  const showErrors = (next: FieldErrors) => {
    setErrors(next);
    setSummaryShown((n) => n + 1);
  };

  const takeAnswer = (draft: ImportDraft) => {
    setLines(draft.lines);
    setCanSave(draft.canSave);
  };

  const failWith = (failure: ImportFailure, fallback: string, lineIndex?: number): FieldErrors | null => {
    if (failure.kind === "problem" && failure.status === 400 && failure.fieldErrors) return mapImportErrors(failure.fieldErrors, lineIndex);
    setNotice(failure.kind === "problem" && failure.problemType === undefined ? fallback : importFailureText(t, failure));
    return null;
  };

  const applyEdit = async (index: number, edit: LineEdit): Promise<boolean> => {
    setBusy(true);
    setNotice(null);
    const result = await updateImport(householdId, draftId, { lines: [edit] });
    setBusy(false);
    if (result.ok) {
      takeAnswer(result.value);
      setErrors((e) => withoutLineErrors(e, index));
      return true;
    }
    const mapped = failWith(result.failure, t("import.editFailed"), index);
    if (mapped) setErrors((e) => ({ ...withoutLineErrors(e, index), ...mapped }));
    return false;
  };

  const removeLine = async (index: number) => {
    setBusy(true);
    setNotice(null);
    const result = await updateImport(householdId, draftId, { lines: [removalEdit(index)] });
    setBusy(false);
    if (result.ok) {
      takeAnswer(result.value);
      setErrors((e) => withoutLineErrors(e));
      linesHeadingRef.current?.focus();
    } else failWith(result.failure, t("import.editFailed"));
  };

  const save = async () => {
    if (busy || saving) return;
    setNotice(null);
    const local = validateHeader(form);
    if (Object.keys(local).length > 0) {
      showErrors(local);
      return;
    }
    setSaving(true);
    const put = await updateImport(householdId, draftId, {
      title: form.title.trim(),
      servings: Number(form.servings),
      prepMinutes: form.prepMinutes.trim() === "" ? null : Number(form.prepMinutes),
      cookMinutes: form.cookMinutes.trim() === "" ? null : Number(form.cookMinutes),
      steps: form.steps.map((s) => s.text.trim()),
    });
    if (!put.ok) {
      setSaving(false);
      const mapped = failWith(put.failure, t("import.saveFailed"));
      if (mapped) showErrors(mapped);
      else setSummaryShown((n) => n + 1);
      return;
    }
    takeAnswer(put.value);
    const saved = await saveImport(householdId, draftId);
    if (saved.ok) {
      void navigate(`/recipes/${saved.value.recipeId}`);
      return;
    }
    setSaving(false);
    const mapped = failWith(saved.failure, t("import.saveFailed"));
    if (mapped) {
      // The server's view of the lines is the truth: re-read them so the cards show what is still missing.
      const fresh = await getImport(householdId, draftId);
      if (fresh.ok) takeAnswer(fresh.value);
      showErrors(mapped);
    } else setSummaryShown((n) => n + 1);
  };

  const discard = async () => {
    setBusy(true);
    setNotice(null);
    const result = await discardImport(householdId, draftId);
    if (result.ok || (result.failure.kind === "problem" && result.failure.status === 404)) {
      void navigate("/recipes/import", { replace: true });
      return;
    }
    setBusy(false);
    setNotice(importFailureText(t, result.failure));
  };

  const keys = orderedKeys(errors);
  const blocked = attention.length > 0 || !canSave;

  return (
    <div className="import-review">
      <p>{t("import.reviewIntro")}</p>
      {initial.warnings.length > 0 && <p className="import-notes">{t("import.notes", { count: initial.warnings.length })}</p>}

      {(keys.length > 0 || notice) && (
        <div ref={summaryRef} tabIndex={-1} className="error-summary" data-testid="error-summary" aria-labelledby="import-error-title">
          <p id="import-error-title" className="error-summary-title">
            {keys.length > 0 ? t("recipe.errorSummary", { count: keys.length }) : t("import.saveFailed")}
          </p>
          {notice && <p role="alert">{notice}</p>}
          {keys.length > 0 && (
            <ul>
              {keys.map((key) => {
                const error = errors[key]!;
                const label = labelFor(t, key);
                return (
                  <li key={key}>
                    <button type="button" className="link-button" onClick={() => focusKey(key)}>
                      {label ? `${label}: ` : ""}
                      {t(error.key, error.params)}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      )}

      <form
        noValidate
        className="recipe-form"
        aria-busy={saving}
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        <div className="form-field">
          <label htmlFor={fieldId("title")}>{t("recipe.fieldTitle")}</label>
          <input {...fieldProps("title", errors)} value={form.title} autoComplete="off" onChange={(e) => set({ title: e.target.value })} />
          <ErrorText name="title" errors={errors} />
        </div>
        <div className="numbers">
          <div className="form-field">
            <label htmlFor={fieldId("servings")}>{t("recipe.fieldServings")}</label>
            <input {...fieldProps("servings", errors)} inputMode="numeric" autoComplete="off" value={form.servings} onChange={(e) => set({ servings: e.target.value })} />
            <ErrorText name="servings" errors={errors} />
          </div>
          <div className="form-field">
            <label htmlFor={fieldId("prepMinutes")}>{t("recipe.fieldPrep")}</label>
            <input {...fieldProps("prepMinutes", errors)} inputMode="numeric" autoComplete="off" value={form.prepMinutes} onChange={(e) => set({ prepMinutes: e.target.value })} />
            <ErrorText name="prepMinutes" errors={errors} />
          </div>
          <div className="form-field">
            <label htmlFor={fieldId("cookMinutes")}>{t("recipe.fieldCook")}</label>
            <input {...fieldProps("cookMinutes", errors)} inputMode="numeric" autoComplete="off" value={form.cookMinutes} onChange={(e) => set({ cookMinutes: e.target.value })} />
            <ErrorText name="cookMinutes" errors={errors} />
          </div>
        </div>

        <section aria-labelledby="import-lines-heading">
          <h2 id="import-lines-heading" ref={linesHeadingRef} tabIndex={-1}>
            {t("import.linesHeading")}
          </h2>
          <div aria-live="polite" data-testid="attention-summary" className={attention.length > 0 ? "import-summary import-summary-bad" : "import-summary"}>
            {attention.length > 0 ? (
              <>
                <p>
                  <span aria-hidden="true">&#9888;</span> <strong>{t("import.needAttention", { count: attention.length })}</strong>
                </p>
                <ul>
                  {attention.map((i) => (
                    <li key={i}>
                      <button type="button" className="link-button" onClick={() => document.getElementById(`import-line-${i}`)?.focus()}>
                        {t("import.goToLine", { n: i + 1, original: lines[i]!.original })}
                      </button>
                    </li>
                  ))}
                </ul>
              </>
            ) : (
              <p>
                <span aria-hidden="true">&#10003;</span> {t("import.allMatched")}
              </p>
            )}
          </div>
          <ol className="row-list">
            {lines.map((line, index) => (
              <LineCard
                key={`${index}:${line.original}`}
                householdId={householdId}
                index={index}
                line={line}
                units={units}
                serverErrors={Object.fromEntries(Object.entries(errors).filter(([key]) => lineOfKey(key) === index))}
                busy={busy || saving}
                onApply={applyEdit}
                onRemove={(i) => void removeLine(i)}
              />
            ))}
          </ol>
        </section>

        <section aria-labelledby="import-steps-heading">
          <h2 id="import-steps-heading">{t("recipe.stepsHeading")}</h2>
          <StepRows rows={form.steps} errors={errors} onChange={(steps) => set({ steps: steps.length === 0 ? [emptyStep()] : steps })} />
        </section>

        <div className="form-actions form-submit">
          <button type="submit" className="button" disabled={blocked || busy || saving} aria-describedby={blocked ? "import-save-hint" : undefined}>
            {saving ? t("import.saving") : t("import.save")}
          </button>
        </div>
        {blocked && (
          <p id="import-save-hint" className="import-save-hint">
            {t("import.saveBlocked")}
          </p>
        )}
      </form>

      <section className="danger-zone" aria-label={t("import.discard")}>
        {!confirming ? (
          <button type="button" className="button button-danger" disabled={busy || saving} onClick={() => setConfirming(true)}>
            {t("import.discard")}
          </button>
        ) : (
          <div ref={confirmRef} tabIndex={-1} role="group" aria-labelledby="import-discard-text" className="delete-confirm">
            <p id="import-discard-text">{t("import.discardConfirm")}</p>
            <div className="form-actions">
              <button type="button" className="button button-danger" disabled={busy} onClick={() => void discard()}>
                {busy ? t("import.discarding") : t("import.discardYes")}
              </button>
              <button type="button" className="button button-outline" disabled={busy} onClick={() => setConfirming(false)}>
                {t("import.discardKeep")}
              </button>
            </div>
          </div>
        )}
      </section>
    </div>
  );
}
