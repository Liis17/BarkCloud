import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiGet } from '../../lib/api';
import { MoveToFolderModal } from './MoveToFolderModal';

vi.mock('../../lib/api', () => ({
  apiGet: vi.fn(),
}));

const apiGetMock = vi.mocked(apiGet);

afterEach(() => {
  vi.resetAllMocks();
  vi.restoreAllMocks();
});

function renderModal(
  progress?: { done: number; total: number },
  dirs: { id: string; name: string }[] = [],
  currentDir = 'src',
) {
  const onClose = vi.fn();
  const onPick = vi.fn();
  apiGetMock.mockResolvedValue({
    dirs: dirs.map((d) => ({ ...d, updatedAt: '2026-01-01T00:00:00Z' })),
    files: [],
  });
  render(<MoveToFolderModal count={3} currentDir={currentDir} onPick={onPick} onClose={onClose} progress={progress} />);
  return { onClose, onPick };
}

function popstate() {
  act(() => {
    window.dispatchEvent(new PopStateEvent('popstate', { state: window.history.state }));
  });
}

describe('MoveToFolderModal: прогресс перемещения', () => {
  it('во время перемещения показывает прогресс и блокирует закрытие', async () => {
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
    const { onClose } = renderModal();

    await screen.findByText('Вложенных папок нет.');
    fireEvent.keyDown(window, { key: 'Escape' });
    popstate();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('во время перемещения «назад» браузера не закрывает модалку', async () => {
    const { onClose } = renderModal({ done: 0, total: 5 }, [{ id: 'dir-a', name: 'Папка А' }]);
    await screen.findByText('Папка А');

    popstate();

    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('MoveToFolderModal: кнопка «назад» браузера', () => {
  it('на вложенной папке «назад» шагает вверх по дереву, а не закрывает модалку', async () => {
    const { onClose } = renderModal(undefined, [{ id: 'dir-a', name: 'Папка А' }], '');
    fireEvent.click(await screen.findByText('Папка А'));
    expect(await screen.findByText('Переместить сюда')).toBeTruthy();

    popstate();

    expect(onClose).not.toHaveBeenCalled();
    expect(await screen.findByText('Файлы уже здесь')).toBeTruthy();
  });

  it('в корне «назад» закрывает модалку', async () => {
    const { onClose } = renderModal(undefined, [{ id: 'dir-a', name: 'Папка А' }]);
    await screen.findByText('Папка А');

    popstate();

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('кнопка «Отмена» снимает свою запись истории и закрывает модалку после popstate', async () => {
    const go = vi.spyOn(window.history, 'go');
    const { onClose } = renderModal(undefined, [{ id: 'dir-a', name: 'Папка А' }]);
    await screen.findByText('Папка А');

    fireEvent.click(screen.getByText('Отмена'));

    expect(go).toHaveBeenCalledWith(-1);
    expect(onClose).not.toHaveBeenCalled();
    popstate();
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
