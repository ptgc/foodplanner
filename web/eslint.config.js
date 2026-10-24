import js from "@eslint/js";
import jsxA11y from "eslint-plugin-jsx-a11y";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";
import tseslint from "typescript-eslint";
import localRules from "./eslint-rules/no-literal-text.js";

export default tseslint.config(
  { ignores: ["build/", ".react-router/", "node_modules/", "app/api/schema.d.ts"] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  jsxA11y.flatConfigs.recommended,
  reactHooks.configs.flat.recommended,
  {
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
  },
  {
    // T-038: no hard-coded user-facing text in the app; it lives in app/i18n/en.ts and nb.ts.
    files: ["app/**/*.{ts,tsx}"],
    ignores: ["app/**/*.test.{ts,tsx}"],
    plugins: { foodplanner: localRules },
    rules: { "foodplanner/no-literal-text": "error" },
  },
);
