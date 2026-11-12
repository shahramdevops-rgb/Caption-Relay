// ALL Gemini selectors live here, so they can be fixed quickly when Google changes the page.
// Prefer structural/class selectors: the UI may be in Persian, so English aria-labels are only a last resort
// (Persian labels are listed too). Each list is tried in order; the first visible match wins.
// The options page "Test" button reports which selector of each list matched.
globalThis.CaptionRelaySelectors = globalThis.CaptionRelaySelectors || {};
globalThis.CaptionRelaySelectors.gemini = {
  // The prompt box (a Quill editor inside <rich-textarea>).
  input: [
    'rich-textarea div.ql-editor[contenteditable="true"]',
    'rich-textarea div[contenteditable="true"]',
    'div.ql-editor[contenteditable="true"]',
    '[role="textbox"][contenteditable="true"]',
  ],

  // The send button (shown once the box has text). Checked on the live page 2026-11-12:
  // <gem-icon-button class="send-button has-input submit"><button aria-label="Send message">.
  sendButton: [
    'gem-icon-button.send-button:not(.stop) button',
    'gem-icon-button.submit button',
    'button.send-button:not(.stop)',
    'button[data-test-id="send-button"]',
    'button[aria-label*="Send"]',
    'button[aria-label*="ارسال"]',
  ],

  // The same place while Gemini is generating (appears ~0.1 s after sending):
  // <gem-icon-button class="send-button stop"><button aria-label="Stop response">.
  stopButton: [
    'gem-icon-button.send-button.stop button',
    'gem-icon-button.stop button',
    'button.send-button.stop',
    'button[data-test-id="stop-button"]',
    'button[aria-label*="Stop"]',
    'button[aria-label*="توقف"]',
  ],

  // Where to look for "the button inside the input's container closest to the box".
  inputContainer: ['input-area-v2', '.input-area-container', '.input-area', 'fieldset', 'form'],

  // One element per message the user sent (to verify a new one appeared).
  userTurn: ['user-query', '[data-test-id="user-query"]', '.user-query-container'],

  // One element per answer.
  modelTurn: ['model-response', '[data-test-id="model-response"]'],

  // Signs that the user is not signed in.
  signedOut: ['a[href*="accounts.google.com/ServiceLogin"]', 'a[href*="accounts.google.com/v3/signin"]', 'a[href*="accounts.google.com/signin"]'],
};
