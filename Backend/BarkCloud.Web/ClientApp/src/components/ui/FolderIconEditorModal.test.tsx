import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { FolderIconEditorModal } from './FolderIconEditorModal';

// jsdom без пакета canvas: рисование треугольника пропускается.
HTMLCanvasElement.prototype.getContext = (() => null) as typeof HTMLCanvasElement.prototype.getContext;

function renderEditor(initial = { iconKey: null as string | null, iconColor: null as string | null }) {
  const onSave = vi.fn().mockResolvedValue(undefined);
  render(<FolderIconEditorModal folderName="Музыка" initial={initial} onClose={vi.fn()} onSave={onSave} />);
  return onSave;
}

describe('FolderIconEditorModal', () => {
  it('сохраняет выбранные иконку и цвет', async () => {
    const onSave = renderEditor();
    expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole('option', { name: 'Музыка' }));
    fireEvent.click(screen.getByRole('button', { name: 'Цвет #EF6C00' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledWith({ iconKey: 'music', iconColor: '#EF6C00' }));
  });

  it('фильтрует иконки по ключевым словам', () => {
    renderEditor();
    fireEvent.change(screen.getByLabelText('Поиск иконок'), { target: { value: '18+' } });

    expect(screen.getByRole('option', { name: 'Для взрослых' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Музыка' })).toBeNull();
  });

  it('не даёт сохранить некорректный HEX и сбрасывает значок к стандартному', async () => {
    const onSave = renderEditor({ iconKey: 'archive', iconColor: '#2E7D32' });
    const hex = screen.getByLabelText('HEX цвета подложки');

    fireEvent.change(hex, { target: { value: '#12' } });
    expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: 'Сбросить' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledWith({ iconKey: null, iconColor: null }));
  });
});
