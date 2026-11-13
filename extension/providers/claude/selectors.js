// ALL Claude (claude.ai) selectors live here, so they can be fixed quickly when Anthropic changes the page.
// claude.ai has stable data-testid attributes; English/Persian aria-labels are only a last resort.
// Each list is tried in order; the first visible match wins. The options page "Test" button reports which matched.
//
// Checked on the live page 2026-11-13 (signed in, English UI): a Tiptap/ProseMirror editor
// <div data-testid="chat-input" contenteditable="true" role="textbox"> inside <fieldset>; the send button
// <button data-testid="chat-input-send" aria-label="Send message"> is always shown (disabled while the box is empty);
// while Claude answers <button data-testid="chat-input-stop" aria-label="Stop response"> appears.
globalThis.CaptionRelaySelectors = globalThis.CaptionRelaySelectors || {};
globalThis.CaptionRelaySelectors.claude = {
  // The prompt box.
  input: [
    'div[data-testid="chat-input"][contenteditable="true"]',
    'fieldset div.ProseMirror[contenteditable="true"]',
    'div.ProseMirror[contenteditable="true"]',
    '[role="textbox"][contenteditable="true"]',
  ],

  // The send button (enabled once the box has text).
  sendButton: [
    'button[data-testid="chat-input-send"]',
    'fieldset button[aria-label*="Send"]',
    'fieldset button[aria-label*="ارسال"]',
  ],

  // Shown while Claude is generating.
  stopButton: [
    'button[data-testid="chat-input-stop"]',
    'fieldset button[aria-label*="Stop"]',
    'fieldset button[aria-label*="توقف"]',
  ],

  // Where to look for "the button inside the input's container closest to the box".
  inputContainer: ['fieldset', '[data-cds="ChatComposerDock"]', '[data-cds="ChatComposer"]'],

  // One element per message the user sent (to verify a new one appeared).
  userTurn: ['[data-testid="user-message"]'],

  // One element per answer.
  modelTurn: ['[data-testid="assistant-message"]'],

  // Signs that the user is not signed in.
  signedOut: ['a[href^="/login"]', 'button[data-testid="login-button"]'],
};
