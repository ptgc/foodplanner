import { useId, useState, type ReactNode } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call, failureKey } from "../household/call";
import { useLoaded } from "../household/use-loaded";
import { useI18n, type MessageKey } from "../i18n";
import { FIELD_ERRORS, type Action } from "./shared";

type Plan = components["schemas"]["SupportPlanResponse"];
type Limit = components["schemas"]["SupportPlanLimit"];

type Field = "photoImportsPerDay" | "lineHelpPerDay" | "lineHelpPerMonth";
const fields: { id: Field; label: MessageKey }[] = [
  { id: "photoImportsPerDay", label: "support.subscription.photoImportsPerDay" },
  { id: "lineHelpPerDay", label: "support.subscription.lineHelpPerDay" },
  { id: "lineHelpPerMonth", label: "support.subscription.lineHelpPerMonth" },
];

type Draft = { unlimited: boolean; limit: string };
type FieldErrors = Partial<Record<Field | "name", true>>;

/** The field names of a ValidationProblem `errors` object (the server's English texts are never shown). */
function errorFields(body: unknown): FieldErrors {
  const errors = typeof body === "object" && body !== null ? (body as { errors?: unknown }).errors : undefined;
  const result: FieldErrors = {};
  if (typeof errors !== "object" || errors === null) return result;
  for (const key of Object.keys(errors)) {
    if (key === "name" || key === "photoImportsPerDay" || key === "lineHelpPerDay" || key === "lineHelpPerMonth") result[key] = true;
  }

  return result;
}

const draftOf = (limit: Limit): Draft => ({ unlimited: limit.unlimited, limit: limit.limit == null ? "" : String(limit.limit) });

/** What the form sends for one limit. An empty or non-numeric number is sent as no number, so the server answers with a field error. */
function limitOf(draft: Draft): Limit {
  if (draft.unlimited) return { unlimited: true, limit: null };
  const parsed = /^\d{1,6}$/.test(draft.limit.trim()) ? Number(draft.limit.trim()) : null;
  return { unlimited: false, limit: parsed };
}

/**
 * The Subscription panel (T-123): a household's Claude limits. Everyone in a support session sees them; only a write session gets
 * the form, and Save and Reset go through the confirmation of the other write actions. Today's usage is not shown.
 */
export function Subscription({
  householdId,
  householdName,
  write,
  confirm,
}: {
  householdId: string;
  householdName: string;
  write: boolean;
  confirm: (action: Action) => void;
}) {
  const { t } = useI18n();
  const state = useLoaded<Plan>(`subscription:${householdId}`, () =>
    call(() => api.GET("/api/support/households/{targetHouseholdId}/plan", { params: { path: { targetHouseholdId: householdId } } })),
  );
  if (state.error && !state.data) {
    return (
      <div>
        <p role="alert" className="form-error">
          {t("support.loadFailed")} {t(state.error)}
        </p>
        <button type="button" className="button" onClick={state.reload}>
          {t("support.retry")}
        </button>
      </div>
    );
  }

  if (!state.data) return <p role="status">{t("support.loading")}</p>;
  const plan = state.data;
  const valueText = (limit: Limit) => (limit.unlimited ? t("support.subscription.unlimited") : String(limit.limit ?? 0));
  return (
    <>
      <h2>{t("support.tab.subscription")}</h2>
      <dl className="household-facts" data-testid="support-subscription">
        <div>
          <dt>{t("support.subscription.source")}</dt>
          <dd data-testid="support-subscription-source">{t(plan.source === "Custom" ? "support.subscription.source.Custom" : "support.subscription.source.Default")}</dd>
        </div>
        <div>
          <dt>{t("support.subscription.label")}</dt>
          <dd>{plan.name ?? t("support.subscription.noLabel")}</dd>
        </div>
        {fields.map((f) => (
          <div key={f.id}>
            <dt>{t(f.label)}</dt>
            <dd data-testid={`support-subscription-${f.id}`}>
              {valueText(plan[f.id])}
              {plan.source === "Custom" && (
                <span className="support-plan-default"> ({t("support.subscription.defaultIs", { value: valueText(plan.defaults[f.id]) })})</span>
              )}
            </dd>
          </div>
        ))}
      </dl>
      {write && (
        <PlanForm
          key={JSON.stringify(plan)}
          plan={plan}
          householdId={householdId}
          householdName={householdName}
          confirm={confirm}
          onDone={state.reload}
          valueText={valueText}
        />
      )}
    </>
  );
}

