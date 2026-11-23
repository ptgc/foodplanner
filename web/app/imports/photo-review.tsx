import { useId } from "react";
import { useI18n } from "../i18n";
import { maxSourceName } from "../photo/photo-api";

type Props = {
  sourceName: string;
  onSourceName: (value: string) => void;
  /** The cropped dish picture Claude found (the url of `GET imports/{id}/picture`), or null when there is none. */
  pictureUrl: string | null;
  useSuggestedImage: boolean;
  onUseSuggestedImage: (use: boolean) => void;
  busy: boolean;
};

/**
 * The parts of the import review that only a photo import has (T-119): the AI note, the source book and the suggested dish
 * picture with its switch. Lazy (with the "photo" messages) so a URL or text import never loads it.
 */
export default function PhotoReviewExtras({ sourceName, onSourceName, pictureUrl, useSuggestedImage, onUseSuggestedImage, busy }: Props) {
  const { t } = useI18n();
  const base = useId();
  return (
    <div className="photo-review">
      <div className="import-ai-note" data-testid="ai-note" data-status="Applied">
        <p>
          <span className="import-ai-badge" aria-hidden="true">
            {t("import.aiBadge")}
          </span>{" "}
          {t("photo.review.note")}
        </p>
        <p className="import-ai-privacy">{t("photo.privacy")}</p>
      </div>
      <div className="form-field">
        <label htmlFor={`${base}-book`}>{t("photo.review.sourceName")}</label>
        <input
          id={`${base}-book`}
          type="text"
          autoComplete="off"
          maxLength={maxSourceName}
          value={sourceName}
          aria-describedby={`${base}-book-hint`}
          data-testid="photo-source-name"
          onChange={(e) => onSourceName(e.target.value)}
        />
        <p id={`${base}-book-hint`} className="photo-hint">
          {t("photo.review.sourceNameHint")}
        </p>
      </div>
      {pictureUrl && (
        <section aria-labelledby={`${base}-picture`} className="photo-review-picture" data-testid="photo-suggested">
          <h2 id={`${base}-picture`}>{t("photo.review.pictureHeading")}</h2>
          <img src={pictureUrl} alt={t("photo.review.pictureAlt")} />
          <label className="photo-switch">
            <input
              type="checkbox"
              role="switch"
              checked={useSuggestedImage}
              disabled={busy}
              onChange={(e) => onUseSuggestedImage(e.target.checked)}
            />
            <span>{t("photo.review.useSwitch")}</span>
          </label>
        </section>
      )}
    </div>
  );
}
