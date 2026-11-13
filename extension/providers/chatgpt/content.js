// Content script on chatgpt.com: receives deliver/test requests from the service worker.
(() => {
  // Injected twice (manifest + scripting fallback): keep the first copy only while its extension context is alive.
  // After an extension reload the old copy is orphaned ("Extension context invalidated") and must be replaced.
  if (globalThis.__captionRelayChatGptAlive?.()) return;
  globalThis.__captionRelayChatGptAlive = () => {
    try {
      return !!chrome.runtime?.getManifest();
    } catch {
      return false;
    }
  };
  globalThis.CaptionRelayWebChat.listen(globalThis.CaptionRelaySelectors.chatgpt, 'ChatGPT');
})();
