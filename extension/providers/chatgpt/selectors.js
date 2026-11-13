// ALL ChatGPT selectors live here, so they can be fixed quickly when OpenAI changes the page.
// Prefer structural selectors: the UI may be in Persian, so English aria-labels are only a last resort
// (Persian labels are listed too). Each list is tried in order; the first visible match wins.
// The options page "Test" button reports which selector of each list matched.
//
// Checked on the live page 2026-11-13 (signed in, Free plan, English UI): no #prompt-textarea and no data-testid on
// the composer buttons any more. The composer is <form data-chatgpt-composer> holding a ProseMirror editor; the
// right-most round button (class size-token-button-composer) is "Start Voice" while the box is empty,
// <button type="submit" aria-label="Send"> once it has text, and <button type="button" aria-label="Stop"> while
// ChatGPT answers. Older ids/testids are kept as fallbacks.
globalThis.CaptionRelaySelectors = globalThis.CaptionRelaySelectors || {};
globalThis.CaptionRelaySelectors.chatgpt = {
  // The prompt box: <div class="ProseMirror" contenteditable="true" role="textbox">.
  input: [
    'form[data-chatgpt-composer] div.ProseMirror[contenteditable="true"]',
    '#prompt-textarea[contenteditable="true"]',
    'div.ProseMirror[contenteditable="true"]',
    'form [contenteditable="true"][role="textbox"]',
  ],

  // The send button (shown once the box has text; the voice button sits there while it is empty).
  sendButton: [
    'form[data-chatgpt-composer] button[type="submit"]',
    'button[data-testid="send-button"]',
    'form button[aria-label*="Send"]',
    'form button[aria-label*="ارسال"]',
  ],

  // The same place while ChatGPT is generating: the round button that is neither submit nor "Start Voice".
  stopButton: [
    'form[data-chatgpt-composer] button.size-token-button-composer[type="button"]:not([data-composer-voice-start])',
    'button[data-testid="stop-button"]',
    'form button[aria-label="Stop"]',
    'form button[aria-label*="توقف"]',
  ],

  // Where to look for "the button inside the input's container closest to the box".
  inputContainer: ['form[data-chatgpt-composer]', 'form'],

  // One element per message the user sent (to verify a new one appeared).
  userTurn: ['[data-user-message-bubble="true"]', '[data-conversation-role="user"]', '[data-message-author-role="user"]'],

  // One element per answer.
  modelTurn: ['[data-conversation-role="assistant"]', '[data-message-author-role="assistant"]'],

  // Signs that the user is not signed in (ChatGPT also lets signed-out users chat; this only explains a missing box).
  signedOut: ['button[data-testid="login-button"]', 'a[href*="auth.openai.com"]'],
};
