// ChatGPT web-chat provider (service-worker side). Tab handling is shared: see ../webprovider.js.
import { createWebProvider } from '../webprovider.js';

export const chatgpt = createWebProvider({
  id: 'chatgpt',
  name: 'ChatGPT',
  origin: 'https://chatgpt.com',
  newChatUrl: 'https://chatgpt.com/',
  contentFiles: ['providers/chatgpt/selectors.js', 'providers/webchat.js', 'providers/chatgpt/content.js'],
});
