import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import MigrationTab from './MigrationTab';
import { MigrationJob, MigrationSource } from '../hooks/useStorageMigration';
import { jsonResponse } from '../test/settingsFixtures';
import { migrationPercent } from '../components/settings/MigrationProgress';
import { MigrationApplyPanel } from '../components/settings/MigrationApplyPanel';

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });
const source: MigrationSource = { id: 'source-id', serviceUrl: 'https://source.example', bucketName: 'source-bucket', roles: ['images', 'cloud-files-old'], profileIds: ['images-v1', 'images-v2', 'cloud-files-old-v1'] };
const job: MigrationJob = { id: 'job-id', source, destination: { serviceUrl: 'https://target.example', bucketName: 'target-bucket', region: 'eu-central-1', forcePathStyle: false, isR2: false },
  state: 'running', phase: 'verifying', totalBytes: '100', copiedBytes: '40', currentBytes: '10', totalFiles: 10, copiedFiles: 4,
  currentKey: 'вложено/имя +%?#.txt', currentName: 'имя.txt', error: null, activeUploads: 0, canApply: false, canRetry: false, canCancel: true, createdAt: '2026-10-03T00:00:00Z' };
function mockMigration(jobs: MigrationJob[] = []) {
  const fetchMock = vi.fn((path: string, _init?: RequestInit) => Promise.resolve(jsonResponse(
    path.endsWith('/sources') ? [source] : path.endsWith('/jobs') ? jobs : path.endsWith('/cutovers') ? []
      : path.endsWith('/check') ? { validationId: 'checked-on-server', message: 'Тестовый объект удалён' } : job,
  )));
  vi.stubGlobal('fetch', fetchMock); return fetchMock;
}
async function fill() {
  fireEvent.click(await screen.findByRole('combobox', { name: 'Исходный бакет' }));
  fireEvent.click(await screen.findByRole('option', { name: /source-bucket/ }));
  for (const [label, value] of [['Endpoint назначения', 'https://target.example'], ['Bucket назначения', 'target-bucket'], ['Access key назначения', 'entered-access'], ['Secret key назначения', 'entered-secret']])
    fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe('Миграция S3', () => {
  it.each([
    ['missing', 'Такого S3-ключа нет в назначении'],
    ['metadata', 'Объект уже есть, но метаданные отличаются'],
    ['header:Content-Type', 'Объект уже есть, но отличается заголовок Content-Type'],
    ['content', 'Объект уже есть, но SHA-256 содержимого отличается'],
  ])('показывает причину передачи %s и отделяет готовые копии от новых загрузок', async (currentReason, message) => {
    mockMigration([{ ...job, currentReason, skippedFiles: 3, uploadedFiles: 1 }]); render(<MigrationTab onExpired={() => {}} />);
    expect(await screen.findByText(message)).toBeTruthy();
    expect(screen.getByText('Без повторной загрузки: 3 · Успешных загрузок с проверкой: 1')).toBeTruthy();
    expect(screen.getByText('Проверка загруженной копии')).toBeTruthy();
  });

  it.each([
    ['checking-existing', 'Проверка существующих копий'],
    ['reading', 'Чтение части из источника'],
    ['final-reading', 'Чтение части для досинхронизации'],
  ])('объясняет этап %s и учитывает уже проверенные копии', async (phase, label) => {
    mockMigration([{ ...job, phase, copiedBytes: '60', currentBytes: '0', copiedFiles: 6 }]);
    render(<MigrationTab onExpired={() => {}} />);
    expect(await screen.findByText(label)).toBeTruthy();
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('60');
    expect(screen.getByText('6 / 10')).toBeTruthy();
  });

  it('после остановки объясняет запуск новой задачи с существующими копиями', async () => {
    mockMigration([{ ...job, state: 'cancelled', canCancel: false }]); render(<MigrationTab onExpired={() => {}} />);
    expect(await screen.findByText(/Новая задача проверит их и докопирует оставшиеся файлы/)).toBeTruthy();
    expect(screen.getByText(/После перезапуска Web запустите новую задачу с тем же назначением/)).toBeTruthy();
    expect(screen.queryByText(/требует пустого бакета/)).toBeNull();
  });

  it.each([
    ['cloud-video.s3.fra.databucket.eu', 'https://cloud-video.s3.fra.databucket.eu'],
    ['  minio.example:9000  ', 'https://minio.example:9000'],
    ['http://127.0.0.1:9000', 'http://127.0.0.1:9000'],
  ])('проверяет endpoint %s и отправляет полный адрес %s', async (entered, expected) => {
    const fetch = mockMigration(); render(<MigrationTab onExpired={() => {}} />);
    await fill();
    fireEvent.change(screen.getByLabelText('Endpoint назначения'), { target: { value: entered } });
    const check = screen.getByRole('button', { name: 'Проверить доступ и запись' });
    expect(check).toHaveProperty('disabled', false);
    fireEvent.click(check);
    await screen.findByRole('button', { name: 'Перенести' });
    const request = fetch.mock.calls.find(([path]) => path.endsWith('/check'));
    expect(JSON.parse(request?.[1]?.body as string).destination.serviceUrl).toBe(expected);
  });

  it('объясняет невалидный endpoint рядом с полем и снимает ошибку после исправления', async () => {
    mockMigration(); render(<MigrationTab onExpired={() => {}} />);
    await fill();
    const endpoint = screen.getByLabelText('Endpoint назначения');
    fireEvent.change(endpoint, { target: { value: 'ftp://target.example' } });
    expect(screen.getByRole('button', { name: 'Проверить доступ и запись' })).toHaveProperty('disabled', true);
    expect(endpoint.getAttribute('aria-invalid')).toBe('true');
    expect(document.getElementById(endpoint.getAttribute('aria-describedby')!)?.textContent).toContain('HTTP');
    fireEvent.change(endpoint, { target: { value: 'target.example' } });
    fireEvent.blur(endpoint);
    expect(endpoint).toHaveProperty('value', 'https://target.example');
    expect(endpoint.getAttribute('aria-invalid')).toBe('false');
    expect(screen.getByRole('button', { name: 'Проверить доступ и запись' })).toHaveProperty('disabled', false);
  });

  it('проверяет доступ только после заполнения, инвалидирует проверку и запускает по серверному ID', async () => {
    const localWrites = vi.fn(); const sessionWrites = vi.fn();
    vi.stubGlobal('localStorage', { setItem: localWrites }); vi.stubGlobal('sessionStorage', { setItem: sessionWrites });
    const fetch = mockMigration(); render(<MigrationTab onExpired={() => {}} />);
    expect(screen.getByRole('button', { name: 'Проверить доступ и запись' })).toHaveProperty('disabled', true);
    await fill(); fireEvent.click(screen.getByRole('button', { name: 'Проверить доступ и запись' }));
    expect(await screen.findByRole('button', { name: 'Перенести' })).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Регион назначения'), { target: { value: 'eu-central-1' } });
    expect(screen.queryByRole('button', { name: 'Перенести' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Проверить доступ и запись' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Перенести' }));
    await waitFor(() => expect(fetch.mock.calls.some(([path]) => path.endsWith('/start'))).toBe(true));
    const start = fetch.mock.calls.find(([path]) => path.endsWith('/start'));
    expect(JSON.parse(start?.[1]?.body as string)).toEqual({ validationId: 'checked-on-server' });
    await waitFor(() => expect(screen.getByLabelText('Secret key назначения')).toHaveProperty('value', ''));
    expect(localWrites).not.toHaveBeenCalled(); expect(sessionWrites).not.toHaveBeenCalled();
  });

  it('показывает проверенные файлы, полный ключ, волну и продолжение после ошибки', async () => {
    const fetch = mockMigration([{ ...job, state: 'failed', canRetry: true, error: 'Контрольная сумма не совпала' }]);
    render(<MigrationTab onExpired={() => {}} />);
    expect(await screen.findByText(job.currentKey!)).toBeTruthy();
    expect(screen.getByText('4 / 10')).toBeTruthy();
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('50');
    expect(screen.getByRole('alert').textContent).toContain('Контрольная сумма');
    fireEvent.click(screen.getByRole('button', { name: 'Продолжить' }));
    await waitFor(() => expect(fetch.mock.calls.some(([path]) => path.endsWith('/job-id/retry'))).toBe(true));
  });

  it('повторно закрывает административную вкладку при 403', async () => {
    const expired = vi.fn(); const fetch = mockMigration();
    fetch.mockImplementation(() => Promise.resolve(jsonResponse({ message: 'Доступ истёк' }, 403)));
    render(<MigrationTab onExpired={expired} />);
    await waitFor(() => expect(expired).toHaveBeenCalled());
  });

  it('приостанавливает polling в скрытой вкладке и обновляет состояние сразу после возврата', async () => {
    vi.useFakeTimers(); const fetch = mockMigration();
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    render(<MigrationTab onExpired={() => {}} />);
    await act(async () => {});
    const before = fetch.mock.calls.filter(([path]) => path.endsWith('/jobs')).length;
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(fetch.mock.calls.filter(([path]) => path.endsWith('/jobs')).length).toBe(before + 1);
    visibility.mockReturnValue('hidden'); hidden.mockReturnValue(true); fireEvent(document, new Event('visibilitychange'));
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(fetch.mock.calls.filter(([path]) => path.endsWith('/jobs')).length).toBe(before + 1);
    visibility.mockReturnValue('visible'); hidden.mockReturnValue(false);
    await act(async () => { fireEvent(document, new Event('visibilitychange')); });
    expect(fetch.mock.calls.filter(([path]) => path.endsWith('/jobs')).length).toBe(before + 2);
  });

  it('подтверждает назначение и все связанные профили перед применением', async () => {
    const fetch = mockMigration([{ ...job, state: 'copied', phase: 'copied', canApply: true }]);
    render(<MigrationApplyPanel active onExpired={() => {}} onApplied={async () => {}} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Применить миграцию' }));
    const dialog = within(await screen.findByRole('dialog'));
    expect(dialog.getByText(/images-v1, images-v2, cloud-files-old-v1/)).toBeTruthy();
    expect(dialog.getByText(/Назначение: https:\/\/target.example/)).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: 'Применить миграцию' }));
    await waitFor(() => expect(fetch.mock.calls.some(([path]) => path.endsWith('/storage/migration/apply'))).toBe(true));
  });

  it('при возврате отменяет зависший polling и игнорирует его запоздалый ответ', async () => {
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    const fetch = mockMigration(); let pending = true; let finish: (response: Response) => void = () => {};
    fetch.mockImplementation((path: string) => {
      if (path.endsWith('/sources')) return Promise.resolve(jsonResponse([source]));
      if (path.endsWith('/jobs') && pending) { pending = false; return new Promise<Response>(resolve => { finish = resolve; }); }
      return Promise.resolve(jsonResponse(path.endsWith('/jobs') ? [{ ...job, currentKey: 'fresh/key' }] : []));
    });
    render(<MigrationTab onExpired={() => {}} />); await act(async () => {});
    visibility.mockReturnValue('hidden'); hidden.mockReturnValue(true);
    await act(async () => { fireEvent(document, new Event('visibilitychange')); });
    visibility.mockReturnValue('visible'); hidden.mockReturnValue(false);
    await act(async () => { fireEvent(document, new Event('visibilitychange')); });
    expect(fetch.mock.calls.filter(([path]) => path.endsWith('/jobs'))).toHaveLength(2);
    expect(await screen.findByText('fresh/key')).toBeTruthy();
    await act(async () => { finish(jsonResponse([{ ...job, currentKey: 'stale/key' }])); });
    expect(screen.queryByText('stale/key')).toBeNull();
  });

  it('считает пустой завершённый бакет и большие объёмы без потери точности', () => {
    expect(migrationPercent({ ...job, state: 'copied', totalBytes: '0', copiedBytes: '0' })).toBe(100);
    expect(migrationPercent({ ...job, totalBytes: '18014398509481984', copiedBytes: '9007199254740992', currentBytes: '0' })).toBe(50);
    expect(migrationPercent({ ...job, copiedBytes: '100', currentBytes: '0' })).toBe(99);
  });
});
