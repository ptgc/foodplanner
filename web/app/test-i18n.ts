import { formatNumber, getLocale, translate, type MessageKey, type Params } from "./i18n";

/**
 * For tests: look texts up in the active test locale (vitest runs every suite under en and nb, see
 * vitest.config.ts), so assertions do not hard-code a language.
 */
export function t(key: MessageKey, params?: Params): string {
  return translate(getLocale(), key, params);
}

/** A number as the active test locale writes it (1.5 in en, 1,5 in nb). */
export function n(value: number): string {
  return formatNumber(getLocale(), value, { maximumFractionDigits: 2 });
}
