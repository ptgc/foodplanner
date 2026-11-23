import { useEffect, useId, useRef, useState, type ChangeEvent } from "react";
import { useI18n } from "../i18n";

/** One chosen photo: a stable id for the list key and the file as the browser gave it. */
export type PickedPhoto = { id: string; file: File };

let nextId = 0;
const newId = () => `photo-${++nextId}`;

/** Types the file picker offers. The camera input takes `image/*` (the phone camera decides). The server decides from the bytes. */
export const pickerAccept = "image/jpeg,image/png,image/webp";

type Props = {
  photos: PickedPhoto[];
  onChange: (photos: PickedPhoto[]) => void;
  /** At most this many photos (the recipe photo import uses 4). */
  max: number;
  /** True while a request runs: nothing can be added or removed. */
  disabled?: boolean;
};

/**
 * Camera and file input with thumbnails (T-119). Reusable by every "photos in" screen (T-132): it holds no import
 * logic, only the list of chosen files. The camera button opens the phone camera (`capture="environment"`); the other
 * opens the photo library or files and allows several. Photos over `max` are not added and a message says so.
 */
export function PhotoPicker({ photos, onChange, max, disabled = false }: Props) {
  const { t } = useI18n();
  const base = useId();
  const cameraRef = useRef<HTMLInputElement>(null);
  const filesRef = useRef<HTMLInputElement>(null);
  const [tooMany, setTooMany] = useState(false);
  const full = photos.length >= max;

  const add = (event: ChangeEvent<HTMLInputElement>) => {
    const chosen = [...(event.target.files ?? [])];
    event.target.value = ""; // the same file can be chosen again after a removal
    if (chosen.length === 0) return;
    const room = Math.max(0, max - photos.length);
    setTooMany(chosen.length > room);
    if (room > 0) onChange([...photos, ...chosen.slice(0, room).map((file) => ({ id: newId(), file }))]);
  };

  const remove = (id: string) => {
    setTooMany(false);
    const index = photos.findIndex((p) => p.id === id);
    onChange(photos.filter((p) => p.id !== id));
    // Keep the keyboard in the list: the next remove button, else the previous one, else the camera button.
    setTimeout(() => {
      const buttons = document.querySelectorAll<HTMLButtonElement>(`[data-picker="${base}"] .photo-remove`);
      (buttons[Math.min(index, buttons.length - 1)] ?? cameraRef.current?.parentElement?.querySelector<HTMLButtonElement>(".photo-camera"))?.focus();
    }, 0);
  };

  return (
    <div className="photo-picker" role="group" aria-labelledby={`${base}-heading`} data-picker={base}>
      <h2 id={`${base}-heading`} className="photo-picker-heading">
        {t("photo.picker.heading", { n: photos.length, max })}
      </h2>
      <div className="photo-picker-actions">
        <button type="button" className="button photo-camera" disabled={disabled || full} onClick={() => cameraRef.current?.click()}>
          {t("photo.picker.camera")}
        </button>
        <button type="button" className="button button-outline photo-files" disabled={disabled || full} onClick={() => filesRef.current?.click()}>
          {t("photo.picker.files")}
        </button>
        {/* The real inputs stay in the page (hidden, not display:none) so the buttons above open them and tests can fill them. */}
        <input
          ref={cameraRef}
          type="file"
          accept="image/*"
          capture="environment"
          className="visually-hidden"
          tabIndex={-1}
          aria-label={t("photo.picker.cameraInput")}
          data-testid="photo-camera-input"
          disabled={disabled || full}
          onChange={add}
        />
        <input
          ref={filesRef}
          type="file"
          accept={pickerAccept}
          multiple
          className="visually-hidden"
          tabIndex={-1}
          aria-label={t("photo.picker.filesInput")}
          data-testid="photo-files-input"
          disabled={disabled || full}
          onChange={add}
        />
      </div>
      {tooMany && (
        <p role="alert" className="field-error" data-testid="photo-too-many">
          {t("photo.picker.tooMany", { max })}
        </p>
      )}
      {photos.length === 0 ? (
        <p className="photo-picker-empty">{t("photo.picker.empty", { max })}</p>
      ) : (
        <ul className="photo-thumbs">
          {photos.map((photo, index) => (
            <li key={photo.id} className="photo-thumb" data-testid="photo-thumb">
              <Thumb file={photo.file} label={t("photo.picker.thumbAlt", { n: index + 1, name: photo.file.name })} />
              <button
                type="button"
                className="photo-remove"
                aria-label={t("photo.picker.remove", { n: index + 1 })}
                disabled={disabled}
                onClick={() => remove(photo.id)}
              >
                <span aria-hidden="true">&times;</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** A preview from the file in memory (an object URL, revoked when the thumbnail goes away). */
function Thumb({ file, label }: { file: File; label: string }) {
  const [src, setSrc] = useState<string | null>(null);
  useEffect(() => {
    if (typeof URL.createObjectURL !== "function") return;
    const url = URL.createObjectURL(file);
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the object URL is an external resource that must be created and revoked with the effect
    setSrc(url);
    return () => URL.revokeObjectURL(url);
  }, [file]);
  return src ? <img src={src} alt={label} /> : <span className="photo-thumb-fallback" role="img" aria-label={label} />;
}
