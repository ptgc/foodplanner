import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { useI18n } from "../i18n";
import { downsizePhoto, maxPhotoBytes } from "./downsize";
import {
  createPhotoImport,
  maxPhotos,
  maxSourceName,
  photoFailureMessage,
  photoNumber,
  waitKey,
  type PhotoFailure,
  type PhotoImportDraft,
} from "./photo-api";
import { PhotoPicker, type PickedPhoto } from "./photo-picker";
import { usePhotoUsage } from "./use-photo-usage";

type Phase = "idle" | "preparing" | "reading";
type Problem = { text: string; textFallback: boolean };

/**
 * `/recipes/import/photo` (T-119): choose 1 to 4 photos of one recipe, send them, and land in the import review.
 * Shows what is left of today's photo imports, the privacy line before anything is sent, progress with a cancel, and a
 * translated message for every problem the API can answer with. The photos stay chosen after an error.
 */
export function PhotoImportScreen({ householdId, onCreated }: { householdId: string; onCreated: (draft: PhotoImportDraft) => void }) {
  const { t } = useI18n();
  const { state, retry, update } = usePhotoUsage(householdId, true);
  const [photos, setPhotos] = useState<PickedPhoto[]>([]);
  const [sourceName, setSourceName] = useState("");
  const [phase, setPhase] = useState<Phase>("idle");
  const [problem, setProblem] = useState<Problem | null>(null);
  const [cancelled, setCancelled] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const running = useRef(false);
  const alertRef = useRef<HTMLDivElement>(null);
  const base = useId();
  const busy = phase !== "idle";

  // Leaving the page while a request runs aborts it, so nothing is left running in the background.
  useEffect(() => () => controller.current?.abort(), []);
  useEffect(() => {
    if (problem) alertRef.current?.focus();
  }, [problem]);

  const unlimited = state.status === "ok" && state.usage.unlimited;
  const remaining = state.status === "ok" ? photoNumber(state.usage.remainingToday) : null;
  const dailyLimit = state.status === "ok" ? photoNumber(state.usage.dailyLimit) : null;
  const limitReached = state.status === "ok" && state.usage.enabled && !unlimited && remaining !== null && remaining <= 0;

  const show = (failure: PhotoFailure) => {
    const message = photoFailureMessage(failure);
    const params = message.waitSeconds !== undefined ? { wait: waitText(message.waitSeconds) } : message.params;
    setProblem({ text: t(message.key, params), textFallback: message.textFallback });
    if (failure.kind === "problem" && failure.remainingToday !== null) update(failure.remainingToday);
  };
  const waitText = (seconds: number) => {
    const w = waitKey(seconds);
    return t(w.key, w.params);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (running.current || photos.length === 0 || limitReached) return;
    running.current = true;
    const abort = new AbortController();
    controller.current = abort;
    setProblem(null);
    setCancelled(false);
    setPhase("preparing");
    try {
      const prepared: File[] = [];
      for (const photo of photos) {
        const file = await downsizePhoto(photo.file);
        if (abort.signal.aborted) throw new DOMException("cancelled", "AbortError");
        if (!file) {
          setProblem({ text: t("photo.picker.fileTooLarge", { name: photo.file.name, max: maxPhotoBytes / (1024 * 1024) }), textFallback: false });
          return;
        }
        prepared.push(file);
      }
      setPhase("reading");
      const result = await createPhotoImport(householdId, prepared, sourceName.trim(), abort.signal);
      if (result.ok) {
        const left = result.draft.photoUsage;
        if (left) update(photoNumber(left.remainingToday), left.unlimited);
        onCreated(result.draft);
        return;
      }
      show(result.failure);
    } catch {
      if (abort.signal.aborted) setCancelled(true); // an abort is not a failure: say so and keep the photos
      else show({ kind: "unreachable" });
    } finally {
      running.current = false;
      controller.current = null;
      setPhase("idle");
    }
  };

  const noPhotoHint = `${base}-hint`;
  const usageReady = state.status === "ok" && state.usage.enabled;

  return (
    <section aria-labelledby="photo-title" className="photo-import">
      <h1 id="photo-title">{t("photo.title")}</h1>
      <p>{t("photo.intro")}</p>

      {state.status === "loading" && <p role="status">{t("photo.usageLoading")}</p>}
      {state.status === "error" && (
        <div role="alert" className="import-problem">
          <p>{t("photo.usageFailed")}</p>
          <button type="button" className="button" onClick={retry}>
            {t("photo.retry")}
          </button>
        </div>
      )}
      {state.status === "ok" && !state.usage.enabled && (
        <div role="alert" className="import-problem" data-testid="photo-unavailable">
          <p>{t("photo.unavailable")}</p>
          <Link to="/recipes/import" className="button button-outline">
            {t("photo.textInstead")}
          </Link>
        </div>
      )}

      {usageReady && (
        <form noValidate className="recipe-form photo-form" aria-busy={busy} onSubmit={(e) => void submit(e)}>
          {!unlimited && remaining !== null && dailyLimit !== null && (
            <p className="photo-left" data-testid="photo-left">
              {t("photo.left", { remaining, limit: dailyLimit })}
            </p>
          )}
          {limitReached && (
            <p className="import-problem" data-testid="photo-limit">
              {t("photo.limitNone")}{" "}
              <Link to="/recipes/import">{t("photo.textInstead")}</Link>
            </p>
          )}

          <PhotoPicker photos={photos} onChange={setPhotos} max={maxPhotos} disabled={busy} />

          <div className="form-field">
            <label htmlFor={`${base}-book`}>{t("photo.sourceName")}</label>
            <input
              id={`${base}-book`}
              type="text"
              autoComplete="off"
              maxLength={maxSourceName}
              value={sourceName}
              disabled={busy}
              aria-describedby={`${base}-book-hint`}
              onChange={(e) => setSourceName(e.target.value)}
            />
            <p id={`${base}-book-hint`} className="photo-hint">
              {t("photo.sourceNameHint")}
            </p>
          </div>

          <p className="import-ai-privacy" data-testid="photo-privacy">
            {t("photo.privacy")}
          </p>

          {phase !== "idle" && (
            <p role="status" data-testid="photo-progress">
              {phase === "preparing" ? t("photo.preparing") : t("photo.reading")}
            </p>
          )}
          {cancelled && (
            <p role="status" data-testid="photo-cancelled">
              {t("photo.cancelled")}
            </p>
          )}
          {problem && (
            <div ref={alertRef} tabIndex={-1} role="alert" className="import-problem" data-testid="photo-problem">
              <p>{problem.text}</p>
              {problem.textFallback && (
                <Link to="/recipes/import" className="button button-outline">
                  {t("photo.textInstead")}
                </Link>
              )}
            </div>
          )}

          <div className="form-actions form-submit">
            <button type="submit" className="button" disabled={busy || photos.length === 0 || limitReached} aria-describedby={photos.length === 0 ? noPhotoHint : undefined}>
              {t("photo.send")}
            </button>
            {busy && (
              <button type="button" className="button button-outline" onClick={() => controller.current?.abort()}>
                {t("photo.cancel")}
              </button>
            )}
          </div>
          {photos.length === 0 && (
            <p id={noPhotoHint} className="photo-hint">
              {t("photo.sendNeedsPhoto")}
            </p>
          )}
        </form>
      )}
    </section>
  );
}

export default PhotoImportScreen;
