import { useState } from "react";
import { Link, Navigate, useLocation } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { loginPathFor } from "../auth-paths";
import { call } from "../household/call";
import { powers } from "../household/roles";
import { useLoaded } from "../household/use-loaded";
import { useI18n, type MessageKey } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import { foodTypeKey } from "../recipes/food-types";
import { RecipeImage } from "../recipes/recipe-image";
import { useSession } from "../session";
import "./what-can-i-make.css";

type Suggestions = components["schemas"]["RecipeSuggestionsResponse"];
type Suggestion = components["schemas"]["RecipeSuggestionResponse"];
type Missing = components["schemas"]["MissingIngredientResponse"];

const problemPrefix = "urn:foodplanner:problem:";
const maxChoice = 12;

/** What a card shows after a button: a message, or the items put on the shopping list. */
type CardState = { planned?: "added" | "already"; added?: Missing[]; error?: MessageKey; busy?: "cook" | "add" };

/** Splits the API order into the two groups, keeping the order inside each group. */
export function groupSuggestions(items: readonly Suggestion[]): { ready: Suggestion[]; almost: Suggestion[] } {
  return { ready: items.filter((i) => i.missing.length === 0), almost: items.filter((i) => i.missing.length > 0) };
}

/** "What can I make with what we have?" (T-100): recipes the pantry covers, then those missing one or two things. */
export default function WhatCanIMake() {
  const { session } = useSession();
  // Kid mode never reaches this screen (the shell shows the kid app); if it does, go home. The API refuses anyway.
  if (session.status !== "signedIn") return null; // loading, or signed out: the shell handles both
  if (session.user.kidMode) return <Navigate to="/" replace />;
  const household = session.households.find((h) => h.id === session.activeHouseholdId);
  if (!household) return <Navigate to="/" replace />;
  return <Screen key={household.id} householdId={household.id} canCook={powers(household.role).weekly} />;
}

