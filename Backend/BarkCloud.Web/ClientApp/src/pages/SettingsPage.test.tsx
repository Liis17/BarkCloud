import { render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SettingsPage } from './SettingsPage';

afterEach(() => vi.unstubAllGlobals());

describe('SettingsPage', () => {
  it('показывает оригинал аватара в настройках, а не его превью', async () => {
    const avatarUrl = 'https://files.example/avatar-original';
    const avatarPreviewUrl = 'https://files.example/avatar-preview';

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true,
      status: 200,
      json: async () => ({
        profile: {
          initials: 'TU',
          firstName: 'Test',
          lastName: 'User',
          name: 'Test User',
          email: 'test@example.com',
          username: 'test',
          bio: '',
          avatarUrl,
          avatarPreviewUrl,
        },
        security: { twoFa: false, authenticator: false, emailOtp: false },
        privacy: { profileVisibility: 0, emailVisibility: 0, lastSeenVisibility: 0, searchableByUsername: true },
        storage: {
          used: 0,
          total: 1,
          unit: 'B',
          percent: 0,
          forecast: '',
          breakdown: [],
          freeLabel: '',
          autoUpload: false,
          devicesCount: '',
          trashLabel: '',
          disk: {
            totalLabel: '',
            usedLabel: '',
            otherLabel: '',
            s3Label: '',
            freeLabel: '',
            usedPct: 0,
            otherPct: 0,
            s3Pct: 0,
          },
        },
        sessions: [],
        sessionsHeader: '',
        admin: { enabled: false, unlocked: false },
        system: { version: '', edition: '', emailEnabled: true, registrationEnabled: true },
      }),
    })));

    render(<SettingsPage />);

    await waitFor(() => {
      expect(document.querySelector('.avatar-big img')?.getAttribute('src')).toBe(avatarUrl);
    });
  });
});
