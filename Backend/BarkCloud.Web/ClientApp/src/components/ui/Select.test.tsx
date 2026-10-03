import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { Select } from './Select';

const OPTIONS = [{ value: 1, label: 'Низкий' }, { value: 2, label: 'Обычный' }, { value: 3, label: 'Высокий' }];

describe('Select', () => {
  it('показывает текущее значение и выбирает опцию мышью', () => {
    const onChange = vi.fn();
    render(<Select aria-label="Приоритет" value={2} options={OPTIONS} onChange={onChange} />);
    const box = screen.getByRole('combobox', { name: 'Приоритет' });
    expect(box.textContent).toBe('Обычный');
    fireEvent.click(box);
    expect(screen.getByRole('option', { name: 'Обычный' }).getAttribute('aria-selected')).toBe('true');
    fireEvent.click(screen.getByRole('option', { name: 'Высокий' }));
    expect(onChange).toHaveBeenCalledWith(3);
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('управляется клавиатурой и не вызывает onChange для того же значения', () => {
    const onChange = vi.fn();
    render(<Select aria-label="Приоритет" value={2} options={OPTIONS} onChange={onChange} />);
    const box = screen.getByRole('combobox', { name: 'Приоритет' });
    fireEvent.keyDown(box, { key: 'ArrowDown' });
    const list = screen.getByRole('listbox');
    fireEvent.keyDown(list, { key: 'ArrowUp' });
    fireEvent.keyDown(list, { key: 'Enter' });
    expect(onChange).toHaveBeenCalledWith(1);
    fireEvent.keyDown(box, { key: 'Enter' });
    fireEvent.keyDown(screen.getByRole('listbox'), { key: 'Escape' });
    expect(screen.queryByRole('listbox')).toBeNull();
    fireEvent.keyDown(box, { key: ' ' });
    fireEvent.keyDown(screen.getByRole('listbox'), { key: 'Enter' });
    expect(onChange).toHaveBeenCalledTimes(1);
  });
});
