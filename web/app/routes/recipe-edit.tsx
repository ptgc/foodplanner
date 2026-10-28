import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useI18n, type I18n } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import { ErrorText, fieldProps } from "../recipes/form-parts";
import { IngredientRows } from "../recipes/ingredient-rows";
import {
  checkImage,
  emptyForm,
  fieldId,
  fromRecipe,
  limits,
  mapServerErrors,
  orderedKeys,
  renameIngredients,
  serverErrorsOf,
  toRequest,
  validate,
  type FieldError,
  type FieldErrors,
  type FormState,
} from "../recipes/recipe-form";
import { RecipeImage } from "../recipes/recipe-image";
import { StepRows } from "../recipes/step-rows";
import { canEditRecipes, createTag, useActiveRole, useUnits, type Tag } from "../recipes/use-catalog";
import { useTags, type Failure } from "../recipes/use-recipes";
import { useSession } from "../session";
import { failureText } from "./recipes";

type Recipe = components["schemas"]["RecipeResponse"];

/**
 * `/recipes/new` and `/recipes/:id/edit` (Adult or Owner). A Child sees a message instead of the form; the API
 * refuses their writes with 403 anyway (T-021), this only keeps the controls away.
 */
export default function RecipeEdit() {
  const { t } = useI18n();
  const { session } = useSession();
  const role = useActiveRole();
  const { id } = useParams();
  if (session.status !== "signedIn") return null;
  if (!session.activeHouseholdId) return <p>{t("recipes.noHousehold")}</p>;
  if (!canEditRecipes(role)) {
    return (
      <section className="recipe-edit">
        <Link to={id ? `/recipes/${id}` : "/recipes"} className="back-link">
          {t("recipes.back")}
        </Link>
        <p role="alert" data-testid="no-access">
          {t("recipe.noAccess")}
        </p>
      </section>
    );
  }
  return <Loader key={`${session.activeHouseholdId}/${id ?? "new"}`} householdId={session.activeHouseholdId} id={id} />;
}

type Loaded = { kind: "ok"; recipe: Recipe | null } | { kind: "notFound" } | { kind: "error"; failure: Failure };

