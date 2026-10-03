import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ServerSettingsTab from './ServerSettingsTab';
import { jsonResponse, serverSettings, setting, storageProfile } from '../test/settingsFixtures';

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });
function mockServer(data = serverSettings) {
  const fetchMock = vi.fn((path: string, _init?: RequestInit) => Promise.resolve(jsonResponse(
    path === '/api/settings/server' ? data : { success: true, message: 'Доступ подтверждён', restartTargets: ['files'] },
  )));
  vi.stubGlobal('fetch', (path: string, init?: RequestInit) => ['/api/settings/migration/jobs', '/api/settings/migration/cutovers'].includes(path)
    ? Promise.resolve(jsonResponse([])) : fetchMock(path, init));
  return fetchMock;
}
async function expandAll() {
  const button = await screen.findByRole('button', { name: 'Развернуть всё' });
  await act(async () => { fireEvent.click(button); });
}
function editor() { return within(document.querySelector('.storage-profile-card') as HTMLElement); }
async function confirm() { fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Подтвердить' })); }

describe('ServerSettingsTab', () => {
  it('не выводит секреты профиля и скрывает служебные токены', async () => {
    const unsafeProfile = { ...storageProfile, accessKey: 'RAW_ACCESS_MUST_NOT_RENDER', secretKey: 'RAW_SECRET_MUST_NOT_RENDER' };
    mockServer({ ...serverSettings, settings: [
      { ...setting, key: 'SecretKey', value: '', isSensitive: true, valueKind: 'password' },
      { ...setting, section: 'UsersService', key: 'Token', value: '', isSensitive: true },
    ], storageProfiles: [unsafeProfile] });
    render(<ServerSettingsTab />);
    await expandAll();
    expect(editor().getByPlaceholderText('Задан')).toHaveProperty('value', '');
    expect(editor().getByPlaceholderText('Оставьте пустым, чтобы не менять')).toHaveProperty('value', '');
    expect(editor().getByLabelText('Показать введённый secret key')).toHaveProperty('disabled', true);
    expect(document.body.textContent).not.toContain('RAW_SECRET_MUST_NOT_RENDER');
    expect(document.body.textContent).not.toContain('RAW_ACCESS_MUST_NOT_RENDER');
    expect(screen.queryByLabelText('UsersService:Token')).toBeNull();
  });
  it('не предлагает вводить значение для read-only параметра', async () => {
    mockServer({ ...serverSettings, settings: [{ ...setting, serviceId: 2, section: 'UsersDb', key: '', value: '', isSensitive: true, isReadOnly: true, valueKind: 'password', editedFrom: 'env' }] });
    render(<ServerSettingsTab />);
    await expandAll();
    const input = screen.getByLabelText('UsersDb');
    expect(input).toHaveProperty('disabled', true);
    expect(input.getAttribute('placeholder')).toBe('');
  });
  it('проверяет подключение по изменённому черновику без сохранения', async () => {
    const fetchMock = mockServer();
    render(<ServerSettingsTab />);
    await expandAll();
    fireEvent.change(editor().getByLabelText('Endpoint'), { target: { value: 'https://draft.example' } });
    fireEvent.click(editor().getByRole('button', { name: 'Проверить доступ' }));
    expect(await screen.findByText('Доступ подтверждён')).toBeTruthy();
    const call = fetchMock.mock.calls.find(([path]) => path.endsWith('/storage/check'));
    expect(JSON.parse(call?.[1]?.body as string)).toMatchObject({
      role: 'universal', serviceUrl: 'https://draft.example', bucketName: 'cloud-universal',
      accessKey: '', secretKey: '', profileId: 'universal-v1',
    });
    expect(fetchMock.mock.calls.some(([path]) => path.endsWith('/storage/profile'))).toBe(false);
  });
  it('подтверждает изменения квоты в диалоге и сохраняет целое значение с единицей', async () => {
    const fetchMock = mockServer({ ...serverSettings, storageProfiles: [{ ...storageProfile, quotaBytes: '2199023255552' }] });
    render(<ServerSettingsTab />);
    await expandAll();
    expect(editor().getByLabelText('Квота S3 бакета')).toHaveProperty('value', '2');
    expect(editor().getByRole('combobox', { name: 'Единица квоты' }).textContent).toBe('ТБ');
    fireEvent.change(editor().getByLabelText('Квота S3 бакета'), { target: { value: '3' } });
    fireEvent.click(editor().getByRole('combobox', { name: 'Единица квоты' }));
    fireEvent.click(screen.getByRole('option', { name: 'ПБ' }));
    fireEvent.click(editor().getByRole('button', { name: 'Сохранить профиль' }));
    expect(within(await screen.findByRole('dialog')).getByText('Квота: 3 PB')).toBeTruthy();
    expect(fetchMock.mock.calls.some(([path]) => path.endsWith('/storage/profile'))).toBe(false);
    await confirm();
    await waitFor(() => expect(fetchMock.mock.calls.some(([path]) => path.endsWith('/storage/profile'))).toBe(true));
    const call = fetchMock.mock.calls.find(([path]) => path.endsWith('/storage/profile'));
    expect(JSON.parse(call?.[1]?.body as string)).toMatchObject({ quotaValue: '3', quotaUnit: 'pb', accessKey: '', secretKey: '' });
  });
  it('сворачивает группы, раскрывает совпадения и восстанавливает прежнее раскрытие и черновики', async () => {
    mockServer();
    render(<ServerSettingsTab />);
    const search = await screen.findByLabelText('Поиск по параметрам конфигурации');
    const group = document.querySelector('.server-service-card') as HTMLDetailsElement;
    expect(group.open).toBe(false);
    fireEvent.change(search, { target: { value: 'Issuer' } });
    await waitFor(() => expect(group.open).toBe(true));
    fireEvent.change(screen.getByLabelText('JwtSettings:Issuer'), { target: { value: 'draft' } });
    fireEvent.change(search, { target: { value: 'нет-такого-параметра' } });
    expect(screen.getByText('По этому запросу параметры не найдены.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Очистить поиск конфигураций' }));
    await waitFor(() => expect(group.open).toBe(false));
    await expandAll();
    expect(screen.getByLabelText('JwtSettings:Issuer')).toHaveProperty('value', 'draft');
    fireEvent.click(screen.getByRole('button', { name: 'Свернуть всё' }));
    expect(group.open).toBe(false);
    await expandAll();
    expect(screen.getByLabelText('JwtSettings:Issuer')).toHaveProperty('value', 'draft');
  });
  it('сохранение соседнего параметра оставляет другой черновик', async () => {
    const data = structuredClone(serverSettings);
    const fetchMock = mockServer(data);
    fetchMock.mockImplementation((path, init) => {
      if (path.endsWith('/server/value')) {
        const body = JSON.parse(init!.body as string) as { key: string; value: string };
        data.settings.find((item) => item.key === body.key)!.value = body.value;
      }
      return Promise.resolve(jsonResponse(path === '/api/settings/server' ? data : { success: true, message: 'Сохранено', restartTargets: ['identity'] }));
    });
    render(<ServerSettingsTab />);
    await expandAll();
    fireEvent.change(screen.getByLabelText('JwtSettings:Issuer'), { target: { value: 'draft' } });
    fireEvent.change(screen.getByLabelText('JwtSettings:Audience'), { target: { value: 'saved' } });
    const row = screen.getByLabelText('JwtSettings:Audience').closest('.server-setting-row')!;
    fireEvent.click(within(row as HTMLElement).getByRole('button', { name: 'Сохранить' }));
    await confirm();
    await waitFor(() => expect(within(row as HTMLElement).getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', true));
    expect(screen.getByLabelText('JwtSettings:Issuer')).toHaveProperty('value', 'draft');
  });
  it('показывает fallback universal и сохраняет черновик S3 при переключении ролей', async () => {
    mockServer();
    render(<ServerSettingsTab />);
    await expandAll();
    const role = screen.getByRole('button', { name: /Изображения images Universal/ });
    expect(role.textContent).toContain('cloud-universal');
    fireEvent.change(editor().getByLabelText('Endpoint'), { target: { value: 'https://draft.example' } });
    fireEvent.click(role);
    expect(editor().getByText('До создания профиля новые файлы этой роли используют universal.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Универсальное universal v1/ }));
    expect(editor().getByLabelText('Endpoint')).toHaveProperty('value', 'https://draft.example');
  });
  it('активация другой версии сохраняет черновик редактируемого профиля', async () => {
    const data = structuredClone(serverSettings);
    data.storageProfiles.push({ ...storageProfile, profileId: 'universal-v2', version: 2, isActive: false });
    const fetchMock = mockServer(data);
    fetchMock.mockImplementation((path, init) => {
      if (path.endsWith('/storage/activate')) {
        const body = JSON.parse(init!.body as string);
        data.storageProfiles.forEach((profile) => { profile.isActive = profile.profileId === body.profileId; });
      }
      return Promise.resolve(jsonResponse(path === '/api/settings/server' ? data : { success: true, message: 'Выполнено', restartTargets: ['files'] }));
    });
    render(<ServerSettingsTab />);
    await expandAll();
    fireEvent.change(editor().getByLabelText('Endpoint'), { target: { value: 'https://draft.example' } });
    fireEvent.change(editor().getByLabelText('Secret key'), { target: { value: 'unsaved-secret' } });
    fireEvent.click(editor().getByRole('button', { name: 'Активировать' }));
    await confirm();
    await waitFor(() => expect(screen.getByRole('button', { name: /Универсальное universal v2/ })).toBeTruthy());
    // Версии отсортированы от новой к старой; возвращаемся к исходному профилю.
    const versions = document.querySelectorAll('.storage-version-row');
    fireEvent.click(within(versions[1] as HTMLElement).getByRole('button', { name: 'Редактировать' }));
    expect(editor().getByLabelText('Endpoint')).toHaveProperty('value', 'https://draft.example');
    expect(editor().getByLabelText('Secret key')).toHaveProperty('value', 'unsaved-secret');
  });
  it('создаёт профиль, скрывает credentials в подтверждении и очищает их после сохранения', async () => {
    const data = structuredClone(serverSettings);
    const fetchMock = mockServer(data);
    fetchMock.mockImplementation((path, init) => {
      if (path.endsWith('/storage/profile')) {
        const body = JSON.parse(init!.body as string);
        data.storageProfiles.push({ ...storageProfile, ...body, hasAccessKey: true, hasSecretKey: true, profileId: 'images-v1', version: 1 });
      }
      return Promise.resolve(jsonResponse(path === '/api/settings/server' ? data : { success: true, message: 'Сохранено', restartTargets: ['files'] }));
    });
    render(<ServerSettingsTab />);
    await expandAll();
    fireEvent.click(screen.getByRole('button', { name: /Изображения images Universal/ }));
    fireEvent.change(editor().getByLabelText('Endpoint'), { target: { value: 'https://s3.example' } });
    fireEvent.change(editor().getByLabelText('Bucket'), { target: { value: 'images' } });
    fireEvent.change(editor().getByLabelText('Access key'), { target: { value: 'new-access' } });
    fireEvent.change(editor().getByLabelText('Secret key'), { target: { value: 'new-secret' } });
    fireEvent.click(editor().getByRole('button', { name: 'Создать профиль' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).not.toContain('new-secret');
    expect(dialog.textContent).not.toContain('new-access');
    await confirm();
    await waitFor(() => expect(editor().getByLabelText('Secret key')).toHaveProperty('value', ''));
    expect(editor().getByLabelText('Access key')).toHaveProperty('value', '');
    expect(screen.getByText(/Требуется перезапуск: files/)).toBeTruthy();
  });
  it('разделяет legacy, версии и активацию профиля', async () => {
    const fetchMock = mockServer({ ...serverSettings, storageProfiles: [storageProfile,
      { ...storageProfile, profileId: 'universal-v2', version: 2, isActive: false },
      { ...storageProfile, profileId: 'images-old-v1', role: 'images-old', isLegacy: true, isActive: false },
    ] });
    render(<ServerSettingsTab />);
    await expandAll();
    expect(screen.getByText('Legacy · существующие файлы')).toBeTruthy();
    fireEvent.click(editor().getByText(/Версии и история/));
    fireEvent.click(editor().getByRole('button', { name: 'Активировать' }));
    await confirm();
    await waitFor(() => expect(fetchMock.mock.calls.some(([path]) => path.endsWith('/storage/activate'))).toBe(true));
    const call = fetchMock.mock.calls.find(([path]) => path.endsWith('/storage/activate'));
    expect(JSON.parse(call?.[1]?.body as string)).toEqual({ profileId: 'universal-v2' });
  });
  it('ищет названия S3-полей и показывает ошибки рядом с полями до записи', async () => {
    const fetchMock = mockServer({ ...serverSettings, storageProfiles: [] });
    render(<ServerSettingsTab />);
    const search = await screen.findByLabelText('Поиск по параметрам конфигурации');
    await act(async () => { fireEvent.change(search, { target: { value: 'Endpoint' } }); });
    expect((document.querySelector('.server-storage-section') as HTMLDetailsElement).open).toBe(true);
    expect(screen.getByText(/0 параметров · 1 поле S3/)).toBeTruthy();
    fireEvent.change(editor().getByLabelText('Endpoint'), { target: { value: 'invalid' } });
    fireEvent.click(editor().getByRole('button', { name: 'Создать профиль' }));
    expect(editor().getByText('Укажите HTTP(S) endpoint')).toBeTruthy();
    expect(editor().getByLabelText('Endpoint').getAttribute('aria-invalid')).toBe('true');
    expect(fetchMock.mock.calls).toHaveLength(1);
  });
  it('сообщает об истечении админ-доступа после 403', async () => {
    const expired = vi.fn();
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ message: 'Доступ истёк' }, 403))));
    render(<ServerSettingsTab onAccessExpired={expired} />);
    await waitFor(() => expect(expired).toHaveBeenCalled());
    expect(screen.queryByLabelText('Endpoint')).toBeNull();
  });
});
