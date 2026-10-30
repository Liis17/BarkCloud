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
    fireEvent.change(screen.getByRole('textbox', { name: 'HEX цвета тега' }), { target: { value: '#123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await screen.findByRole('button', { name: 'Изменить тег Личное' });
    expect(apiPost).toHaveBeenCalledWith('/api/settings/tags/save', { id: '', name: 'Личное', color: '#123456' });
    fireEvent.click(screen.getByRole('button', { name: 'Изменить тег Личное' }));
    fireEvent.change(screen.getByLabelText('Название тега'), { target: { value: 'Архив' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'HEX цвета тега' }), { target: { value: '#abcdef' } });
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

  it('не отправляет форму с невалидным HEX и сохраняет draft до исправления', async () => {
    vi.mocked(apiGet).mockResolvedValue({ items: [] });
    render(<TagsTab active />);
    await screen.findByText('Тегов пока нет. Создайте первую метку для файлов.');
    fireEvent.click(screen.getByRole('button', { name: 'Создать тег' }));
    fireEvent.change(screen.getByLabelText('Название тега'), { target: { value: 'Работа' } });
    const hex = screen.getByRole('textbox', { name: 'HEX цвета тега' });
    fireEvent.change(hex, { target: { value: '#12' } });
    expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.submit(document.getElementById('tag-editor-form')!);
    expect(apiPost).not.toHaveBeenCalled();
    expect(await screen.findByText('Введите цвет в формате #RRGGBB.')).toBeTruthy();
    expect((hex as HTMLInputElement).value).toBe('#12');
  });

  it('отменяет создание и редактирование без сохранения draft', async () => {
    vi.mocked(apiGet).mockResolvedValue({ items: [tag] });
    render(<TagsTab active />);
    const create = await screen.findByRole('button', { name: 'Создать тег' });
    create.focus();
    fireEvent.click(create);
    const name = screen.getByLabelText('Название тега');
    expect(document.activeElement).toBe(name);
    fireEvent.change(name, { target: { value: 'Черновик' } });
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(create);
    fireEvent.click(create);
    expect((screen.getByLabelText('Название тега') as HTMLInputElement).value).toBe('');
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));

    fireEvent.click(screen.getByRole('button', { name: 'Изменить тег Личное' }));
    fireEvent.change(screen.getByLabelText('Название тега'), { target: { value: 'Черновик' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить тег Личное' }));
    expect((screen.getByLabelText('Название тега') as HTMLInputElement).value).toBe('Личное');
    expect((screen.getByRole('textbox', { name: 'HEX цвета тега' }) as HTMLInputElement).value).toBe('#123456');
  });

  it('отключает picker и действия во время сохранения', async () => {
    vi.mocked(apiGet).mockResolvedValue({ items: [] });
    let resolveSave!: (value: typeof tag) => void;
    vi.mocked(apiPost).mockImplementation(() => new Promise((resolve) => { resolveSave = resolve as (value: typeof tag) => void; }));
    render(<TagsTab active />);
    await screen.findByText('Тегов пока нет. Создайте первую метку для файлов.');
    fireEvent.click(screen.getByRole('button', { name: 'Создать тег' }));
    fireEvent.change(screen.getByLabelText('Название тега'), { target: { value: 'Личное' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect((screen.getByRole('button', { name: 'Сохраняем…' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Выбрать цвет #6750A4' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Отмена' }) as HTMLButtonElement).disabled).toBe(true);
    resolveSave(tag);
    await screen.findByRole('button', { name: 'Изменить тег Личное' });
  });
});
