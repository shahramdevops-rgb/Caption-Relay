// Console logging with a common prefix. In the service worker the lines are also forwarded to the app's log
// (setForwarder), so problems can be diagnosed without opening DevTools. Never log chat text.
const PREFIX = '[CaptionRelay]';
let forward = null;

export function setForwarder(fn) {
  forward = fn;
}

function emit(level, args) {
  const consoleFn = level === 'error' ? console.error : level === 'warn' ? console.warn : console.log;
  consoleFn(PREFIX, ...args);
  if (forward) {
    try {
      forward(level, args.map((a) => (typeof a === 'string' ? a : JSON.stringify(a))).join(' '));
    } catch {
      // never let logging break anything
    }
  }
}

export const log = (...args) => emit('info', args);
export const warn = (...args) => emit('warn', args);
export const error = (...args) => emit('error', args);
