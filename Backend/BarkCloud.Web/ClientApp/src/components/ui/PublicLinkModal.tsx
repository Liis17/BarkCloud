import React from 'react';
import { Modal } from './Modal';

/** Варианты срока действия публичной ссылки; `null` — бессрочно. */
export const PUBLIC_LINK_EXPIRY_OPTIONS: { days: number | null; label: string }[] = [
  { days: null, label: 'Бессрочно' },
  { days: 1, label: 'На 1 день' },
  { days: 7, label: 'На 7 дней' },
  { days: 30, label: 'На 30 дней' },
];

const DAY_MS = 24 * 60 * 60 * 1000;
const expiryFormat = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' });

export function formatLinkExpiry(iso: string): string {
  return expiryFormat.format(new Date(iso));
}

interface PublicLinkModalProps {
  title: string;
  onClose: () => void;
  /** expiresAt — ISO-время окончания или `null` для бессрочной ссылки. */
  onCreate: (expiresAt: string | null) => Promise<void>;
}

/** Выбор срока действия перед созданием публичной ссылки. */
export function PublicLinkModal({ title, onClose, onCreate }: PublicLinkModalProps) {
  const [days, setDays] = React.useState<number | null>(null);
  const [busy, setBusy] = React.useState(false);
  // Дата пересчитывается при выборе варианта; при создании берётся заново, чтобы срок шёл от момента нажатия.
  const preview = days === null ? null : new Date(Date.now() + days * DAY_MS).toISOString();

  async function create() {
    setBusy(true);
    try {
      await onCreate(days === null ? null : new Date(Date.now() + days * DAY_MS).toISOString());
    } catch {
      // Ошибку показывает вызывающий; диалог остаётся открытым для повтора.
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      title={title}
      onClose={busy ? undefined : onClose}
      actions={
        <>
          <button className="btn text" onClick={onClose} disabled={busy}>
            Отмена
          </button>
          <button className="btn primary" onClick={create} disabled={busy}>
            {busy ? '…' : 'Создать и скопировать'}
          </button>
        </>
      }
    >
      <div className="chip-row" role="radiogroup" aria-label="Срок действия">
        {PUBLIC_LINK_EXPIRY_OPTIONS.map((o) => (
          <button
            key={o.label}
            type="button"
            role="radio"
            aria-checked={days === o.days}
            className={'chip' + (days === o.days ? ' active' : '')}
            onClick={() => setDays(o.days)}
          >
            {o.label}
          </button>
        ))}
      </div>
      <p className="meta-line" style={{ marginTop: 16, fontSize: 14 }}>
        {preview ? (
          <>
            <span className="k">Действует до</span> <span style={{ color: 'var(--md-on-surface)' }}>{formatLinkExpiry(preview)}</span>
          </>
        ) : (
          'Ссылка будет работать, пока вы её не отзовёте'
        )}
      </p>
    </Modal>
  );
}

interface DialogRequest {
  title: string;
  create: (expiresAt: string | null) => Promise<void>;
}

let showDialog: ((req: DialogRequest | null) => void) | null = null;

/** Открыть диалог срока из любого места приложения (нужен смонтированный {@link PublicLinkDialogHost}). */
export function openPublicLinkDialog(req: DialogRequest): void {
  showDialog?.(req);
}

/** Единственный экземпляр диалога в оболочке приложения. */
export function PublicLinkDialogHost() {
  const [req, setReq] = React.useState<DialogRequest | null>(null);
  React.useEffect(() => {
    showDialog = setReq;
    return () => {
      if (showDialog === setReq) showDialog = null;
    };
  }, []);
  if (!req) return null;
  return (
    <PublicLinkModal
      title={req.title}
      onClose={() => setReq(null)}
      onCreate={async (expiresAt) => {
        await req.create(expiresAt);
        setReq(null);
      }}
    />
  );
}
