import { useI18n } from "../i18n";
import { fieldId, type FieldErrors } from "./recipe-form";

export const errorId = (key: string) => `${fieldId(key)}-error`;

/** Props that tie an input to its error message: id (for the summary link), aria-invalid and aria-describedby. */
export function fieldProps(key: string, errors: FieldErrors) {
  const failed = key in errors;
  return {
    id: fieldId(key),
    "aria-invalid": failed ? (true as const) : undefined,
    "aria-describedby": failed ? errorId(key) : undefined,
  };
}

/** The translated message of one field, if it has an error. */
export function ErrorText({ name, errors }: { name: string; errors: FieldErrors }) {
  const { t } = useI18n();
  const error = errors[name];
  if (!error) return null;
  return (
    <p id={errorId(name)} className="field-error">
      {t(error.key, error.params)}
    </p>
  );
}

type RowControlsProps = {
  index: number;
  count: number;
  upLabel: string;
  downLabel: string;
  removeLabel: string;
  onMove: (from: number, to: number) => void;
  onRemove: () => void;
};

/**
 * Move up, move down and remove for one row, all 44px. The move buttons use aria-disabled instead of `disabled` at the
 * ends of the list, so focus stays on the button after a row has moved to the top or bottom.
 */
export function RowControls({ index, count, upLabel, downLabel, removeLabel, onMove, onRemove }: RowControlsProps) {
  const atTop = index === 0;
  const atBottom = index === count - 1;
  return (
    <div className="row-controls">
      <button
        type="button"
        className="button button-outline row-button"
        aria-label={upLabel}
        aria-disabled={atTop || undefined}
        onClick={() => !atTop && onMove(index, index - 1)}
      >
        &uarr;
      </button>
      <button
        type="button"
        className="button button-outline row-button"
        aria-label={downLabel}
        aria-disabled={atBottom || undefined}
        onClick={() => !atBottom && onMove(index, index + 1)}
      >
        &darr;
      </button>
      <button type="button" className="button button-outline row-button" aria-label={removeLabel} onClick={onRemove}>
        &times;
      </button>
    </div>
  );
}
