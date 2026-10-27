import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Modal } from './Modal';
import { TextField } from './TextField';

function ModalHarness() {
  const [open, setOpen] = React.useState(false);
  return <>
    <button type="button" onClick={() => setOpen(true)}>Открыть</button>
    {open && <Modal title="Проверка" onClose={() => setOpen(false)} actions={<button type="button">Последнее действие</button>}>
      <form><TextField label="Название" autoFocus /><button type="button">Действие в форме</button></form>
    </Modal>}
  </>;
}

describe('Modal', () => {
  it('сохраняет autofocus, циклический Tab, Escape и возврат фокуса', () => {
    render(<React.StrictMode><ModalHarness /></React.StrictMode>);
    const opener = screen.getByRole('button', { name: 'Открыть' });
    opener.focus();
    fireEvent.click(opener);
    const name = screen.getByLabelText('Название');
    expect(document.activeElement).toBe(name);

    const close = screen.getByRole('button', { name: 'Закрыть' });
    close.focus();
    fireEvent.keyDown(window, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Последнее действие' }));
    fireEvent.keyDown(window, { key: 'Tab' });
    expect(document.activeElement).toBe(close);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });
});
