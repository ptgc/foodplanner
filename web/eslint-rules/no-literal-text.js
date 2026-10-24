/**
 * Local ESLint rule: user-facing text must come from the translation files (T-038, Q-013).
 * Reports, in JSX:
 *  - literal text between tags:                 <p>Hello</p>
 *  - string literals in child expressions:      <p>{"Hello"}</p>, <p>{ok ? "Yes" : "No"}</p>
 *  - literal text attributes:                   aria-label="Close", placeholder, title, alt (also as {"..."})
 * "Text" means the string contains a letter; punctuation, digits and entities (&times;) are fine.
 * Use `t("some.key")` instead; for a proper noun use a constant (APP_NAME). Exceptions need an eslint-disable
 * comment with a reason.
 */

const textAttributes = new Set(["aria-label", "aria-description", "aria-placeholder", "aria-roledescription", "placeholder", "title", "alt"]);

const hasLetter = (value) => /\p{L}/u.test(value);

/** Literal strings that would reach the screen when `node` is rendered or used as an attribute value. */
function literalsIn(node) {
  switch (node.type) {
    case "Literal":
      return typeof node.value === "string" && hasLetter(node.value) ? [node] : [];
    case "TemplateLiteral":
      return node.quasis.some((q) => hasLetter(q.value.cooked ?? "")) ? [node] : [];
    case "ConditionalExpression":
      return [...literalsIn(node.consequent), ...literalsIn(node.alternate)];
    case "LogicalExpression":
      return [...literalsIn(node.left), ...literalsIn(node.right)];
    default:
      return [];
  }
}

export const noLiteralText = {
  meta: {
    type: "problem",
    schema: [],
    messages: {
      text: "User-facing text must be translated: use t('key') and add the key to i18n/en.ts and i18n/nb.ts.",
    },
  },
  create(context) {
    const report = (node) => context.report({ node, messageId: "text" });
    return {
      JSXText(node) {
        if (hasLetter(node.value)) report(node);
      },
      JSXExpressionContainer(node) {
        if (node.parent.type === "JSXAttribute") return; // handled below, only for text attributes
        literalsIn(node.expression).forEach(report);
      },
      JSXAttribute(node) {
        if (node.name.type !== "JSXIdentifier" || !textAttributes.has(node.name.name)) return;
        const { value } = node;
        if (!value) return;
        if (value.type === "Literal") {
          if (typeof value.value === "string" && hasLetter(value.value)) report(value);
        } else if (value.type === "JSXExpressionContainer") {
          literalsIn(value.expression).forEach(report);
        }
      },
    };
  },
};

export default { rules: { "no-literal-text": noLiteralText } };
