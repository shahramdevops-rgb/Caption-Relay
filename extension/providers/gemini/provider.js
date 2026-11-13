// Gemini web-chat provider (service-worker side). Tab handling is shared: see ../webprovider.js.
import { createWebProvider } from '../webprovider.js';

export const gemini = createWebProvider({
  id: 'gemini',
  name: 'Gemini',
  origin: 'https://gemini.google.com',
  newChatUrl: 'https://gemini.google.com/app',
  contentFiles: ['providers/gemini/selectors.js', 'providers/webchat.js', 'providers/gemini/content.js'],
});
