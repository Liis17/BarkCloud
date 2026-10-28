import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiGet } from '../../lib/api';
import { MoveToFolderModal } from './MoveToFolderModal';

vi.mock('../../lib/api', () => ({
  apiGet: vi.fn(),
}));

const apiGetMock = vi.mocked(apiGet);

afterEach(() => vi.resetAllMocks());

function renderModal(progress?: { done: number; total: number }) {
  const onClose = vi.fn();
  const onPick = vi.fn();
  render(<MoveToFolderModal count={3} currentDir="src" onPick={onPick} onClose={onClose} progress={progress} />);
  return { onClose, onPick };
}

describe('MoveToFolderModal', () => {
  it('во время перемещения показывает прогресс и блокирует закрытие', async () => {
    apiGetMock.mockResolvedValue({ dirs: [] });
    const { onClose, onPick } = renderModal({ done: 2, total: 5 });

    const bar = await screen.findByRole('progressbar');
    expect(bar.getAttribute('aria-valuenow')).toBe('2');
    expect(bar.getAttribute('aria-valuemax')).toBe('5');
    expect(screen.getByText('Перемещено 2 из 5')).toBeTruthy();

    expect((screen.getByLabelText('Закрыть') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Отмена' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Перемещение…' }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();
    expect(onPick).not.toHaveBeenCalled();
  });

  it('без прогресса Esc закрывает модалку', async () => {
    apiGetMock.mockResolvedValue({ dirs: [] });
    const { onClose } = renderModal();

    await screen.findByText('Вложенных папок нет.');
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledOnce();
  });
});
