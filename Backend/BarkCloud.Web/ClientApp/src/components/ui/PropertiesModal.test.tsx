import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiGet, apiPut } from '../../lib/api';
import { PropertiesModal } from './PropertiesModal';

vi.mock('../../lib/api', () => ({ apiGet: vi.fn(), apiPut: vi.fn() }));
const info = { id: 'file-1', name: 'document.txt', kind: 'document', searchAlias: 'отпуск', tags: ['личное'] };
beforeEach(() => {
  vi.mocked(apiGet).mockImplementation(async (path) => {
    if (path === '/api/settings/tags') return { items: [
      { id: '1', name: 'личное', color: '#123456' },
      { id: '2', name: 'архив', color: '#654321' },
      { id: '3', name: 'работа', color: '#ABCDEF' },
    ] };
    return path.startsWith('/api/files/info') ? info : { items: [] };
  });
});
afterEach(() => vi.resetAllMocks());

describe('PropertiesModal', () => {
  it('показывает имя один раз, однострочное поле и цветные бейджи', async () => {
    render(<PropertiesModal fileId="file-1" />);
    expect(await screen.findByDisplayValue('отпуск')).toBeTruthy();
    expect(screen.getAllByText('document.txt')).toHaveLength(1);
    await waitFor(() => expect(screen.getByText('личное').closest('.tag-chip')?.getAttribute('style')).toContain('#123456'));
    expect(screen.getByLabelText('Имя для поиска').tagName).toBe('INPUT');
  });

  it('показывает поиск первым, исключает выбранные теги и сохраняет выбор', async () => {
    vi.mocked(apiPut).mockResolvedValueOnce({ alias: 'документ', tags: ['личное', 'архив'] });
    render(<PropertiesModal fileId="file-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Добавить тег' }));
    const search = screen.getByRole('searchbox', { name: 'Поиск тегов' });
    expect(search.closest('.tag-picker-menu')?.querySelector('input, button')).toBe(search);
    const options = screen.getByLabelText('Доступные теги');
    expect(within(options).queryByRole('button', { name: 'личное' })).toBeNull();
    fireEvent.change(search, { target: { value: 'АРХ' } });
    expect(within(options).queryByRole('button', { name: 'работа' })).toBeNull();
    fireEvent.click(await within(options).findByRole('button', { name: 'архив' }));
    expect(screen.queryByRole('searchbox')).toBeNull();
    fireEvent.change(screen.getByLabelText('Имя для поиска'), { target: { value: 'документ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(await screen.findByText('Сохранено')).toBeTruthy();
    expect(apiPut).toHaveBeenCalledWith('/api/files/file-1/search-metadata', { alias: 'документ', tags: ['личное', 'архив'] });
  });

  it('снимает тег, очищает алиас и сохраняет введённые данные при ошибке', async () => {
    vi.mocked(apiPut).mockRejectedValueOnce(new Error('Нет соединения'));
    render(<PropertiesModal fileId="file-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Снять тег личное' }));
    fireEvent.change(screen.getByLabelText('Имя для поиска'), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(await screen.findByText('Нет соединения')).toBeTruthy();
    expect(apiPut).toHaveBeenCalledWith('/api/files/file-1/search-metadata', { alias: '', tags: [] });
  });

  it('Escape закрывает только выбор тегов и возвращает фокус на плюс', async () => {
    const onClose = vi.fn();
    render(<PropertiesModal fileId="file-1" onClose={onClose} />);
    const plus = await screen.findByRole('button', { name: 'Добавить тег' });
    fireEvent.click(plus);
    fireEvent.keyDown(screen.getByRole('searchbox'), { key: 'Escape' });
    expect(screen.queryByRole('searchbox')).toBeNull();
    expect(document.activeElement).toBe(plus);
    expect(onClose).not.toHaveBeenCalled();
  });
});
