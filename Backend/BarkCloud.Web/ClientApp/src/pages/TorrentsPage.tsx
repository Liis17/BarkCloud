import React from 'react';
import { useLocation } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { Modal } from '../components/ui/Modal';
import { EmptyState } from '../components/ui/EmptyState';
import { useToast } from '../hooks/useToast';
import { usePageHeader } from '../hooks/usePageHeader';
import { useTorrentStream, type Torrent, type TorrentFile } from '../hooks/useTorrentStream';
import * as T from '../lib/torrents';

function fmtBytes(n: number): string {
  if (!n || n < 0) return '0 Б';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${u[i]}`;
}
const fmtSpeed = (n: number) => (n > 0 ? `${fmtBytes(n)}/с` : '—');
function fmtEta(sec: number): string {
  if (sec < 0) return '—';
  if (sec < 60) return `${sec} с`;
  if (sec < 3600) return `${Math.round(sec / 60)} мин`;
  return `${Math.round(sec / 3600)} ч`;
}

const STATUS_RU: Record<string, string> = {
  metadata: 'Метаданные', downloading: 'Загрузка', seeding: 'Раздача',
  paused: 'Пауза', completed: 'Готово', error: 'Ошибка', unknown: '—',
};
const PRIORITY_OPTS = [
  { value: 0, label: 'Не качать' },
  { value: 1, label: 'Низкий' },
  { value: 2, label: 'Обычный' },
  { value: 3, label: 'Высокий' },
];

function ProgressBar({ value }: { value: number }) {
  return (
    <div style={{ height: 6, borderRadius: 4, background: 'var(--md-surface-variant)', overflow: 'hidden' }}>
      <div style={{ height: '100%', width: `${Math.round(value * 100)}%`, background: 'var(--md-primary)', transition: 'width .3s' }} />
    </div>
  );
}

function FilesPanel({ torrent, onToast }: { torrent: Torrent; onToast: (m: string, k?: 'ok' | 'err') => void }) {
  const [files, setFiles] = React.useState<TorrentFile[] | null>(null);

  const reload = React.useCallback(() => {
    T.listFiles(torrent.id).then((r) => setFiles(r.files)).catch((e) => onToast(e.message, 'err'));
  }, [torrent.id, onToast]);

  React.useEffect(() => { reload(); }, [reload]);

  const changePriority = async (index: number, priority: number) => {
    try { await T.setFilePriority(torrent.id, index, priority); reload(); }
    catch (e) { onToast((e as Error).message, 'err'); }
  };

  const importFile = async (index: number) => {
    try {
      const r = await T.importToCloud(torrent.id, undefined, index);
      onToast(`Импортировано в облако: ${r.files.length}`);
    } catch (e) { onToast((e as Error).message, 'err'); }
  };

  if (!files) return <div style={{ padding: 12, color: 'var(--md-on-surface-variant)' }}>Загрузка файлов…</div>;

  return (
    <div style={{ padding: '4px 0 8px' }}>
      {files.map((f) => (
        <div key={f.index} style={{ display: 'grid', gridTemplateColumns: '1fr 90px 150px 120px', gap: 12, alignItems: 'center', padding: '6px 12px' }}>
          <div style={{ minWidth: 0 }}>
            <div style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.path}</div>
            <div style={{ marginTop: 4 }}><ProgressBar value={f.progress} /></div>
          </div>
          <div style={{ fontSize: 13, color: 'var(--md-on-surface-variant)' }}>{fmtBytes(f.size)}</div>
          <select value={f.priority} onChange={(e) => changePriority(f.index, Number(e.target.value))}
            style={{ padding: '4px 6px', borderRadius: 6, border: '1px solid var(--md-outline)', background: 'var(--md-surface)', color: 'inherit' }}>
            {PRIORITY_OPTS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
            <a className="icon-btn" href={T.downloadUrl(torrent.id, f.index)} title="Скачать" target="_blank" rel="noreferrer"><Icon.download size={18} /></a>
            {f.progress >= 1 && <button className="icon-btn" title="В облако" onClick={() => importFile(f.index)}><Icon.cloud size={18} /></button>}
          </div>
        </div>
      ))}
    </div>
  );
}

function TorrentRow({ t, onToast, forceOpen = false }: { t: Torrent; onToast: (m: string, k?: 'ok' | 'err') => void; forceOpen?: boolean }) {
  const [open, setOpen] = React.useState(false);
  const paused = t.status === 'paused';

  React.useEffect(() => {
    if (forceOpen) setOpen(true);
  }, [forceOpen]);

  const act = (fn: Promise<unknown>, ok?: string) =>
    fn.then(() => ok && onToast(ok)).catch((e) => onToast((e as Error).message, 'err'));

  const remove = () => {
    const del = window.confirm('Удалить торрент? Нажмите OK — удалить и файлы с диска, Отмена — оставить запись.');
    // OK → удалить с файлами; для «оставить файлы» пользователь выбирает второй диалог
    if (del) act(T.removeTorrent(t.id, true), 'Торрент и файлы удалены');
  };

  return (
    <div style={{ border: '1px solid var(--md-outline-variant)', borderRadius: 12, padding: 14, marginBottom: 10, background: 'var(--md-surface)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <button className="icon-btn" onClick={() => setOpen((v) => !v)} title="Файлы">
          <Icon.chev size={18} style={{ transform: open ? 'rotate(90deg)' : 'none', transition: 'transform .2s' }} />
        </button>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12 }}>
            <div style={{ fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{t.name || t.infoHash}</div>
            <span style={{ fontSize: 12, padding: '2px 8px', borderRadius: 20, background: 'var(--md-surface-variant)', whiteSpace: 'nowrap' }}>{STATUS_RU[t.status] ?? t.status}</span>
          </div>
          <div style={{ margin: '8px 0 6px' }}><ProgressBar value={t.progress} /></div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 16px', fontSize: 12, color: 'var(--md-on-surface-variant)' }}>
            <span>{Math.round(t.progress * 100)}% из {fmtBytes(t.totalSize)}</span>
            <span>↓ {fmtSpeed(t.downloadSpeed)}</span>
            <span>↑ {fmtSpeed(t.uploadSpeed)}</span>
            <span>сиды {t.seeds} · личи {t.leechers}</span>
            <span>скачано {fmtBytes(t.downloaded)} · отдано {fmtBytes(t.uploaded)}</span>
            <span>ratio {t.ratio.toFixed(2)}</span>
            {t.status === 'downloading' && <span>ETA {fmtEta(t.etaSeconds)}</span>}
          </div>
        </div>
        <div style={{ display: 'flex', gap: 6 }}>
          {paused
            ? <button className="icon-btn" title="Возобновить" onClick={() => act(T.resumeTorrent(t.id))}><Icon.play size={18} /></button>
            : <button className="icon-btn" title="Пауза" onClick={() => act(T.pauseTorrent(t.id))}><Icon.pause size={18} /></button>}
          <button className="icon-btn" title="Удалить" onClick={remove}><Icon.trash size={18} /></button>
        </div>
      </div>
      {open && <FilesPanel torrent={t} onToast={onToast} />}
    </div>
  );
}

function AddModal({ onClose, onToast }: { onClose: () => void; onToast: (m: string, k?: 'ok' | 'err') => void }) {
  const [magnet, setMagnet] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const fileRef = React.useRef<HTMLInputElement>(null);

  const submitMagnet = async () => {
    if (!magnet.trim()) return;
    setBusy(true);
    try { await T.addMagnet(magnet.trim()); onToast('Торрент добавлен'); onClose(); }
    catch (e) { onToast((e as Error).message, 'err'); }
    finally { setBusy(false); }
  };

  const submitFile = async (file: File) => {
    setBusy(true);
    try { await T.addTorrentFile(file); onToast('Торрент добавлен'); onClose(); }
    catch (e) { onToast((e as Error).message, 'err'); }
    finally { setBusy(false); }
  };

  return (
    <Modal title="Добавить торрент" onClose={onClose}
      actions={<>
        <button className="btn" onClick={onClose}>Отмена</button>
        <button className="btn primary" disabled={busy || !magnet.trim()} onClick={submitMagnet}>Добавить</button>
      </>}>
      <label style={{ display: 'block', fontSize: 13, marginBottom: 6 }}>Magnet-ссылка</label>
      <input value={magnet} onChange={(e) => setMagnet(e.target.value)} placeholder="magnet:?xt=urn:btih:…" autoFocus
        style={{ width: '100%', padding: '10px 12px', borderRadius: 8, border: '1px solid var(--md-outline)', background: 'var(--md-surface)', color: 'inherit' }} />
      <div style={{ margin: '16px 0 8px', color: 'var(--md-on-surface-variant)', fontSize: 13 }}>или загрузите .torrent-файл:</div>
      <input ref={fileRef} type="file" accept=".torrent" style={{ display: 'none' }}
        onChange={(e) => { const f = e.target.files?.[0]; if (f) submitFile(f); }} />
      <button className="btn" disabled={busy} onClick={() => fileRef.current?.click()}>
        <Icon.upload size={16} /> Выбрать .torrent
      </button>
    </Modal>
  );
}

export function TorrentsPage() {
  const location = useLocation();
  const openTorrentId = new URLSearchParams(location.search).get('open') || '';
  const { torrents } = useTorrentStream();
  const [toastNode, push] = useToast();
  const [adding, setAdding] = React.useState(false);

  usePageHeader(() => ({
    title: 'Торренты',
    actions: (
      <button className="btn primary" onClick={() => setAdding(true)}>
        <Icon.plus size={18} /> Добавить
      </button>
    ),
  }), []);

  return (
    <div style={{ padding: 16, maxWidth: 1100, margin: '0 auto' }}>
      {torrents.length === 0
        ? <EmptyState icon="torrent" title="Нет торрентов" hint="Добавьте magnet-ссылку или .torrent-файл" />
        : torrents.map((t) => <TorrentRow key={t.id} t={t} onToast={push} forceOpen={t.id === openTorrentId} />)}

      {adding && <AddModal onClose={() => setAdding(false)} onToast={push} />}
      {toastNode}
    </div>
  );
}
