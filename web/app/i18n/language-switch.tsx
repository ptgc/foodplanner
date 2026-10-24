import { locales, setLocale, useI18n, type Locale } from "./index";

/** Language picker for the shell and the login page. The choice is remembered on this device. */
export function LanguageSwitch() {
  const { locale, t } = useI18n();
  return (
    <>
      <label htmlFor="language" className="visually-hidden">
        {t("common.language")}
      </label>
      <select
        id="language"
        className="language-switch"
        value={locale}
        onChange={(e) => setLocale(e.target.value as Locale)}
      >
        {locales.map((l) => (
          <option key={l} value={l} lang={l}>
            {t(`language.${l}`)}
          </option>
        ))}
      </select>
    </>
  );
}
