// Gemini web-chat provider. Delivery into the page comes in Phase 8 (selectors.js + content script).
export const gemini = {
  id: 'gemini',
  name: 'Gemini',
  kind: 'web',
  urlPatterns: ['https://gemini.google.com/*'],
  newChatUrl: 'https://gemini.google.com/app',

  async deliver(_text) {
    return { status: 'error', detail: 'Gemini delivery is not implemented yet (Phase 8)', provider: 'gemini' };
  },
};
