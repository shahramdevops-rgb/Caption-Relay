// Content script on claude.ai: receives deliver/test requests from the service worker.
(() => {
  // Injected twice (manifest + scripting fallback): keep the first copy only while its extension context is alive.
  // After an extension reload the old copy is orphaned ("Extension context invalidated") and must be replaced.
  if (globalThis.__captionRelayClaudeAlive?.()) return;
  globalThis.__captionRelayClaudeAlive = () => {
    try {
      return !!chrome.runtime?.getManifest();
    } catch {
      return false;
    }
  };
  globalThis.CaptionRelayWebChat.listen(globalThis.CaptionRelaySelectors.claude, 'Claude');
})();
