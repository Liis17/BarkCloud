import { describe, expect, it } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { useConfirm, usePrompt } from './useDialog';

function ConfirmHost({ onResult }: { onResult: (v: boolean) => void }) {
  const [node, confirm] = useConfirm();
  return (
    <>
      <button onClick={async () => onResult(await confirm({ title: 'Удалить?', message: 'Точно', confirmLabel: 'Удалить' }))}>open</button>
      {node}
    </>
  );
}

function PromptHost({ onResult }: { onResult: (v: string | null) => void }) {
  const [node, prompt] = usePrompt();
  return (
    <>
      <button onClick={async () => onResult(await prompt({ title: 'Название', initial: 'Ключ' }))}>open</button>
      {node}
    </>
  );
}

describe('useConfirm', () => {
  it('resolves true on confirm and closes the dialog', async () => {
    const results: boolean[] = [];
    render(<ConfirmHost onResult={(v) => results.push(v)} />);
    fireEvent.click(screen.getByText('open'));
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Удалить' })));
    expect(results).toEqual([true]);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('resolves false on cancel', async () => {
    const results: boolean[] = [];
    render(<ConfirmHost onResult={(v) => results.push(v)} />);
    fireEvent.click(screen.getByText('open'));
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Отмена' })));
    expect(results).toEqual([false]);
  });
});

describe('usePrompt', () => {
  it('resolves the entered value, or null on cancel', async () => {
    const results: (string | null)[] = [];
    render(<PromptHost onResult={(v) => results.push(v)} />);
    fireEvent.click(screen.getByText('open'));
    fireEvent.change(screen.getByRole('textbox'), { target: { value: ' YubiKey ' } });
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })));
    fireEvent.click(screen.getByText('open'));
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Отмена' })));
    expect(results).toEqual(['YubiKey', null]);
  });
});
