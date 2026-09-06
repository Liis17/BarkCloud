import fs from 'node:fs';
import vm from 'node:vm';
import { describe, expect, it, vi } from 'vitest';

function loadWaitScript(fetchMock) {
  const source = fs.readFileSync(`${process.cwd()}/../Pages/maintenance-wait.js`, 'utf8');
  const timers = [];
  const window = { location: { replace: vi.fn() } };
  const elements = {
    timer: { textContent: '' },
    'wait-error': { hidden: true, textContent: '' },
    'wait-error-message': { hidden: true, textContent: '' },
  };
  const document = {
    getElementById: (id) => elements[id] || null,
  };
  const context = {
    window,
    document,
    fetch: fetchMock,
    setInterval: vi.fn(),
    setTimeout: (callback) => {
      timers.push(callback);
      return timers.length;
    },
    clearTimeout: vi.fn(),
  };
  vm.runInNewContext(source, context);
  return { window, timers, elements };
}

const response = (body, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  headers: { get: () => '2026-09-06T21:55:23.4320929+00:00' },
  json: async () => body,
});

describe('maintenance wait page', () => {
  it('returns after a completed operation even when the page has the new server timestamp', async () => {
    const fetchMock = vi.fn((url) => Promise.resolve(url.startsWith('/maintenance-status')
      ? response({ state: 'completed' })
      : response(null)));
    const { window, timers } = loadWaitScript(fetchMock);

    window.BarkCloudWait.start({
      initialDelayMs: 0,
      operationId: '11111111-1111-1111-1111-111111111111',
      pageServerStartedAt: '2026-09-06T21:55:23.4320929+00:00',
    });

    for (let i = 0; i < 3; i++) {
      const callback = timers.shift();
      expect(callback).toBeTypeOf('function');
      callback();
      for (let flush = 0; flush < 30; flush++) {
        await Promise.resolve();
      }
    }

    expect(fetchMock).toHaveBeenCalledWith('/maintenance-status?operationId=11111111-1111-1111-1111-111111111111', expect.any(Object));
    expect(window.location.replace).toHaveBeenCalledTimes(1);
  });

  it('shows the helper error and stops polling when the operation fails', async () => {
    const fetchMock = vi.fn((url) => Promise.resolve(url.startsWith('/maintenance-status')
      ? response({ state: 'failed', message: 'Не удалось скачать новый образ' })
      : response(null)));
    const { window, timers, elements } = loadWaitScript(fetchMock);

    window.BarkCloudWait.start({ initialDelayMs: 0, operationId: '22222222-2222-2222-2222-222222222222', pageServerStartedAt: 'old' });
    const callback = timers.shift();
    callback();
    for (let flush = 0; flush < 30; flush++) await Promise.resolve();

    expect(elements['wait-error'].hidden).toBe(false);
    expect(elements['wait-error-message'].textContent).toBe('Не удалось скачать новый образ');
    expect(window.location.replace).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(timers).toHaveLength(0);
  });

  it('does not accept a different server timestamp until the tracked operation is completed', async () => {
    const fetchMock = vi.fn((url) => Promise.resolve(url.startsWith('/maintenance-status')
      ? response({ state: 'pending' })
      : response(null)));
    const { window, timers } = loadWaitScript(fetchMock);

    window.BarkCloudWait.start({
      initialDelayMs: 0,
      operationId: '33333333-3333-3333-3333-333333333333',
      pageServerStartedAt: 'old',
    });
    const callback = timers.shift();
    callback();
    for (let flush = 0; flush < 30; flush++) await Promise.resolve();

    expect(window.location.replace).not.toHaveBeenCalled();
    expect(timers).toHaveLength(1);
  });
});
