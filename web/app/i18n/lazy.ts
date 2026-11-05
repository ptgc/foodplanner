import { lazy, type ComponentType } from "react";
import { loadMessages, type MessageGroup } from "./index";

/**
 * React.lazy for a screen that needs an optional message group (T-106): the component's code and the active locale's
 * messages of the group arrive together, so the screen never renders with untranslated keys. A later language switch
 * loads the group for the new locale too (see loadMessages).
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any -- same bound as React.lazy
export function lazyWithMessages<T extends ComponentType<any>>(group: MessageGroup, load: () => Promise<{ default: T }>) {
  return lazy(() => Promise.all([load(), loadMessages(group)]).then(([module]) => module));
}
