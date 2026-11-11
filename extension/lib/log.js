// Console logging with a common prefix (service worker, options page, content scripts).
const PREFIX = '[CaptionRelay]';

export const log = (...args) => console.log(PREFIX, ...args);
export const warn = (...args) => console.warn(PREFIX, ...args);
export const error = (...args) => console.error(PREFIX, ...args);
