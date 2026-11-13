// Claude (claude.ai) web-chat provider (service-worker side). Tab handling is shared: see ../webprovider.js.
import { createWebProvider } from '../webprovider.js';

export const claude = createWebProvider({
  id: 'claude',
  name: 'Claude',
  origin: 'https://claude.ai',
  newChatUrl: 'https://claude.ai/new',
  contentFiles: ['providers/claude/selectors.js', 'providers/webchat.js', 'providers/claude/content.js'],
});