function PlanForm({
  plan,
  householdId,
  householdName,
  confirm,
  onDone,
  valueText,
}: {
  plan: Plan;
  householdId: string;
  householdName: string;
  confirm: (action: Action) => void;
  onDone: () => void;
  valueText: (limit: Limit) => string;
}) {
  const { t } = useI18n();
  const nameId = useId();
  const [name, setName] = useState(plan.name ?? "");
  const [drafts, setDrafts] = useState<Record<Field, Draft>>({
    photoImportsPerDay: draftOf(plan.photoImportsPerDay),
    lineHelpPerDay: draftOf(plan.lineHelpPerDay),
    lineHelpPerMonth: draftOf(plan.lineHelpPerMonth),
  });
  const [errors, setErrors] = useState<FieldErrors>({});

  const change = (field: Field, next: Partial<Draft>) => {
    setDrafts((current) => ({ ...current, [field]: { ...current[field], ...next } }));
    setErrors((current) => ({ ...current, [field]: undefined }));
  };

  const save = () => {
    const body = {
      name: name.trim() === "" ? null : name.trim(),
      photoImportsPerDay: limitOf(drafts.photoImportsPerDay),
      lineHelpPerDay: limitOf(drafts.lineHelpPerDay),
      lineHelpPerMonth: limitOf(drafts.lineHelpPerMonth),
    };
    const effect = t("support.subscription.effect", {
      photo: valueText(body.photoImportsPerDay),
      day: valueText(body.lineHelpPerDay),
      month: valueText(body.lineHelpPerMonth),
    });
    setErrors({});
    confirm({
      body: t("support.subscription.confirmSet", { household: householdName, effect }),
      run: async () => {
        try {
          const result = await api.PUT("/api/support/households/{targetHouseholdId}/plan", { params: { path: { targetHouseholdId: householdId } }, body });
          if (result.response.ok) return null;
          if (result.response.status === 400) {
            setErrors(errorFields(result.error));
            return FIELD_ERRORS;
          }

          return failureKey(result.response.status, result.error);
        } catch {
          return "problem.unreachable";
        }
      },
      onDone,
    });
  };

  const reset = () =>
    confirm({
      body: t("support.subscription.confirmReset", {
        household: householdName,
        effect: t("support.subscription.effect", {
          photo: valueText(plan.defaults.photoImportsPerDay),
          day: valueText(plan.defaults.lineHelpPerDay),
          month: valueText(plan.defaults.lineHelpPerMonth),
        }),
      }),
      run: async () => {
        const result = await call(() => api.DELETE("/api/support/households/{targetHouseholdId}/plan", { params: { path: { targetHouseholdId: householdId } } }));
        return result.ok ? null : result.key;
      },
      onDone,
    });

  return (
    <form
      className="support-plan-form"
      aria-label={t("support.subscription.edit")}
      onSubmit={(event) => {
        event.preventDefault();
        save();
      }}
    >
      <h3>{t("support.subscription.edit")}</h3>
      <div className="field">
        <label htmlFor={nameId}>{t("support.subscription.labelField")}</label>
        <input id={nameId} type="text" maxLength={40} value={name} aria-invalid={errors.name ? true : undefined} onChange={(e) => { setName(e.target.value); setErrors((c) => ({ ...c, name: undefined })); }} />
        {errors.name && (
          <p role="alert" className="form-error">
            {t("support.subscription.error.name")}
          </p>
        )}
      </div>
      {fields.map((f) => (
        <LimitField key={f.id} label={t(f.label)} draft={drafts[f.id]} invalid={errors[f.id] === true} onChange={(next) => change(f.id, next)} />
      ))}
      <div className="household-actions">
        <button type="submit" className="button">
          {t("support.subscription.save")}
        </button>
        {plan.source === "Custom" && (
          <button type="button" className="button button-outline" onClick={reset}>
            {t("support.subscription.reset")}
          </button>
        )}
      </div>
    </form>
  );
}

function LimitField({ label, draft, invalid, onChange }: { label: string; draft: Draft; invalid: boolean; onChange: (next: Partial<Draft>) => void }): ReactNode {
  const { t } = useI18n();
  const numberId = useId();
  return (
    <fieldset className="support-fieldset support-plan-limit">
      <legend>{label}</legend>
      <label className="support-radio">
        <input type="checkbox" checked={draft.unlimited} onChange={(e) => onChange({ unlimited: e.target.checked })} />
        <span>{t("support.subscription.unlimitedFor", { field: label })}</span>
      </label>
      <div className="field">
        <label htmlFor={numberId}>{t("support.subscription.numberFor", { field: label })}</label>
        <input
          id={numberId}
          type="number"
          inputMode="numeric"
          min={0}
          max={10000}
          value={draft.unlimited ? "" : draft.limit}
          disabled={draft.unlimited}
          aria-invalid={invalid ? true : undefined}
          onChange={(e) => onChange({ limit: e.target.value })}
        />
        {invalid && (
          <p role="alert" className="form-error">
            {t("support.subscription.error.limit")}
          </p>
        )}
      </div>
    </fieldset>
  );
}
