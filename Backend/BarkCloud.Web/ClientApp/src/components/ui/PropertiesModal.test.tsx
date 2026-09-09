import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiGet, apiPut } from '../../lib/api';
import type { FileInfo } from '../../lib/types';
import { PropertiesModal } from './PropertiesModal';

vi.mock('../../lib/api', () => ({
  apiGet: vi.fn(),
  apiPut: vi.fn(),
}));

const fileInfo: FileInfo = {
  id: 'file-1',
  name: 'document.txt',
  ext: 'txt',
  kind: 'document',
  iconKind: 'doc',
  size: 42,
  sizeLabel: '42 Б',
  width: 0,
  height: 0,
  previews: [],
  createdAt: null,
  uploadedAt: null,
  searchAlias: 'отпуск',
  tags: ['личное'],
};

const apiGetMock = vi.mocked(apiGet);
const apiPutMock = vi.mocked(apiPut);

beforeEach(() => {
  apiGetMock.mockResolvedValueOnce(fileInfo).mockResolvedValueOnce({ items: [] });
});

afterEach(() => vi.resetAllMocks());

describe('PropertiesModal', () => {
  it('показывает заполненные данные поиска в списке и раскрывает редакторы кнопками', async () => {
    render(<PropertiesModal fileId="file-1" />);

    expect(await screen.findByText('отпуск')).toBeTruthy();
    expect(screen.getByText('личное')).toBeTruthy();
    expect(screen.queryByPlaceholderText('Например, важный документ')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Имя для поиска' }));
    expect(screen.getByPlaceholderText('Например, важный документ')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Теги' }));
    expect(screen.queryByPlaceholderText('Например, важный документ')).toBeNull();
    expect(screen.getByPlaceholderText('Добавить тег')).toBeTruthy();
  });

  it('обновляет верхний список после сохранения новых данных поиска', async () => {
    apiPutMock.mockResolvedValueOnce({ alias: 'важный документ', tags: ['личное', 'архив'] });
    render(<PropertiesModal fileId="file-1" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Имя для поиска' }));
    fireEvent.change(screen.getByPlaceholderText('Например, важный документ'), { target: { value: 'важный документ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    expect(await screen.findByText('Сохранено')).toBeTruthy();
    expect(screen.getByText('важный документ')).toBeTruthy();
    expect(screen.getByText('личное, архив')).toBeTruthy();
  });

  it('не добавляет пустые алиас и теги в список информации', async () => {
    apiGetMock.mockReset();
    apiGetMock.mockResolvedValueOnce({ ...fileInfo, searchAlias: '', tags: [] }).mockResolvedValueOnce({ items: [] });
    render(<PropertiesModal fileId="file-1" />);

    expect(await screen.findByText('document.txt')).toBeTruthy();
    const labels = Array.from(document.querySelectorAll('.prop-grid .pk')).map((element) => element.textContent);
    expect(labels).not.toContain('Имя для поиска');
    expect(labels).not.toContain('Теги');
  });
});
