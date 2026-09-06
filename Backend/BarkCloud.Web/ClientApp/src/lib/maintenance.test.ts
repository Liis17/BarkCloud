import { describe, expect, it } from 'vitest';
import { maintenanceWaitPath } from './maintenance';

describe('maintenance wait routing', () => {
  it('passes the operation and the pre-restart timestamp to the wait page', () => {
    expect(maintenanceWaitPath('update', 'operation-1', '2026-09-06T21:55:23.4320929+00:00'))
      .toBe('/updating?operationId=operation-1&previousStartedAt=2026-09-06T21%3A55%3A23.4320929%2B00%3A00');
  });

  it('keeps the legacy path when no operation context is available', () => {
    expect(maintenanceWaitPath('restart')).toBe('/restarting');
  });
});
