import { useState } from "react";

/** Pixel widths of the stored sizes (T-022: thumb 160, card 640). The intrinsic size keeps the layout from shifting. */
export const thumbWidth = 160;
export const cardWidth = 640;
export const fullWidth = 1600;
const aspect = { width: 4, height: 3 };

// Friendly, distinct tile colours (dark text on light tints; the initial is decorative and large).
const palette = ["#ffd9c7", "#ffe8a3", "#cdeccb", "#c9e4f6", "#e3d4f5", "#ffd3e2"] as const;

/** Same title, same colour, so a card does not change colour between visits. */
export function tileColor(title: string): string {
  let hash = 0;
  for (const ch of title) hash = (hash * 31 + (ch.codePointAt(0) ?? 0)) >>> 0;
  return palette[hash % palette.length]!;
}

export function initialOf(title: string): string {
  const first = [...title.trim()][0];
  return first ? first.toLocaleUpperCase() : "?";
}

export type RecipeImageProps = {
  title: string;
  /** Small and card sized image urls (T-022). Without a card url a friendly placeholder tile is shown. */
  thumbUrl?: string | null;
  cardUrl?: string | null;
  /** Full size image (1600px), offered to large and high-density screens on the detail page. */
  fullUrl?: string | null;
  /** `sizes` hint for the responsive image; default is the two-column mobile grid. */
  sizes?: string;
};

/**
 * The picture of a recipe card. This is the only place that knows about image urls: with real urls it renders a
 * lazy, async-decoded, size-reserved `<img>` with a thumb/card `srcset`; without them (or if loading fails) a
 * coloured tile with the recipe's initial. Always decorative (the title is next to it), so alt is empty.
 */
export function RecipeImage({ title, thumbUrl, cardUrl, fullUrl, sizes = "(min-width: 40rem) 14rem, 50vw" }: RecipeImageProps) {
  const [failed, setFailed] = useState<string | null>(null);
  const src = cardUrl ?? thumbUrl ?? null;

  if (!src || failed === src) {
    return (
      <div className="recipe-image recipe-image-placeholder" style={{ backgroundColor: tileColor(title) }} aria-hidden="true">
        <span>{initialOf(title)}</span>
      </div>
    );
  }

  const candidates = [
    thumbUrl && `${thumbUrl} ${thumbWidth}w`,
    cardUrl && `${cardUrl} ${cardWidth}w`,
    fullUrl && cardUrl && `${fullUrl} ${fullWidth}w`,
  ].filter(Boolean);
  const srcSet = thumbUrl && cardUrl ? candidates.join(", ") : undefined;

  return (
    <div className="recipe-image">
      <img
        src={src}
        srcSet={srcSet}
        sizes={srcSet ? sizes : undefined}
        width={cardWidth}
        height={(cardWidth * aspect.height) / aspect.width}
        loading="lazy"
        decoding="async"
        alt=""
        onError={() => setFailed(src)}
      />
    </div>
  );
}
