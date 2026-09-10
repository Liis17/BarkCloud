import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ServerSettingsTab from './ServerSettingsTab';

afterEach(() => vi.unstubAllGlobals());

describe('ServerSettingsTab', () => {
  it('не выводит секреты и не раскрывает access key', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true,
      json: async () => ({
        settings: [{
          serviceId: 0,
          section: 'JwtSettings',
          key: 'SecretKey',
          value: '',
          isSensitive: true,
          hasValue: true,
          isReadOnly: false,
          valueKind: 'password',
          restartTargets: ['identity'],
          editedAt: null,
          editedBy: 'seed',
          editedFrom: 'seed',
        }, {
          serviceId: 2,
          section: 'UsersService',
          key: 'Token',
          value: '',
          isSensitive: true,
          hasValue: true,
          isReadOnly: false,
          valueKind: 'password',
          restartTargets: ['users'],
          editedAt: null,
          editedBy: 'seed',
          editedFrom: 'seed',
        }],
        reservedNames: [],
        storageProfiles: [{
          profileId: 'universal-v1',
          role: 'universal',
          version: 1,
          serviceUrl: 'http://minio:9000',
          accessKey: 'RAW_ACCESS_MUST_NOT_RENDER',
          hasAccessKey: true,
          hasSecretKey: true,
          secretKey: 'RAW_SECRET_MUST_NOT_RENDER',
          bucketName: 'cloud-universal',
          isR2: false,
          isActive: true,
          isLegacy: false,
          editedAt: null,
          editedBy: 'seed',
          editedFrom: 'seed',
        }],
        storageRevisions: [],
      }),
    })));

    render(<ServerSettingsTab />);

    expect(await screen.findByText('universal-v1')).toBeTruthy();
    expect(screen.getByPlaceholderText('Задан')).toHaveProperty('value', '');
    expect(screen.getByPlaceholderText('Оставьте пустым, чтобы не менять')).toHaveProperty('value', '');
    expect(document.body.textContent).not.toContain('RAW_SECRET_MUST_NOT_RENDER');
    expect(document.body.textContent).not.toContain('RAW_ACCESS_MUST_NOT_RENDER');
    expect(screen.queryByText('UsersService:Token')).toBeNull();
  });

  it('не предлагает вводить значение для read-only параметра', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true,
      json: async () => ({
        settings: [{
          serviceId: 2,
          section: 'UsersDb',
          key: '',
          value: '',
          isSensitive: true,
          hasValue: true,
          isReadOnly: true,
          valueKind: 'password',
          restartTargets: ['users'],
          editedAt: null,
          editedBy: 'seed',
          editedFrom: 'env',
        }],
        reservedNames: [],
        storageProfiles: [],
        storageRevisions: [],
      }),
    })));

    render(<ServerSettingsTab />);

    await screen.findByText('UsersDb');
    const input = document.querySelector('[data-setting-id="2:UsersDb:"] input') as HTMLInputElement;
    expect(input.disabled).toBe(true);
    expect(input.getAttribute('placeholder')).toBe('');
  });
});