function Screen({ householdId, canCook }: { householdId: string; canCook: boolean }) {
  const { t } = useI18n();
  const location = useLocation();
  // The recipe's own servings unless the person chooses a number. There is no "usual servings" setting for a household.
  const [servings, setServings] = useState<number | null>(null);
  const [cards, setCards] = useState<Record<string, CardState>>({});
  const { data, error, reload, setData } = useLoaded<Suggestions>(`${householdId}:${servings ?? "own"}`, () =>
    call(
      () =>
        api.GET("/api/households/{householdId}/what-can-i-make", {
          params: { path: { householdId }, query: servings === null ? {} : { servings } },
        }),
      { 401: "problem.unauthenticated" },
    ),
  );
  const empty = data !== null && data.items.length === 0;
  const { data: pantry } = useLoaded<number>(`${householdId}:pantry:${empty}`, async () => {
    if (!empty) return { ok: true, data: undefined, status: 200 };
    const result = await call(() => api.GET("/api/households/{householdId}/pantry", { params: { path: { householdId } } }));
    return result.ok ? { ok: true, data: result.data?.length ?? 0, status: result.status } : result;
  });

  if (error === "problem.unauthenticated") return <Navigate to={loginPathFor(location)} replace />;

  const patch = (recipeId: string, state: CardState) => setCards((prev) => ({ ...prev, [recipeId]: { ...prev[recipeId], ...state } }));

  async function cookToday(item: Suggestion) {
    patch(item.recipeId, { busy: "cook", error: undefined });
    try {
      const { response, data: body, error: problem } = await api.POST("/api/households/{householdId}/what-can-i-make/{recipeId}/cook-today", {
        params: { path: { householdId, recipeId: item.recipeId } },
        body: servings === null ? undefined : { servings },
      });
      if (response.ok && body) {
        patch(item.recipeId, { busy: undefined, planned: body.alreadyPlanned ? "already" : "added" });
        // Update the card in place, so the list does not jump while the person reads it.
        setData((prev) =>
          prev ? { ...prev, items: prev.items.map((i) => (i.recipeId === item.recipeId ? { ...i, plannedToday: true } : i)) } : prev,
        );
        return;
      }
      patch(item.recipeId, { busy: undefined, error: failureOf(response.status, problem) });
      if (response.status === 404) reload();
    } catch {
      patch(item.recipeId, { busy: undefined, error: "problem.unreachable" });
    }
  }

  async function addMissing(item: Suggestion) {
    patch(item.recipeId, { busy: "add", error: undefined });
    try {
      const { response, data: body, error: problem } = await api.POST("/api/households/{householdId}/what-can-i-make/{recipeId}/add-missing", {
        params: { path: { householdId, recipeId: item.recipeId } },
      });
      if (response.ok && body) {
        patch(item.recipeId, { busy: undefined, added: body.missing });
        return;
      }
      patch(item.recipeId, { busy: undefined, error: failureOf(response.status, problem) });
    } catch {
      patch(item.recipeId, { busy: undefined, error: "problem.unreachable" });
    }
  }

  const { ready, almost } = groupSuggestions(data?.items ?? []);

  return (
    <section className="household suggest" aria-labelledby="suggest-title" data-testid="suggest-screen">
      <h1 id="suggest-title">{t("suggest.title")}</h1>
      <p>{t("suggest.intro")}</p>

      <div className="field suggest-servings">
        <label htmlFor="suggest-servings">{t("suggest.servingsLabel")}</label>
        <select
          id="suggest-servings"
          value={servings ?? ""}
          onChange={(e) => {
            setServings(e.target.value === "" ? null : Number(e.target.value));
            setCards({});
          }}
        >
          <option value="">{t("suggest.servingsOwn")}</option>
          {Array.from({ length: maxChoice }, (_, i) => i + 1).map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
      </div>

      {!data && !error && <p role="status">{t("suggest.loading")}</p>}
      {error && (
        <div className="household-card">
          <p role="alert" className="form-error">
            {t("suggest.loadFailed")} {t(error)}
          </p>
          <button type="button" className="button" onClick={reload}>
            {t("suggest.retry")}
          </button>
        </div>
      )}

      {empty && pantry === 0 && (
        <div className="household-card" data-testid="suggest-empty-pantry">
          <h2>{t("suggest.empty.title")}</h2>
          <p>{t("suggest.empty.body")}</p>
          <Link className="button household-link" to="/pantry">
            {t("suggest.empty.link")}
          </Link>
        </div>
      )}
      {empty && pantry !== 0 && pantry !== null && (
        <div className="household-card" data-testid="suggest-none">
          <h2>{t("suggest.none.title")}</h2>
          <p>{t("suggest.none.body")}</p>
          <Link className="button household-link" to="/recipes">
            {t("suggest.none.link")}
          </Link>
        </div>
      )}

      {ready.length > 0 && (
        <Group title={t("suggest.ready.title")} testId="suggest-ready">
          {ready.map((item) => (
            <Card key={item.recipeId} item={item} canCook={canCook} state={cards[item.recipeId]} onCook={cookToday} onAdd={addMissing} />
          ))}
        </Group>
      )}
      {almost.length > 0 && (
        <Group title={t("suggest.missing.title")} testId="suggest-almost">
          {almost.map((item) => (
            <Card key={item.recipeId} item={item} canCook={canCook} state={cards[item.recipeId]} onCook={cookToday} onAdd={addMissing} />
          ))}
        </Group>
      )}
    </section>
  );
}

function failureOf(status: number, body: unknown): MessageKey {
  const type = problemTypeOf(body);
  if (status === 403 && type === `${problemPrefix}kid-mode`) return "suggest.problem.kidMode";
  if (status === 409 && type === `${problemPrefix}recipe-not-planned`) return "suggest.problem.recipeNotPlanned";
  if (status === 409 && type === `${problemPrefix}shopping-list-conflict`) return "suggest.problem.listConflict";
  if (status === 404 && type === `${problemPrefix}recipe-not-found`) return "suggest.problem.recipeGone";
  if (status === 400) return "suggest.problem.servings";
  const typed = problemKey(type);
  if (typed !== "problem.generic") return typed;
  if (status === 401) return "problem.unauthenticated";
  if (status === 403) return "problem.forbidden";
  return "problem.generic";
}

function Group({ title, testId, children }: { title: string; testId: string; children: React.ReactNode }) {
  return (
    <section className="suggest-group" aria-label={title} data-testid={testId}>
      <h2>{title}</h2>
      <ul className="suggest-list">{children}</ul>
    </section>
  );
}

/** One missing thing as text: "200 g cheese", "100 g more cheese", or just the name when the amount cannot be compared. */
export function useMissingText() {
  const { t, formatNumber } = useI18n();
  return (m: Missing): string => {
    const amounts = m.quantities
      .filter((q) => q.amount !== null && q.amount !== undefined)
      .map((q) => `${formatNumber(Number(q.amount), { maximumFractionDigits: 2 })} ${q.unitCode ?? ""}`.trim());
    let item: string = m.name;
    if (m.reason !== "UnitMismatch" && amounts.length > 0) {
      item = t(m.reason === "NotEnough" ? "suggest.line.more" : "suggest.line.amount", { amount: amounts.join(" + "), name: m.name });
    }
    if (m.alternatives.length > 0) {
      item = t("suggest.line.alternatives", { item, alternatives: m.alternatives.map((a) => a.ingredientName).join(", ") });
    }
    return item;
  };
}

function Card({
  item,
  canCook,
  state,
  onCook,
  onAdd,
}: {
  item: Suggestion;
  canCook: boolean;
  state: CardState | undefined;
  onCook: (item: Suggestion) => Promise<void>;
  onAdd: (item: Suggestion) => Promise<void>;
}) {
  const { t } = useI18n();
  const missingText = useMissingText();
  const minutes = item.totalMinutes === null || item.totalMinutes === undefined ? null : Number(item.totalMinutes);
  const likes = Number(item.kidLikes);
  const busy = state?.busy;
  return (
    <li className="suggest-card" data-testid="suggest-card">
      <Link to={`/recipes/${item.recipeId}`} className="suggest-card-link">
        <div className="suggest-thumb">
          <RecipeImage title={item.title} thumbUrl={item.thumbUrl} cardUrl={item.cardUrl} sizes="96px" />
        </div>
        <span className="suggest-card-body">
          <span className="recipe-card-title">{item.title}</span>
          <span className="recipe-card-time">
            {minutes !== null && `${t("recipes.minutes", { minutes })} · `}
            {t("planner.servings", { count: Number(item.servings) })}
          </span>
          <span className="recipe-card-tags">
            {item.foodType !== "Unknown" && (
              <span className="tag-pill food-type-pill" data-testid="suggest-food-type">
                {t(foodTypeKey(item.foodType))}
              </span>
            )}
            {item.plannedToday && (
              <span className="tag-pill suggest-planned" data-testid="suggest-planned-today">
                {t("suggest.plannedToday")}
              </span>
            )}
          </span>
          {likes > 0 && (
            <span className="suggest-likes" data-testid="suggest-likes">
              <span aria-hidden="true">♥ </span>
              {t("suggest.likes", { count: likes })}
            </span>
          )}
          {item.cookedRecently && (
            <span className="suggest-hint" data-testid="suggest-cooked-recently">
              {t("suggest.cookedRecently")}
            </span>
          )}
        </span>
      </Link>
      {item.missing.length > 0 && (
        <ul className="suggest-missing" data-testid="suggest-missing">
          {item.missing.map((m) => (
            <li key={m.ingredientId}>{t("suggest.missingLine", { item: missingText(m) })}</li>
          ))}
        </ul>
      )}
      {canCook && (
        <div className="suggest-actions">
          {!item.plannedToday && (
            <button
              type="button"
              className="button"
              disabled={busy !== undefined}
              aria-label={t("suggest.cookTodayFor", { title: item.title })}
              onClick={() => void onCook(item)}
            >
              {busy === "cook" ? t("suggest.cooking") : t("suggest.cookToday")}
            </button>
          )}
          {item.plannedToday && item.missing.length > 0 && !state?.added && (
            <button
              type="button"
              className="button"
              disabled={busy !== undefined}
              aria-label={t("suggest.addMissingFor", { title: item.title })}
              onClick={() => void onAdd(item)}
            >
              {busy === "add" ? t("suggest.adding") : t("suggest.addMissing")}
            </button>
          )}
        </div>
      )}
      <div role="status" className="suggest-status">
        {state?.planned && <p data-testid="suggest-planned-note">{t(state.planned === "added" ? "suggest.planned" : "suggest.alreadyPlanned")}</p>}
        {state?.added && (
          <p data-testid="suggest-added-note">
            {state.added.length === 0
              ? t("suggest.addedNothing")
              : t("suggest.added", { items: state.added.map(missingText).join(", ") })}{" "}
            {state.added.length > 0 && t("suggest.addedNote")}{" "}
            <Link to="/shopping">{t("suggest.openShopping")}</Link>
          </p>
        )}
      </div>
      {state?.error && (
        <p role="alert" className="form-error" data-testid="suggest-card-error">
          {t(state.error)}
        </p>
      )}
    </li>
  );
}