function Loader({ householdId, id }: { householdId: string; id: string | undefined }) {
  const { t, locale } = useI18n();
  const [state, setState] = useState<Loaded | null>(id ? null : { kind: "ok", recipe: null });

  useEffect(() => {
    if (!id) return;
    let cancelled = false;
    void (async () => {
      let next: Loaded;
      try {
        const { data, error, response } = await api.GET("/api/households/{householdId}/recipes/{recipeId}", {
          params: { path: { householdId, recipeId: id } },
        });
        if (data) next = { kind: "ok", recipe: data };
        else if (response.status === 404) next = { kind: "notFound" };
        else next = { kind: "error", failure: { problemType: problemTypeOf(error) } };
      } catch {
        next = { kind: "error", failure: "unreachable" };
      }
      // A re-read after a language switch (T-078) only brings new ingredient names: if it fails, the form the user is
      // typing in stays, and the form itself keeps its fields (RecipeForm renames the ingredient rows).
      if (!cancelled) setState((prev) => (prev?.kind === "ok" && next.kind !== "ok" ? prev : next));
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, id, locale]);

  if (!state) return <p role="status">{t("common.loading")}</p>;
  if (state.kind !== "ok") {
    return (
      <section className="recipe-edit">
        <Link to="/recipes" className="back-link">
          {t("recipes.back")}
        </Link>
        <p role="alert">{state.kind === "notFound" ? t("recipes.notFound") : failureText(t, state.failure)}</p>
      </section>
    );
  }
  return <RecipeForm householdId={householdId} recipe={state.recipe} />;
}

type Picture = {
  /** A file chosen for upload; sent after the recipe is saved. */
  file: File | null;
  previewUrl: string | null;
  /** Remove the stored picture on save. */
  remove: boolean;
};

const noPicture: Picture = { file: null, previewUrl: null, remove: false };

function previewOf(file: File): string | null {
  return typeof URL.createObjectURL === "function" ? URL.createObjectURL(file) : null;
}

/** Label for an error key in the summary: "Title", "Ingredient 2", "Step 1". */
function labelFor(t: I18n["t"], key: string): string | null {
  const row = /^(ingredients|steps)\[(\d+)\]/.exec(key);
  if (row) return t(row[1] === "steps" ? "recipe.stepN" : "recipe.ingredientN", { n: Number(row[2]) + 1 });
  const names: Record<string, Parameters<I18n["t"]>[0]> = {
    title: "recipe.fieldTitle",
    description: "recipe.fieldDescription",
    servings: "recipe.fieldServings",
    prepMinutes: "recipe.fieldPrep",
    cookMinutes: "recipe.fieldCook",
    ingredients: "recipe.ingredientsHeading",
    steps: "recipe.stepsHeading",
    sourceUrl: "recipe.fieldSource",
    sourceName: "recipe.sourceKindLegend",
    file: "recipe.pictureHeading",
  };
  if (key.startsWith("tagIds")) return t("recipe.tagsHeading");
  const name = names[key];
  return name ? t(name) : null;
}

/** Moves focus to the field of an error key (a tag error goes to the tag group, an unknown key to nothing). */
function focusField(key: string) {
  const base = key.replace(/\[\d+\]$/, "");
  (document.getElementById(fieldId(key)) ?? document.getElementById(fieldId(base)))?.focus();
}

function RecipeForm({ householdId, recipe }: { householdId: string; recipe: Recipe | null }) {
  const { t, locale } = useI18n();
  const navigate = useNavigate();
  const units = useUnits();
  const loadedTags = useTags(householdId);
  const [extraTags, setExtraTags] = useState<Tag[]>([]);
  const [form, setForm] = useState<FormState>(() => (recipe ? fromRecipe(recipe, locale === "nb") : emptyForm()));
  const [errors, setErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState<Failure | null>(null);
  const [busy, setBusy] = useState(false);
  const [picture, setPicture] = useState<Picture>(noPicture);
  const currentImage = recipe?.image ?? null;
  /** Set once the recipe exists, so a second "save" after a failed picture updates it instead of creating another. */
  const [savedId, setSavedId] = useState<string | null>(recipe?.id ?? null);
  const summaryRef = useRef<HTMLDivElement>(null);
  const [summaryShown, setSummaryShown] = useState(0);

  // The recipe is read again after a language switch: take the new ingredient names, keep everything typed (T-078).
  const [seenRecipe, setSeenRecipe] = useState(recipe);
  if (recipe !== seenRecipe) {
    setSeenRecipe(recipe);
    if (recipe) setForm((f) => renameIngredients(f, recipe));
  }

  // Show the error summary and move focus to it (once per failed attempt) so keyboard and screen reader users land on it.
  useEffect(() => {
    if (summaryShown > 0) summaryRef.current?.focus();
  }, [summaryShown]);

  // Free the preview's memory when it is replaced or the page closes.
  useEffect(() => {
    const url = picture.previewUrl;
    return () => {
      if (url && typeof URL.revokeObjectURL === "function") URL.revokeObjectURL(url);
    };
  }, [picture.previewUrl]);

  const tags = useMemo(() => {
    const seen = new Set<string>();
    return [...loadedTags, ...(recipe?.tags ?? []), ...extraTags].filter((tag) => !seen.has(tag.id) && seen.add(tag.id));
  }, [loadedTags, recipe, extraTags]);

  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setForm((f) => ({ ...f, [key]: value }));

  const fail = (next: FieldErrors, other: Failure | null = null) => {
    setErrors(next);
    setFailure(other);
    setSummaryShown((n) => n + 1);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const clientErrors = validate(form);
    if (Object.keys(clientErrors).length > 0) {
      fail(errors.file ? { ...clientErrors, file: errors.file } : clientErrors);
      return;
    }
    setBusy(true);
    setErrors({});
    setFailure(null);
    try {
      const body = toRequest(form);
      const existing = savedId;
      const result = existing
        ? await api.PUT("/api/households/{householdId}/recipes/{recipeId}", {
            params: { path: { householdId, recipeId: existing } },
            body,
          })
        : await api.POST("/api/households/{householdId}/recipes", { params: { path: { householdId } }, body });
      if (!result.data) {
        const fields = serverErrorsOf(result.error);
        if (result.response.status === 400 && fields) fail(mapServerErrors(fields, form));
        else fail({}, { problemType: problemTypeOf(result.error) });
        return;
      }
      setSavedId(result.data.id);

      const imageProblem = await applyPicture(householdId, result.data.id, picture, currentImage !== null);
      if (imageProblem) {
        // The recipe is saved; stay here so the picture can be fixed (a new save updates the same recipe).
        fail({ file: imageProblem });
        return;
      }
      void navigate(`/recipes/${result.data.id}`);
    } catch {
      fail({}, "unreachable");
    } finally {
      setBusy(false);
    }
  };

  const onFile = (file: File | undefined) => {
    if (!file) return;
    const problem = checkImage(file);
    if (problem) {
      setErrors((e) => ({ ...e, file: problem }));
      return;
    }
    setErrors((e) => Object.fromEntries(Object.entries(e).filter(([key]) => key !== "file")));
    setPicture({ file, previewUrl: previewOf(file), remove: false });
  };

  const keys = orderedKeys(errors);
  const showingPicture = picture.file ? "new" : currentImage && !picture.remove ? "current" : "none";
  const id = savedId;
  const saving = busy;

  return (
    <section className="recipe-edit" aria-labelledby="recipe-edit-title">
      <Link to={recipe ? `/recipes/${recipe.id}` : "/recipes"} className="back-link">
        {t("recipes.back")}
      </Link>
      <h1 id="recipe-edit-title">{recipe ? t("recipe.editTitle") : t("recipe.newTitle")}</h1>

      <form onSubmit={(e) => void submit(e)} noValidate className="recipe-form" aria-busy={saving}>
        {(keys.length > 0 || failure) && (
          <div ref={summaryRef} tabIndex={-1} className="error-summary" data-testid="error-summary" aria-labelledby="error-summary-title">
            <p id="error-summary-title" className="error-summary-title">
              {failure ? t("recipe.saveFailed") : t("recipe.errorSummary", { count: keys.length })}
            </p>
            {failure && <p role="alert">{failureText(t, failure)}</p>}
            {keys.length > 0 && (
              <ul>
                {keys.map((key) => {
                  const error = errors[key]!;
                  const label = labelFor(t, key);
                  return (
                    <li key={key}>
                      <button type="button" className="link-button" onClick={() => focusField(key)}>
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

        <div className="form-field">
          <label htmlFor={fieldId("title")}>{t("recipe.fieldTitle")}</label>
          <input {...fieldProps("title", errors)} value={form.title} autoComplete="off" onChange={(e) => set("title", e.target.value)} />
          <ErrorText name="title" errors={errors} />
        </div>

        <div className="form-field">
          <label htmlFor={fieldId("description")}>{t("recipe.fieldDescription")}</label>
          <textarea {...fieldProps("description", errors)} rows={3} value={form.description} onChange={(e) => set("description", e.target.value)} />
          <ErrorText name="description" errors={errors} />
        </div>

        <div className="numbers">
          <div className="form-field">
            <label htmlFor={fieldId("servings")}>{t("recipe.fieldServings")}</label>
            <input {...fieldProps("servings", errors)} inputMode="numeric" autoComplete="off" value={form.servings} onChange={(e) => set("servings", e.target.value)} />
            <ErrorText name="servings" errors={errors} />
          </div>
          <div className="form-field">
            <label htmlFor={fieldId("prepMinutes")}>{t("recipe.fieldPrep")}</label>
            <input {...fieldProps("prepMinutes", errors)} inputMode="numeric" autoComplete="off" value={form.prepMinutes} onChange={(e) => set("prepMinutes", e.target.value)} />
            <ErrorText name="prepMinutes" errors={errors} />
          </div>
          <div className="form-field">
            <label htmlFor={fieldId("cookMinutes")}>{t("recipe.fieldCook")}</label>
            <input {...fieldProps("cookMinutes", errors)} inputMode="numeric" autoComplete="off" value={form.cookMinutes} onChange={(e) => set("cookMinutes", e.target.value)} />
            <ErrorText name="cookMinutes" errors={errors} />
          </div>
        </div>

        <TagPicker
          householdId={householdId}
          tags={tags}
          selected={form.tagIds}
          errors={errors}
          onToggle={(tagId) =>
            set("tagIds", form.tagIds.includes(tagId) ? form.tagIds.filter((x) => x !== tagId) : [...form.tagIds, tagId])
          }
          onCreated={(tag) => {
            setExtraTags((list) => [...list, tag]);
            set("tagIds", [...form.tagIds, tag.id]);
          }}
        />

        <section aria-labelledby="ingredients-heading">
          <h2 id="ingredients-heading">{t("recipe.ingredientsHeading")}</h2>
          <IngredientRows householdId={householdId} rows={form.ingredients} units={units} errors={errors} onChange={(rows) => set("ingredients", rows)} />
        </section>

        <section aria-labelledby="steps-heading">
          <h2 id="steps-heading">{t("recipe.stepsHeading")}</h2>
          <StepRows rows={form.steps} errors={errors} onChange={(rows) => set("steps", rows)} />
        </section>

        <div className="form-field source-field">
          <span id="source-kind-label">{t("recipe.sourceKindLegend")}</span>
          <div className="chip-group" role="group" aria-labelledby="source-kind-label">
            {(["website", "book"] as const).map((kind) => (
              <button
                key={kind}
                type="button"
                className="chip"
                aria-pressed={form.sourceKind === kind}
                data-testid={`source-kind-${kind}`}
                // Switching type clears the fields of the other type, so a stale link or name is never sent.
                onClick={() => form.sourceKind !== kind && setForm((f) => ({ ...f, sourceKind: kind, sourceUrl: "", sourceName: "" }))}
              >
                {t(kind === "website" ? "recipe.sourceKindWebsite" : "recipe.sourceKindBook")}
              </button>
            ))}
          </div>
          {form.sourceKind === "website" ? (
            <>
              <label htmlFor={fieldId("sourceUrl")}>{t("recipe.fieldSource")}</label>
              <input
                {...fieldProps("sourceUrl", errors)}
                type="text"
                inputMode="url"
                autoComplete="off"
                value={form.sourceUrl}
                onChange={(e) => set("sourceUrl", e.target.value)}
              />
              <ErrorText name="sourceUrl" errors={errors} />
              <label htmlFor={fieldId("sourceName")}>{t("recipe.fieldSourceName")}</label>
              <input
                {...fieldProps("sourceName", errors)}
                type="text"
                autoComplete="off"
                value={form.sourceName}
                onChange={(e) => set("sourceName", e.target.value)}
              />
              <ErrorText name="sourceName" errors={errors} />
            </>
          ) : (
            <>
              <label htmlFor={fieldId("sourceName")}>{t("recipe.fieldSourceBook")}</label>
              <input
                {...fieldProps("sourceName", errors)}
                type="text"
                autoComplete="off"
                value={form.sourceName}
                onChange={(e) => set("sourceName", e.target.value)}
              />
              <ErrorText name="sourceName" errors={errors} />
            </>
          )}
        </div>

        <section aria-labelledby="picture-heading" className="picture-section">
          <h2 id="picture-heading">{t("recipe.pictureHeading")}</h2>
          {showingPicture === "new" && picture.previewUrl && (
            <div className="recipe-image">
              <img src={picture.previewUrl} alt={t("recipe.pictureAlt")} data-testid="picture-preview" />
            </div>
          )}
          {showingPicture === "current" && currentImage && (
            <RecipeImage
              title={form.title}
              thumbUrl={currentImage.thumbUrl}
              cardUrl={currentImage.cardUrl}
              fullUrl={currentImage.fullUrl}
              sizes="(min-width: 40rem) 40rem, 100vw"
            />
          )}
          {showingPicture === "none" && <p>{picture.remove ? t("recipe.pictureRemoving") : t("recipe.pictureNone")}</p>}
          {showingPicture === "new" && <p>{t("recipe.pictureNew")}</p>}
          <div className="form-field">
            <label htmlFor={fieldId("file")}>{t("recipe.pictureFile")}</label>
            <input
              {...fieldProps("file", errors)}
              type="file"
              accept="image/jpeg,image/png,image/webp"
              onChange={(e) => {
                onFile(e.target.files?.[0]);
                e.target.value = "";
              }}
            />
            <ErrorText name="file" errors={errors} />
          </div>
          {(picture.file || currentImage) && (
            <button
              type="button"
              className="button button-outline"
              onClick={() => setPicture((p) => (p.file ? noPicture : { ...p, remove: !p.remove }))}
            >
              {picture.remove && !picture.file ? t("recipe.pictureUndo") : t("recipe.pictureRemove")}
            </button>
          )}
        </section>

        <div className="form-actions form-submit">
          <button type="submit" className="button" disabled={saving}>
            {saving ? t("recipe.saving") : t("recipe.save")}
          </button>
          <Link to={id ? `/recipes/${id}` : "/recipes"} className="button button-outline">
            {t("recipe.cancel")}
          </Link>
        </div>
      </form>

      {recipe && <DeleteRecipe householdId={householdId} recipe={recipe} />}
    </section>
  );
}

/**
 * Applies the picture choice after the recipe is saved: upload the chosen file, or delete the stored picture.
 * Returns the field error to show, or null when nothing failed.
 */
async function applyPicture(
  householdId: string,
  recipeId: string,
  picture: Picture,
  hasStored: boolean,
): Promise<FieldError | null> {
  try {
    if (picture.file) {
      const { error, response } = await api.PUT("/api/households/{householdId}/recipes/{recipeId}/image", {
        params: { path: { householdId, recipeId } },
        // The generated schema types a binary part as `string` (OpenAPI format: binary); the File is what is sent.
        body: { file: picture.file as unknown as string },
        bodySerializer: (body) => {
          const data = new FormData();
          if (body?.file) data.append("file", picture.file as File);
          return data;
        },
      });
      if (response.ok) return null;
      if (response.status === 413 || problemTypeOf(error)?.endsWith(":image-too-large")) return { key: "recipe.error.imageSize", params: { max: limits.imageMaxBytes / (1024 * 1024) } };
      return { key: "recipe.error.imageInvalid" };
    }
    if (picture.remove && hasStored) {
      const { response } = await api.DELETE("/api/households/{householdId}/recipes/{recipeId}/image", {
        params: { path: { householdId, recipeId } },
      });
      // 404 means there was no picture any more, which is what was wanted.
      if (response.ok || response.status === 404) return null;
      return { key: "recipe.error.unknown" };
    }
    return null;
  } catch {
    return { key: "problem.unreachable" };
  }
}

function TagPicker({
  householdId,
  tags,
  selected,
  errors,
  onToggle,
  onCreated,
}: {
  householdId: string;
  tags: Tag[];
  selected: string[];
  errors: FieldErrors;
  onToggle: (id: string) => void;
  onCreated: (tag: Tag) => void;
}) {
  const { t } = useI18n();
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const categories = tags.filter((tag) => tag.kind === "Category");
  const plain = tags.filter((tag) => tag.kind === "Tag");
  const tagError = Object.keys(errors).filter((key) => key === "tagIds" || key.startsWith("tagIds["));

  const add = async () => {
    const trimmed = name.trim();
    if (!trimmed || busy) return;
    setBusy(true);
    setProblem(null);
    const result = await createTag(householdId, trimmed);
    setBusy(false);
    if (result.ok) {
      setName("");
      onCreated(result.value);
    } else setProblem(result.failure === "unreachable" ? t("problem.unreachable") : t(problemKey(result.failure.problemType)));
  };

  const chips = (list: Tag[], label: string) =>
    list.length > 0 && (
      <div className="chip-group" role="group" aria-label={label}>
        {list.map((tag) => (
          <button key={tag.id} type="button" className="chip" aria-pressed={selected.includes(tag.id)} onClick={() => onToggle(tag.id)}>
            {tag.name}
          </button>
        ))}
      </div>
    );

  return (
    <section aria-labelledby="tags-heading">
      <h2 id="tags-heading">{t("recipe.tagsHeading")}</h2>
      <div id={fieldId("tagIds")} tabIndex={-1} aria-describedby={tagError[0] ? `${fieldId(tagError[0])}-error` : undefined}>
        {tagError.map((key) => (
          <ErrorText key={key} name={key} errors={errors} />
        ))}
        {chips(categories, t("recipe.tagsCategories"))}
        {chips(plain, t("recipe.tagsHeading"))}
      </div>
      <div className="tag-create">
        <div className="form-field">
          <label htmlFor="new-tag">{t("recipe.newTag")}</label>
          <input
            id="new-tag"
            value={name}
            maxLength={50}
            autoComplete="off"
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                void add();
              }
            }}
          />
        </div>
        <button type="button" className="button button-outline" disabled={busy || name.trim() === ""} onClick={() => void add()}>
          {t("recipe.addTag")}
        </button>
      </div>
      {problem && <p role="alert">{problem}</p>}
    </section>
  );
}

function DeleteRecipe({ householdId, recipe }: { householdId: string; recipe: Recipe }) {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (confirming) panelRef.current?.focus();
  }, [confirming]);

  const remove = async () => {
    setBusy(true);
    setProblem(null);
    try {
      const { response, error } = await api.DELETE("/api/households/{householdId}/recipes/{recipeId}", {
        params: { path: { householdId, recipeId: recipe.id } },
      });
      if (response.ok) {
        void navigate("/recipes", { replace: true });
        return;
      }
      // 409 recipe-in-use gets its own message; anything else the generic one for its type.
      setProblem(t(problemKey(problemTypeOf(error))));
    } catch {
      setProblem(t("problem.unreachable"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="danger-zone" aria-labelledby="delete-heading">
      <h2 id="delete-heading" className="visually-hidden">
        {t("recipe.delete")}
      </h2>
      {!confirming ? (
        <button type="button" className="button button-danger" onClick={() => setConfirming(true)}>
          {t("recipe.delete")}
        </button>
      ) : (
        <div ref={panelRef} tabIndex={-1} role="group" aria-labelledby="delete-confirm" className="delete-confirm">
          <p id="delete-confirm">{t("recipe.deleteConfirm", { title: recipe.title })}</p>
          {problem && <p role="alert">{problem}</p>}
          <div className="form-actions">
            <button type="button" className="button button-danger" disabled={busy} onClick={() => void remove()}>
              {busy ? t("recipe.deleting") : t("recipe.deleteYes")}
            </button>
            <button type="button" className="button button-outline" disabled={busy} onClick={() => setConfirming(false)}>
              {t("recipe.cancel")}
            </button>
          </div>
        </div>
      )}
    </section>
  );
}
