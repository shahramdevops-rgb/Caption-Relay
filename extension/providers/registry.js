// All providers the extension can deliver to. The Windows app and the protocol do not know about them
// (see ROADMAP.md, "provider abstraction"). Add ChatGPT / Claude / API providers here later.
import { gemini } from './gemini/provider.js';

export const PROVIDERS = { gemini };
export const DEFAULT_PROVIDER = 'gemini';

export function getProvider(id) {
  return PROVIDERS[id] ?? PROVIDERS[DEFAULT_PROVIDER];
}

export function listProviders() {
  return Object.values(PROVIDERS).map((p) => ({ id: p.id, name: p.name }));
}
