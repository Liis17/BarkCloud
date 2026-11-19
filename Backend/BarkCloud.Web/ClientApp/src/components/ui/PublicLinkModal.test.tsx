import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const apiPost = vi.hoisted(() => vi.fn());
vi.mock('../../lib/api', () => ({ apiPost }));

import { createFolderShare } from '../../lib/share';
import { formatLinkExpiry, PublicLinkDialogHost } from './PublicLinkModal';

const NOW = new Date('2026-11-19T10:00:00Z');
const IN_7_DAYS = '2026-11-26T10:00:00.000Z';

describe('Публичная ссылка со сроком', () => {
  let writeText: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(NOW);
    writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    apiPost.mockReset();
  });
  afterEach(() => vi.useRealTimers());

  it('показывает дату окончания и отправляет выбранный срок', async () => {
    apiPost.mockResolvedValue({ url: 'https://cloud/f/abc', expiresAt: IN_7_DAYS });
    const toast = vi.fn();
    render(<PublicLinkDialogHost />);

    act(() => createFolderShare('dir-1', 'Отчёты', toast));
    expect(screen.getByText('Ссылка будет работать, пока вы её не отзовёте')).toBeTruthy();

    fireEvent.click(screen.getByRole('radio', { name: 'На 7 дней' }));
    expect(screen.getByText(formatLinkExpiry(IN_7_DAYS))).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Создать и скопировать' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiPost).toHaveBeenCalledWith('/api/folder-shares', { directoryId: 'dir-1', name: 'Отчёты', expiresAt: IN_7_DAYS });
    expect(writeText).toHaveBeenCalledWith('https://cloud/f/abc');
    expect(toast).toHaveBeenCalledWith(`Ссылка на папку скопирована · действует до ${formatLinkExpiry(IN_7_DAYS)}`);
  });

  it('по умолчанию создаёт бессрочную ссылку', async () => {
    apiPost.mockResolvedValue({ url: 'https://cloud/f/abc', expiresAt: null });
    const toast = vi.fn();
    render(<PublicLinkDialogHost />);

    act(() => createFolderShare('dir-1', 'Отчёты', toast));
    fireEvent.click(screen.getByRole('button', { name: 'Создать и скопировать' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiPost).toHaveBeenCalledWith('/api/folder-shares', { directoryId: 'dir-1', name: 'Отчёты', expiresAt: null });
    expect(toast).toHaveBeenCalledWith('Ссылка на папку скопирована');
  });

  it('при ошибке оставляет диалог открытым и показывает её', async () => {
    apiPost.mockRejectedValue(new Error('Срок действия ссылки должен быть в будущем'));
    const toast = vi.fn();
    render(<PublicLinkDialogHost />);

    act(() => createFolderShare('dir-1', 'Отчёты', toast));
    fireEvent.click(screen.getByRole('button', { name: 'Создать и скопировать' }));

    await waitFor(() => expect(toast).toHaveBeenCalledWith('Срок действия ссылки должен быть в будущем', 'err'));
    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(writeText).not.toHaveBeenCalled();
  });
});
