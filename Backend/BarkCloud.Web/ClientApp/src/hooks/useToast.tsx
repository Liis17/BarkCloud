import React from 'react';
import { Icon } from '../components/Icon';

export type ToastKind = 'ok' | 'err';
export type ToastPush = (msg: string, kind?: ToastKind) => void;

interface ToastItem {
  id: string;
  msg: string;
  kind: ToastKind;
}

/** Тосты. Возвращает [node, push(msg, kind)]. */
export function useToast(): [React.ReactElement, ToastPush] {
  const [toasts, setToasts] = React.useState<ToastItem[]>([]);
  const push = React.useCallback<ToastPush>((msg, kind = 'ok') => {
    const id = Math.random().toString(36).slice(2);
    setToasts((t) => [...t, { id, msg, kind }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), 4200);
  }, []);
  const node = (
    <div className="toast-stack">
      {toasts.map((t) => (
        <div key={t.id} className={'toast ' + t.kind} role="status">
          {t.kind === 'ok' ? <Icon.check size={18} /> : <Icon.x size={18} />}
          <span>{t.msg}</span>
        </div>
      ))}
    </div>
  );
  return [node, push];
}
