import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SettingsPage } from './SettingsPage';
import { jsonResponse, profile, serverSettings, system } from '../test/settingsFixtures';

vi.mock('../lib/webauthn', () => ({
  webauthnAuthenticate: vi.fn(),
  webauthnRegister: vi.fn(),
  webauthnSupported: () => true,
}));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.history.replaceState({}, '', '/settings');
});

function mockSettings(admin = { enabled: false, unlocked: false }) {
  const fetchMock = vi.fn((path: string, _init?: RequestInit) => {
    const data: Record<string, unknown> = {
      '/api/settings/context': { admin }, '/api/settings/profile': profile,
      '/api/settings/server': serverSettings, '/api/settings/system': system,
      '/api/settings/migration/sources': [], '/api/settings/migration/jobs': [], '/api/settings/migration/cutovers': [],
      '/api/settings/privacy': { profileVisibility: 0, emailVisibility: 0, lastSeenVisibility: 0, searchableByUsername: true },
      '/api/system/jobs': { jobs: [] }, '/api/system/branches': { services: [] },
      '/api/system/services?includeVersions=false': { dockerOk: true, services: [] },
      '/api/system/services': { dockerOk: true, services: [] },
    };
    return Promise.resolve(jsonResponse(data[path] ?? {}));
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('SettingsPage', () => {
  it('оставляет меню и поиск доступными при медленной загрузке и отменяет ненужный запрос', async () => {
    const fetchMock = vi.fn((_path: string, _init?: RequestInit) => new Promise<Response>(() => {}));
    vi.stubGlobal('fetch', fetchMock);
    render(<SettingsPage />);
    expect(screen.getByRole('button', { name: 'Аккаунт' })).toBeTruthy();
    expect(screen.getByLabelText('Поиск по настройкам')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Внешний вид' }));
    expect(screen.getByRole('button', { name: 'Светлая' })).toBeTruthy();
    expect(fetchMock.mock.calls.find(([path]) => path === '/api/settings/profile')?.[1]?.signal?.aborted).toBe(true);
    expect(fetchMock.mock.calls.some(([path]) => path === '/api/settings/full')).toBe(false);
  });

  it('показывает оригинал аватара и загружает только выбранный раздел', async () => {
    const fetchMock = mockSettings();
    render(<SettingsPage />);
    await waitFor(() => expect(document.querySelector('.avatar-big img')?.getAttribute('src')).toBe(profile.avatarUrl));
    expect(fetchMock.mock.calls.map(([path]) => path).sort()).toEqual(['/api/settings/context', '/api/settings/profile']);
  });

  it('запрашивает reauth перед добавлением passkey и показывает обязательный второй фактор', async () => {
    const fetchMock = mockSettings();
    fetchMock.mockImplementation((path) => {
      if (path === '/api/settings/security/reauth/begin')
        return Promise.resolve(jsonResponse({ message: 'Введите код приложения-аутентификатора', otpMethod: 'authenticator' }, 400));
      const data: Record<string, unknown> = {
        '/api/settings/context': { admin: { enabled: false, unlocked: false } },
        '/api/settings/profile': profile,
        '/api/settings/security/2fa': { authenticator: true, email: false },
        '/api/settings/security/webauthn': { keys: [{ id: 'key-1', name: 'Ключ', createdAt: null, lastUsedAt: null }] },
      };
      return Promise.resolve(jsonResponse(data[path] ?? {}));
    });
    render(<SettingsPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Безопасность' }));
    await screen.findByText('Ключи безопасности');
    fireEvent.click(screen.getByRole('button', { name: 'Добавить ключ' }));
    fireEvent.change(await screen.findByPlaceholderText('Пароль'), { target: { value: 'current-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить паролем' }));

    const otpCode = await screen.findByLabelText('Код из приложения-аутентификатора') as HTMLInputElement;
    expect(otpCode.getAttribute('inputmode')).toBe('numeric');
    expect(otpCode.getAttribute('autocomplete')).toBe('one-time-code');
    const beginCall = fetchMock.mock.calls.find(([path]) => path === '/api/settings/security/reauth/begin');
    expect(JSON.parse(beginCall?.[1]?.body as string)).toMatchObject({ action: 1, password: 'current-password' });
    expect(fetchMock.mock.calls.some(([path]) => path === '/api/settings/security/webauthn/register/begin')).toBe(false);
  });

  it('сохраняет email-код reauth как одноразовый числовой ввод', async () => {
    const fetchMock = mockSettings();
    let beginCount = 0;
    fetchMock.mockImplementation((path) => {
      if (path === '/api/settings/security/reauth/begin') {
        beginCount++;
        return Promise.resolve(beginCount === 1
          ? jsonResponse({ message: 'Введите код из письма', otpMethod: 'email' }, 400)
          : jsonResponse({ challengeId: 'email-challenge' }));
      }
      const data: Record<string, unknown> = {
        '/api/settings/context': { admin: { enabled: false, unlocked: false } },
        '/api/settings/profile': profile,
        '/api/settings/security/2fa': { authenticator: false, email: false },
        '/api/settings/security/webauthn': { keys: [{ id: 'key-1', name: 'Ключ', createdAt: null, lastUsedAt: null }] },
      };
      return Promise.resolve(jsonResponse(data[path] ?? {}));
    });
    render(<SettingsPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Безопасность' }));
    await screen.findByText('Ключи безопасности');
    fireEvent.click(screen.getByRole('button', { name: 'Добавить ключ' }));
    fireEvent.change(await screen.findByLabelText('Пароль'), { target: { value: 'current-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить паролем' }));
    await waitFor(() => expect(beginCount).toBe(1));
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить паролем' }));

    const emailCode = await screen.findByLabelText('Код из письма') as HTMLInputElement;
    expect(emailCode.getAttribute('inputmode')).toBe('numeric');
    expect(emailCode.getAttribute('autocomplete')).toBe('one-time-code');
  });

  it('сохраняет autofocus и Enter для кода включения 2FA', async () => {
    const fetchMock = mockSettings();
    fetchMock.mockImplementation((path) => {
      if (path === '/api/settings/security/2fa') return Promise.resolve(jsonResponse({ authenticator: false, email: false }));
      if (path === '/api/settings/security/2fa/enable') return Promise.resolve(jsonResponse({ qr: '', code: '' }));
      if (path === '/api/settings/security/2fa/confirm') return Promise.resolve(jsonResponse({ message: 'Код неверен' }, 400));
      const data: Record<string, unknown> = {
        '/api/settings/context': { admin: { enabled: false, unlocked: false } },
        '/api/settings/profile': profile,
        '/api/settings/security/webauthn': { keys: [] },
      };
      return Promise.resolve(jsonResponse(data[path] ?? {}));
    });
    render(<SettingsPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Безопасность' }));
    fireEvent.click(await screen.findByRole('switch', { name: 'Приложение-аутентификатор' }));
    fireEvent.change(await screen.findByLabelText('Пароль'), { target: { value: 'current-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Продолжить' }));

    const code = await screen.findByLabelText('Код подтверждения') as HTMLInputElement;
    await waitFor(() => expect(document.activeElement).toBe(code));
    fireEvent.change(code, { target: { value: '123456' } });
    fireEvent.keyDown(code, { key: 'Enter' });
    await waitFor(() => expect(fetchMock.mock.calls.some(([path]) => path === '/api/settings/security/2fa/confirm')).toBe(true));
    const confirmCall = fetchMock.mock.calls.find(([path]) => path === '/api/settings/security/2fa/confirm');
    expect(JSON.parse(confirmCall?.[1]?.body as string)).toEqual({ otpCode: '123456' });
  });

  it('сохраняет autofocus и Enter для кода отключения 2FA', async () => {
    const fetchMock = mockSettings();
    fetchMock.mockImplementation((path) => {
      if (path === '/api/settings/security/2fa') return Promise.resolve(jsonResponse({ authenticator: true, email: false }));
      if (path === '/api/settings/security/2fa/disable') return Promise.resolve(jsonResponse({ message: 'Код неверен' }, 400));
      const data: Record<string, unknown> = {
        '/api/settings/context': { admin: { enabled: false, unlocked: false } },
        '/api/settings/profile': profile,
        '/api/settings/security/webauthn': { keys: [] },
      };
      return Promise.resolve(jsonResponse(data[path] ?? {}));
    });
    render(<SettingsPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Безопасность' }));
    fireEvent.click(await screen.findByRole('switch', { name: 'Приложение-аутентификатор' }));

    const code = await screen.findByLabelText('Код') as HTMLInputElement;
    await waitFor(() => expect(document.activeElement).toBe(code));
    fireEvent.change(code, { target: { value: '654321' } });
    fireEvent.keyDown(code, { key: 'Enter' });
    await waitFor(() => expect(fetchMock.mock.calls.some(([path]) => path === '/api/settings/security/2fa/disable')).toBe(true));
    const disableCall = fetchMock.mock.calls.find(([path]) => path === '/api/settings/security/2fa/disable');
    expect(JSON.parse(disableCall?.[1]?.body as string)).toEqual({ otpType: 1, otpCode: '654321' });
  });

  it('общий поиск открывает пункт и переводит к нему фокус', async () => {
    mockSettings();
    render(<SettingsPage />);
    fireEvent.change(screen.getByLabelText('Поиск по настройкам'), { target: { value: 'светлая' } });
    fireEvent.click(screen.getByRole('button', { name: /Тема.*Внешний вид/ }));
    await waitFor(() => expect(document.activeElement?.getAttribute('data-setting-label')).toBe('Тема'));
    expect(window.location.hash).toBe('#appearance');
  });

  it('не выдаёт админские результаты обычному пользователю', async () => {
    mockSettings();
    render(<SettingsPage />);
    await screen.findByDisplayValue(profile.email);
    fireEvent.change(screen.getByLabelText('Поиск по настройкам'), { target: { value: 'endpoint' } });
    expect(screen.getByText('Ничего не найдено')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Настройки сервера' })).toBeNull();
  });

  it('первый сбой подсчёта диска отображает ошибку вместо загрузки', async () => {
    window.history.replaceState({}, '', '/settings#storage');
    const fetchMock = mockSettings();
    fetchMock.mockImplementation((path) => Promise.resolve(jsonResponse(path === '/api/settings/context'
      ? { admin: { enabled: false, unlocked: false } } : {
        used: 1, total: 10, unit: 'ГБ', percent: 10, breakdown: [], freeLabel: '9 ГБ',
        disk: { state: 'error', updatedAt: null, totalLabel: '0 Б', usedLabel: '0 Б', otherPct: 0, s3Pct: 0 },
      })));
    render(<SettingsPage />);
    const card = (await screen.findByText('Диск сервера')).closest('.set-card') as HTMLElement;
    expect(within(card).queryByText('Подсчитываем…')).toBeNull();
    expect(within(card).getByText('Ошибка подсчёта')).toBeTruthy();
    expect(within(card).getByText('Не удалось получить статистику')).toBeTruthy();
    expect(within(card).queryByText('0 Б из 0 Б')).toBeNull();
  });

  it('сохраняет ссылку на серверные настройки, независимые поиски и черновик между разделами', async () => {
    window.history.replaceState({}, '', '/settings#server-settings');
    mockSettings({ enabled: true, unlocked: true });
    render(<SettingsPage />);
    const configSearch = await screen.findByLabelText('Поиск по параметрам конфигурации');
    expect(window.location.hash).toBe('#server-settings');
    fireEvent.change(configSearch, { target: { value: 'Issuer' } });
    fireEvent.change(screen.getByLabelText('JwtSettings:Issuer'), { target: { value: 'draft' } });
    fireEvent.change(screen.getByLabelText('Поиск по настройкам'), { target: { value: 'Тема' } });
    expect(configSearch).toHaveProperty('value', 'Issuer');
    fireEvent.click(screen.getByRole('button', { name: /Тема.*Внешний вид/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Настройки сервера' }));
    expect(await screen.findByLabelText('JwtSettings:Issuer')).toHaveProperty('value', 'draft');
    expect(screen.getByLabelText('Поиск по параметрам конфигурации')).toHaveProperty('value', 'Issuer');
  });

  it('удаляет редактор при истечении админ-доступа', async () => {
    window.history.replaceState({}, '', '/settings#server-settings');
    const fetchMock = mockSettings({ enabled: true, unlocked: true });
    render(<SettingsPage />);
    await screen.findByLabelText('Поиск по параметрам конфигурации');
    fetchMock.mockImplementation((path) => Promise.resolve(jsonResponse(path === '/api/settings/server'
      ? { message: 'Доступ истёк' } : path === '/api/settings/system' ? system : {}, path === '/api/settings/server' ? 403 : 200)));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    await waitFor(() => expect(screen.queryByLabelText('Поиск по параметрам конфигурации')).toBeNull());
    expect(await screen.findByPlaceholderText('Админ-пароль')).toBeTruthy();
  });

  it('показывает Docker до ответа реестра и независимо от загрузки каналов', async () => {
    window.history.replaceState({}, '', '/settings#system');
    let resolveVersions!: (response: Response) => void;
    const versions = new Promise<Response>((resolve) => { resolveVersions = resolve; });
    const fetchMock = mockSettings({ enabled: true, unlocked: true });
    fetchMock.mockImplementation((path) => {
      if (path === '/api/settings/context') return Promise.resolve(jsonResponse({ admin: { enabled: true, unlocked: true } }));
      if (path === '/api/settings/system') return Promise.resolve(jsonResponse(system));
      if (path === '/api/system/services?includeVersions=false') return Promise.resolve(jsonResponse({ dockerOk: true, services: [
        { service: 'files', composeService: 'files', state: 'running', isWeb: false, image: 'files:latest', version: { state: 'unknown' } },
      ] }));
      if (path === '/api/system/services' || path === '/api/system/branches') return versions;
      return Promise.resolve(jsonResponse({ jobs: [] }));
    });
    render(<SettingsPage />);
    expect(await screen.findByText('Запущен')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Перезапустить files' })).toHaveProperty('disabled', false);
    expect(screen.getByText('Проверяем…')).toBeTruthy();
    await act(async () => { resolveVersions(jsonResponse({ message: 'Реестр недоступен' }, 503)); });
    expect(await screen.findByText('Реестр недоступен')).toBeTruthy();
    expect(screen.getByText('Запущен')).toBeTruthy();
    expect(within(document.querySelector('.svc-row')!).queryByText('Docker недоступен')).toBeNull();
  });
});
