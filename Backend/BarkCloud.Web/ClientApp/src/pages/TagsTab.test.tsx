import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiGet, apiPost } from '../lib/api';
import { TagsTab } from './TagsTab';

vi.mock('../lib/api', () => ({ apiGet: vi.fn(), apiPost: vi.fn() }));
const tag = { id: '1', name: 'Личное', color: '#123456' };
afterEach(() => vi.resetAllMocks());

describe('TagsTab', () => {
  it('создаёт тег с названием и цветом, затем редактирует его', async () => {
    vi.mocked(apiGet).mockResolvedValue({ items: [] });
    vi.mocked(apiPost).mockResolvedValueOnce(tag).mockResolvedValueOnce({ ...tag, name: 'Архив', color: '#ABCDEF' });
    render(<TagsTab active />);
    await screen.findByText('Тегов пока нет. Создайте первую метку для файлов.');
    fireEvent.click(screen.getByRole('button', { name: 'Создать тег' }));
    fireEvent.change(screen.getByLabelText('Название тега'), { target: { value: 'Личное' } });
    fireEvent.change(screen.getByLabelText('Цвет тега'), { target: { value: '#123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await screen.findByRole('button', { name: 'Изменить тег Личное' });
    expect(apiPost).toHaveBeenCalledWith('/api/settings/tags/save', { id: '', name: 'Личное', color: '#123456' });
    fireEvent.click(screen.getByRole('button', { name: 'Изменить тег Личное' }));
    fireEvent.change(screen.getByLabelText('Название тега'), { target: { value: 'Архив' } });
    fireEvent.change(screen.getByLabelText('Цвет тега'), { target: { value: '#abcdef' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await screen.findByRole('button', { name: 'Изменить тег Архив' });
    expect(screen.queryByText('Личное')).toBeNull();
  });

  it('фильтрует список, подтверждает удаление и убирает тег', async () => {
    vi.mocked(apiGet).mockResolvedValue({ items: [tag] });
    vi.mocked(apiPost).mockResolvedValue(null);
    render(<TagsTab active />);
    await screen.findByText('Личное');
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'нет' } });
    expect(screen.getByText('Теги не найдены')).toBeTruthy();
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Удалить тег Личное' }));
    expect(apiPost).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Удалить' }));
    await waitFor(() => expect(screen.queryByText('Личное')).toBeNull());
    expect(apiPost).toHaveBeenCalledWith('/api/settings/tags/delete', { id: '1' });
  });

  it('сохраняет введённые данные при ошибке API', async () => {
    vi.mocked(apiGet).mockResolvedValue({ items: [tag] });
    vi.mocked(apiPost).mockRejectedValue(new Error('Тег уже существует'));
    render(<TagsTab active />);
    fireEvent.click(await screen.findByRole('button', { name: 'Изменить тег Личное' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Тег уже существует');
    expect(screen.getByDisplayValue('Личное')).toBeTruthy();
  });
});
