import React from 'react';
import { Modal } from '../components/ui/Modal';
import type { DuplicateLocation } from '../lib/api';

interface PromptState {
  name: string;
  locations: DuplicateLocation[];
  resolve: (proceed: boolean) => void;
}

/**
 * Модалка «такой файл уже есть»: показывает имя файла и где он лежит, спрашивает,
 * загружать ли ещё одну копию. ask(...) возвращает Promise<boolean> — решение пользователя.
 */
export function useDuplicatePrompt() {
  const [state, setState] = React.useState<PromptState | null>(null);

  const ask = React.useCallback(
    (name: string, locations: DuplicateLocation[]) =>
      new Promise<boolean>((resolve) => setState({ name, locations, resolve })),
    [],
  );

  function answer(proceed: boolean) {
    state?.resolve(proceed);
    setState(null);
  }

  const overlay = state ? (
    <Modal
      title="Такой файл уже есть"
      onClose={() => answer(false)}
      actions={
        <>
          <button className="btn text" onClick={() => answer(false)}>
            Пропустить
          </button>
          <button className="btn primary" onClick={() => answer(true)}>
            Загрузить ещё раз
          </button>
        </>
      }
    >
      <div className="confirm-msg">
        <p>
          Файл <b>«{state.name}»</b> уже есть в вашем облаке.
        </p>
        {state.locations.length > 0 && (
          <ul className="dup-locations">
            {state.locations.map((l) => (
              <li key={l.entryId}>
                {l.name} — {l.directoryName || 'Корневая папка'}
              </li>
            ))}
          </ul>
        )}
        <p>Загрузить ещё одну копию?</p>
      </div>
    </Modal>
  ) : null;

  return { ask, overlay };
}
