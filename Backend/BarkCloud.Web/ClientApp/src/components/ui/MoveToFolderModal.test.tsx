import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MoveToFolderModal } from './MoveToFolderModal';

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function renderModal(onClose = vi.fn(), onPick = vi.fn()) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>().mockImplementation(async () =>
      jsonResponse({ dirs: [{ id: 'dir-a', name: 'Папка А', updatedAt: '2026-01-01T00:00:00Z' }], files: [] }),
    ),
  );
  render(<MoveToFolderModal count={2} currentDir="" onPick={onPick} onClose={onClose} />);
  return { onClose, onPick };
}

function popstate() {
  act(() => {
    window.dispatchEvent(new PopStateEvent('popstate', { state: window.history.state }));
  });
}

describe('MoveToFolderModal: кнопка «назад» браузера', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('на вложенной папке «назад» шагает вверх по дереву, а не закрывает модалку', async () => {
    const { onClose } = renderModal();
    fireEvent.click(await screen.findByText('Папка А'));
    expect(await screen.findByText('Переместить сюда')).toBeTruthy();

    popstate();

    expect(onClose).not.toHaveBeenCalled();
    expect(await screen.findByText('Файлы уже здесь')).toBeTruthy();
  });

  it('в корне «назад» закрывает модалку', async () => {
    const { onClose } = renderModal();
    await screen.findByText('Папка А');

    popstate();

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('кнопка «Отмена» снимает свою запись истории и закрывает модалку после popstate', async () => {
    const go = vi.spyOn(window.history, 'go');
    const { onClose } = renderModal();
    await screen.findByText('Папка А');

    fireEvent.click(screen.getByText('Отмена'));

    expect(go).toHaveBeenCalledWith(-1);
    expect(onClose).not.toHaveBeenCalled();
    popstate();
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
