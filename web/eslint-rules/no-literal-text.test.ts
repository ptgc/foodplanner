import { RuleTester } from "eslint";
import tseslint from "typescript-eslint";
import { describe, it } from "vitest";
import { noLiteralText } from "./no-literal-text.js";

RuleTester.describe = describe;
RuleTester.it = it;

const tester = new RuleTester({
  languageOptions: {
    parser: tseslint.parser,
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
});

const error = [{ messageId: "text" as const }];

tester.run("no-literal-text", noLiteralText, {
  valid: [
    { code: 'const a = <p>{t("shopping.title")}</p>;' },
    { code: 'const a = <button aria-label={t("x.close")} title={label}>{"×"}</button>;' },
    { code: "const a = <span aria-hidden>&times; 12 : 3</span>;" },
    { code: 'const a = <input type="email" name="email" autoComplete="username" className="field" />;' },
    { code: 'const a = <p>{ok ? t("a.yes") : t("a.no")}</p>;' },
    { code: 'const a = <p>{APP_NAME}</p>; const b = "plain strings outside JSX are fine";' },
  ],
  invalid: [
    { code: "const a = <p>Hello</p>;", errors: error },
    { code: 'const a = <p>{"Hello"}</p>;', errors: error },
    { code: "const a = <p>{`Hello ${name}`}</p>;", errors: error },
    { code: 'const a = <p>{ok ? "Yes" : t("a.no")}</p>;', errors: error },
    { code: 'const a = <p>{ok && "Saved"}</p>;', errors: error },
    { code: 'const a = <button aria-label="Close" />;', errors: error },
    { code: 'const a = <input placeholder="Search" />;', errors: error },
    { code: 'const a = <img alt="A cake" src={s} />;', errors: error },
    { code: 'const a = <a title={"Tip"}>{t("a.b")}</a>;', errors: error },
  ],
});
