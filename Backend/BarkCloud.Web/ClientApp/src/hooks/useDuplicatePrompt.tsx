import React from 'react';
import { Modal } from '../components/ui/Modal';
import type { DuplicateLocation } from '../lib/api';

export type DuplicateDecision = 'skip' | 'upload' | 'skip-all' | 'upload-all';

interface PromptState {
  name: string;
  locations: DuplicateLocation[];
  resolve: (decision: DuplicateDecision) => void;
}

/**
 * Модалка «такой файл уже есть»: показывает имя файла и где он лежит, спрашивает,
 * загружать ли ещё одну копию. ask(...) возвращает решение пользователя.
 */
export function useDuplicatePrompt() {
  const [state, setState] = React.useState<PromptState | null>(null);

  const ask = React.useCallback(
    (name: string, locations: DuplicateLocation[]) =>
      new Promise<DuplicateDecision>((resolve) => setState({ name, locations, resolve })),
    [],
  );

  function answer(decision: DuplicateDecision) {
    state?.resolve(decision);
    setState(null);
  }

  const overlay = state ? (
    <Modal
      title="Такой файл уже есть"
      onClose={() => answer('skip')}
      actions={
        <>
          <button className="btn text" onClick={() => answer('skip')}>
            Пропустить
          </button>
          <button className="btn text" onClick={() => answer('skip-all')}>
            Пропустить все
          </button>
          <button className="btn outlined" onClick={() => answer('upload-all')}>
            Загрузить все
          </button>
          <button className="btn primary" onClick={() => answer('upload')}>
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
