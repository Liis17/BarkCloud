import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiGet, apiPost } from '../../lib/api';
import type { DirProperties } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';
import { FolderPropertiesModal } from './FolderPropertiesModal';

vi.mock('../../lib/api', () => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
}));

const apiGetMock = vi.mocked(apiGet);
const apiPostMock = vi.mocked(apiPost);
const toast = vi.fn() as ToastPush;

const directory: DirProperties = {
  id: 'dir-1',
  name: 'Фотографии',
  createdAt: '2026-01-01T10:00:00Z',
  updatedAt: '2026-02-01T10:00:00Z',
  fileCount: 3,
  folderCount: 2,
  totalSize: 1_572_864,
  inherit: true,
  hideFromGallery: false,
  hideFromSearch: false,
  effectiveHideFromGallery: true,
  effectiveHideFromSearch: false,
  hasParent: true,
};

beforeEach(() => {
  apiGetMock.mockResolvedValue(directory);
});

afterEach(() => vi.resetAllMocks());

describe('FolderPropertiesModal', () => {
  it('показывает счётчики и размер папки', async () => {
    render(<FolderPropertiesModal directoryId="dir-1" onClose={vi.fn()} toast={toast} />);

    expect(await screen.findByText('Фотографии')).toBeTruthy();
    expect(screen.getByText('3')).toBeTruthy();
    expect(screen.getByText('2')).toBeTruthy();
    expect(screen.getByText('1,5 МБ')).toBeTruthy();
  });

  it('показывает effective-значения и блокирует переключатели видимости при наследовании', async () => {
    render(<FolderPropertiesModal directoryId="dir-1" onClose={vi.fn()} toast={toast} />);

    const inherit = await screen.findByRole('switch', { name: 'Наследовать настройки родительской папки' });
    const gallery = screen.getByRole('switch', { name: 'Скрыть из галереи' });
    const search = screen.getByRole('switch', { name: 'Скрыть из поиска' });
    expect(inherit.getAttribute('aria-checked')).toBe('true');
    expect(gallery.getAttribute('aria-checked')).toBe('true');
    expect(search.getAttribute('aria-checked')).toBe('false');
    expect(gallery).toHaveProperty('disabled', true);
    expect(search).toHaveProperty('disabled', true);
  });

  it('сохраняет полный набор настроек и блокирует переключатели на время запроса', async () => {
    let resolveSave!: (value: DirProperties) => void;
    apiPostMock.mockReturnValue(new Promise<DirProperties>((resolve) => { resolveSave = resolve; }));
    render(<FolderPropertiesModal directoryId="dir-1" onClose={vi.fn()} toast={toast} />);

    const inherit = await screen.findByRole('switch', { name: 'Наследовать настройки родительской папки' });
    fireEvent.click(inherit);

    expect(apiPostMock).toHaveBeenCalledWith('/api/cloud/dir/visibility', {
      id: 'dir-1', inherit: false, hideFromGallery: true, hideFromSearch: false,
    });
    expect(screen.getAllByRole('switch').every((item) => (item as HTMLButtonElement).disabled)).toBe(true);

    await act(async () => resolveSave({
      ...directory,
      inherit: false,
      hideFromGallery: true,
      effectiveHideFromGallery: true,
    }));
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Наследовать настройки родительской папки' })).toHaveProperty('disabled', false));
  });

  it('возвращает последнее подтверждённое состояние и показывает toast при ошибке', async () => {
    apiGetMock.mockResolvedValue({ ...directory, inherit: false, effectiveHideFromGallery: false });
    apiPostMock.mockRejectedValue(new Error('Сервер недоступен'));
    render(<FolderPropertiesModal directoryId="dir-1" onClose={vi.fn()} toast={toast} />);

    const gallery = await screen.findByRole('switch', { name: 'Скрыть из галереи' });
    fireEvent.click(gallery);

    await waitFor(() => expect(gallery.getAttribute('aria-checked')).toBe('false'));
    expect(toast).toHaveBeenCalledWith('Сервер недоступен', 'err');
  });

  it('оставляет переключатели видимости активными у корневой папки', async () => {
    const root = {
      ...directory,
      inherit: true,
      hideFromGallery: false,
      hideFromSearch: false,
      effectiveHideFromGallery: false,
      effectiveHideFromSearch: false,
      hasParent: false,
    };
    apiGetMock.mockResolvedValue(root);
    apiPostMock
      .mockResolvedValueOnce({ ...root, inherit: false, hideFromGallery: true, effectiveHideFromGallery: true })
      .mockResolvedValueOnce(root);
    render(<FolderPropertiesModal directoryId="dir-1" onClose={vi.fn()} toast={toast} />);

    const gallery = await screen.findByRole('switch', { name: 'Скрыть из галереи' });
    const search = screen.getByRole('switch', { name: 'Скрыть из поиска' });
    expect(screen.queryByRole('switch', { name: 'Наследовать настройки родительской папки' })).toBeNull();
    expect(gallery).toHaveProperty('disabled', false);
    expect(search).toHaveProperty('disabled', false);

    fireEvent.click(gallery);
    expect(apiPostMock).toHaveBeenNthCalledWith(1, '/api/cloud/dir/visibility', {
      id: 'dir-1', inherit: true, hideFromGallery: true, hideFromSearch: false,
    });
    await waitFor(() => expect(gallery.getAttribute('aria-checked')).toBe('true'));
    expect(gallery).toHaveProperty('disabled', false);
    expect(search).toHaveProperty('disabled', false);

    fireEvent.click(gallery);
    expect(apiPostMock).toHaveBeenNthCalledWith(2, '/api/cloud/dir/visibility', {
      id: 'dir-1', inherit: false, hideFromGallery: false, hideFromSearch: false,
    });
    await waitFor(() => expect(gallery.getAttribute('aria-checked')).toBe('false'));
    expect(gallery).toHaveProperty('disabled', false);
    expect(search).toHaveProperty('disabled', false);
  });
});
