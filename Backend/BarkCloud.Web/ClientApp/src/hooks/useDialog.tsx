import React from 'react';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { RenameModal } from '../components/ui/RenameModal';

export interface ConfirmOptions {
  title?: string;
  message: React.ReactNode;
  confirmLabel?: string;
  danger?: boolean;
}

/** M3-замена window.confirm. Возвращает [node, confirm(opts) → Promise<boolean>]. */
export function useConfirm(): [React.ReactNode, (opts: ConfirmOptions) => Promise<boolean>] {
  const [req, setReq] = React.useState<(ConfirmOptions & { resolve: (ok: boolean) => void }) | null>(null);
  const confirm = React.useCallback(
    (opts: ConfirmOptions) => new Promise<boolean>((resolve) => setReq({ ...opts, resolve })),
    [],
  );
  const finish = (ok: boolean) => {
    req?.resolve(ok);
    setReq(null);
  };
  const node = req && (
    <ConfirmModal
      title={req.title}
      message={req.message}
      confirmLabel={req.confirmLabel}
      danger={req.danger}
      onClose={() => finish(false)}
      onConfirm={() => finish(true)}
    />
  );
  return [node, confirm];
}

export interface PromptOptions {
  title?: string;
  label?: string;
  initial?: string;
}

/** M3-замена window.prompt. Возвращает [node, prompt(opts) → Promise<string | null>]. */
export function usePrompt(): [React.ReactNode, (opts: PromptOptions) => Promise<string | null>] {
  const [req, setReq] = React.useState<(PromptOptions & { resolve: (v: string | null) => void }) | null>(null);
  const prompt = React.useCallback(
    (opts: PromptOptions) => new Promise<string | null>((resolve) => setReq({ ...opts, resolve })),
    [],
  );
  const finish = (v: string | null) => {
    req?.resolve(v);
    setReq(null);
  };
  const node = req && (
    <RenameModal
      title={req.title}
      label={req.label}
      initial={req.initial}
      onClose={() => finish(null)}
      onSave={(v) => finish(v)}
    />
  );
  return [node, prompt];
}
