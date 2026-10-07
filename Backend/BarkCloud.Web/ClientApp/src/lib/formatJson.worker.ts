import { formatJson } from './formatJson';

self.onmessage = (event: MessageEvent<string>) => {
  try { self.postMessage(formatJson(event.data)); }
  catch { self.postMessage({ error: 'Не удалось форматировать JSON. Исходник сохранён.' }); }
};
