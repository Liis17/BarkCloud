import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TextAreaField, TextField } from './TextField';

describe('общие текстовые поля', () => {
  it('сохраняет нативные атрибуты, ref и обработчики input', () => {
    const ref = React.createRef<HTMLInputElement>();
    const onChange = vi.fn();
    render(<TextField
      ref={ref}
      id="account-email"
      name="email"
      label="Электронная почта"
      type="email"
      required
      minLength={4}
      maxLength={80}
      autoComplete="email"
      onChange={onChange}
    />);

    const input = screen.getByLabelText('Электронная почта') as HTMLInputElement;
    expect(input).toBe(ref.current);
    expect(input.id).toBe('account-email');
    expect(input.name).toBe('email');
    expect(input.type).toBe('email');
    expect(input.required).toBe(true);
    expect(input.minLength).toBe(4);
    expect(input.maxLength).toBe(80);
    expect(input.autocomplete).toBe('email');

    fireEvent.change(input, { target: { value: 'user@example.com' } });
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it('связывает hint и ошибку с полем, не заменяя переданный aria-describedby', () => {
    render(<TextField
      label="Пароль"
      aria-describedby="password-policy"
      hint="Не менее шести символов"
      error="Пароль не подходит"
      errorRole="alert"
    />);

    const input = screen.getByLabelText('Пароль');
    const describedBy = input.getAttribute('aria-describedby')!.split(' ');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(describedBy).toContain('password-policy');
    expect(describedBy).toContain(`${input.id}-hint`);
    expect(describedBy).toContain(`${input.id}-error`);
    expect(screen.getByText('Не менее шести символов').id).toBe(`${input.id}-hint`);
    expect(screen.getByRole('alert').id).toBe(`${input.id}-error`);
  });

  it('создаёт доступную textarea и передаёт multiline props/ref', () => {
    const ref = React.createRef<HTMLTextAreaElement>();
    const onChange = vi.fn();
    render(<TextAreaField ref={ref} label="Описание" name="bio" rows={4} maxLength={200} defaultValue="Профиль" onChange={onChange} />);

    const textarea = screen.getByLabelText('Описание') as HTMLTextAreaElement;
    expect(textarea).toBe(ref.current);
    expect(textarea.name).toBe('bio');
    expect(textarea.rows).toBe(4);
    expect(textarea.maxLength).toBe(200);
    expect(textarea.value).toBe('Профиль');
    fireEvent.change(textarea, { target: { value: 'Новое описание' } });
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it('рендерит trailing slot и сохраняет disabled/readOnly состояния', () => {
    render(<>
      <TextField label="Секрет" type="password" trailing={<button type="button">Показать</button>} disabled />
      <TextField label="Код" readOnly value="123456" />
    </>);

    expect((screen.getByLabelText('Секрет') as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByRole('button', { name: 'Показать' })).toBeTruthy();
    expect((screen.getByLabelText('Код') as HTMLInputElement).readOnly).toBe(true);
  });
});
