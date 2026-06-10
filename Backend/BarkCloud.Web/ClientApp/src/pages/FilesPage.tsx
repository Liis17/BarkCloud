import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { Modal } from '../components/ui/Modal';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { PropertiesModal } from '../components/ui/PropertiesModal';
import { ShareWithUserModal } from '../components/ui/ShareWithUserModal';
import { MoveToFolderModal } from '../components/ui/MoveToFolderModal';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { useContextMenu, type ContextItem } from '../components/ui/ContextMenu';
import { SelectionBar } from '../components/ui/SelectionBar';
import { useToast } from '../hooks/useToast';
import { useAlbumMembership } from '../hooks/useAlbumMembership';
import { useFileDrop } from '../hooks/useFileDrop';
import { useSelection } from '../hooks/useSelection';
import { usePageHeader } from '../hooks/usePageHeader';
import { pickDocumentIcon } from '../hooks/useDocumentHead';
import { useUploadActions } from '../hooks/useUploadManager';
import { DynamicFoldersStrip } from '../components/dynamic-folders/DynamicFoldersStrip';
import { DynamicFolderDetail } from '../components/dynamic-folders/DynamicFolderDetail';
import { DynamicFolderFormModal } from '../components/dynamic-folders/DynamicFolderFormModal';
import { apiGet, apiPost, pickFiles } from '../lib/api';
import { createShare, createFolderShare } from '../lib/share';
import type { Album, CardFile, DirInfo, DynamicFolder, Entry, Listing } from '../lib/types';

const ruDate = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
function fmtDate(iso: string | null): string {
  return iso ? ruDate.format(new Date(iso)) : '—';
}
function fmtFull(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString('ru-RU') : '—';
}
function kindLabel(k: string | undefined): string {
  return k === 'photo' ? 'фото' : k === 'video' ? 'видео' : k === 'audio' ? 'аудио' : k === 'document' ? 'документ' : 'файл';
}

type RenameTarget = { isDir: boolean; target: DirInfo | Entry };

function DirRow({ dir, onOpen, onRename, onDelete, onMenu }: {
  dir: DirInfo;
  onOpen: (d: DirInfo) => void;
  onRename: (d: DirInfo) => void;
  onDelete: (d: DirInfo) => void;
  onMenu: (e: React.MouseEvent, d: DirInfo) => void;
}) {
  return (
    <tr onClick={() => onOpen(dir)} onContextMenu={(e) => onMenu(e, dir)}>
      <td className="selcell" />
      <td className="name">
        <div className="file-icon folder">DIR</div>
        <div className="file-name-col">
          <div className="fn">{dir.name}</div>
          <div className="meta">Папка</div>
        </div>
      </td>
      <td className="size">—</td>
      <td className="modified">{fmtDate(dir.updatedAt)}</td>
      <td>
        <span className="row-actions">
          <button title="Переименовать" onClick={(e) => { e.stopPropagation(); onRename(dir); }}>
            <Icon.pencil size={18} />
          </button>
          <button title="Удалить" onClick={(e) => { e.stopPropagation(); onDelete(dir); }}>
            <Icon.trash size={18} />
          </button>
        </span>
      </td>
    </tr>
  );
}

function FileRow({ entry, selected, bulkChecked, onBulkToggle, onSelect, onRename, onDelete, onDownload, onMenu }: {
  entry: Entry;
  selected: boolean;
  bulkChecked: boolean;
  onBulkToggle: (e: Entry, shift: boolean) => void;
  onSelect: (e: Entry) => void;
  onRename: (e: Entry) => void;
  onDelete: (e: Entry) => void;
  onDownload: (e: Entry) => void;
  onMenu: (e: React.MouseEvent, entry: Entry) => void;
}) {
  const m = entry.media;
  return (
    <tr className={(selected ? 'selected' : '') + (bulkChecked ? ' checked' : '')} onClick={() => onSelect(entry)} onContextMenu={(e) => onMenu(e, entry)}>
      <td className="selcell" onClick={(e) => e.stopPropagation()}>
        <input
          type="checkbox"
          checked={bulkChecked}
          onChange={() => {}}
          onClick={(e) => onBulkToggle(entry, e.shiftKey)}
        />
      </td>
      <td className="name">
        <div className={'file-icon ' + (m?.iconKind || 'doc')}>{m?.ext || 'FILE'}</div>
        <div className="file-name-col">
          <div className="fn">{entry.name}</div>
          <div className="meta">{kindLabel(m?.kind)}</div>
        </div>
      </td>
      <td className="size">{m?.sizeLabel || '—'}</td>
      <td className="modified">{fmtDate(entry.createdAt)}</td>
      <td>
        <span className="row-actions">
          <button title="Скачать" onClick={(e) => { e.stopPropagation(); onDownload(entry); }}>
            <Icon.download size={18} />
          </button>
          <button title="Переименовать" onClick={(e) => { e.stopPropagation(); onRename(entry); }}>
            <Icon.pencil size={18} />
          </button>
          <button title="Удалить" onClick={(e) => { e.stopPropagation(); onDelete(entry); }}>
            <Icon.trash size={18} />
          </button>
        </span>
      </td>
    </tr>
  );
}

function Inspector({ entry, onOpen, onRename, onDelete, onDownload }: {
  entry: Entry | null;
  onOpen: (m: CardFile) => void;
  onRename: (e: Entry) => void;
  onDelete: (e: Entry) => void;
  onDownload: (e: Entry) => void;
}) {
  if (!entry) {
    return (
      <div className="insp-empty">
        <Icon.file size={40} />
        <div>Выберите файл, чтобы увидеть детали и превью</div>
      </div>
    );
  }
  const m = entry.media;
  const isMedia = m?.kind === 'photo' || m?.kind === 'video';
  const hasPreview = (m?.previews || []).length > 0;
  return (
    <div>
      <div className="insp-preview">
        {isMedia && hasPreview ? (
          <MediaThumb media={m} sizes="360px" />
        ) : (
          <div className={'file-icon big ' + (m?.iconKind || 'doc')}>{m?.ext || 'FILE'}</div>
        )}
        <div className="pin">
          {m?.ext || 'FILE'}
          {m?.kind === 'video' ? ' · видео' : ''}
        </div>
      </div>
      <div className="insp-name">{entry.name}</div>

      <div className="insp-actions">
        <button className="btn primary" onClick={() => onDownload(entry)}>
          <Icon.download size={16} /> Скачать
        </button>
        {isMedia && m && (
          <button className="btn outlined" onClick={() => onOpen(m)}>
            <Icon.eye size={16} /> Открыть
          </button>
        )}
        <button className="btn outlined" onClick={() => onRename(entry)}>
          <Icon.pencil size={16} /> Переименовать
        </button>
        <button className="btn outlined" onClick={() => onDelete(entry)}>
          <Icon.trash size={16} /> Удалить
        </button>
      </div>

      <div className="insp-section">
        <h4>Детали</h4>
        <dl className="kv-list">
          <dt>Тип</dt>
          <dd>
            {m?.ext || '—'}
            {m?.kind && m.kind !== 'other' ? ` · ${kindLabel(m.kind)}` : ''}
          </dd>
          {m?.sizeLabel && (
            <>
              <dt>Размер</dt>
              <dd>{m.sizeLabel}</dd>
            </>
          )}
          {!!m?.width && m.width > 0 && (
            <>
              <dt>Размерность</dt>
              <dd>
                {m.width} × {m.height}
              </dd>
            </>
          )}
          <dt>Добавлен</dt>
          <dd>{fmtFull(entry.createdAt)}</dd>
          {m?.uploadedAt && (
            <>
              <dt>Загружен</dt>
              <dd>{fmtFull(m.uploadedAt)}</dd>
            </>
          )}
        </dl>
      </div>
    </div>
  );
}

export function FilesPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const navState = (location.state || {}) as { stack?: { id: string; name: string }[]; selectEntryId?: string };
  const searchQuery = (new URLSearchParams(location.search).get('q') || '').trim();
  const [stack, setStack] = React.useState<{ id: string; name: string }[]>(navState.stack || []);
  const pendingSelect = React.useRef<string | null>(navState.selectEntryId || null);
  const [listing, setListing] = React.useState<Listing | null>(null);
  const [sel, setSel] = React.useState<Entry | null>(null);
  const [lightbox, setLightbox] = React.useState<CardFile | null>(null);
  const [creating, setCreating] = React.useState(false);
  const [renaming, setRenaming] = React.useState<RenameTarget | null>(null);
  const [name, setName] = React.useState('');
  const [albums, setAlbums] = React.useState<Album[]>([]);
  const [props, setProps] = React.useState<CardFile | null>(null);
  const [shareWith, setShareWith] = React.useState<Entry | null>(null);
  const [shareDirWith, setShareDirWith] = React.useState<DirInfo | null>(null);
  const [confirmDel, setConfirmDel] = React.useState<RenameTarget | null>(null);
  const [bulkConfirm, setBulkConfirm] = React.useState(false);
  const [bulkMoving, setBulkMoving] = React.useState(false);
  const [smartFolders, setSmartFolders] = React.useState<DynamicFolder[]>([]);
  const [openSmart, setOpenSmart] = React.useState<DynamicFolder | null>(null);
  const [creatingSmart, setCreatingSmart] = React.useState(false);
  const [toastNode, toast] = useToast();
  const { enqueue, attachVersion } = useUploadActions();
  const { menu, openAt } = useContextMenu();
  const membership = useAlbumMembership(albums);
  const { over, dropHandlers } = useFileDrop((f) => doUpload(f));
  const fsel = useSelection();

  const currentDir = stack.length ? stack[stack.length - 1].id : '';
  const selectedIconUrl = pickDocumentIcon(sel?.media);
  const documentTitle = sel
    ? sel.name
    : openSmart
      ? openSmart.name
      : searchQuery
        ? `Поиск: ${searchQuery}`
        : stack.length
          ? stack[stack.length - 1].name
          : 'Файлы';
  const documentIconUrl = sel ? selectedIconUrl : openSmart?.coverUrl || null;

  const load = React.useCallback(() => {
    setListing(null);
    setSel(null);
    fsel.clear();
    // Режим поиска: результаты по имени (по всему облаку), без подпапок.
    if (searchQuery) {
      apiGet<{ files: Entry[] }>('/api/cloud/search?q=' + encodeURIComponent(searchQuery))
        .then((d) => setListing({ dirs: [], files: d.files || [] }))
        .catch((e) => {
          toast((e as Error).message, 'err');
          setListing({ dirs: [], files: [] });
        });
      return;
    }
    apiGet<Listing>('/api/cloud/list?dir=' + encodeURIComponent(currentDir))
      .then((d) => {
        setListing(d);
        if (pendingSelect.current) {
          const hit = d.files.find((e) => e.entryId === pendingSelect.current);
          pendingSelect.current = null;
          if (hit) setSel(hit);
        }
      })
      .catch((e) => {
        toast((e as Error).message, 'err');
        setListing({ dirs: [], files: [] });
      });
  }, [currentDir, searchQuery, toast, fsel.clear]);
  React.useEffect(load, [load]);
  const loadRef = React.useRef(load);
  loadRef.current = load;
  React.useEffect(() => { loadRef.current(); }, [attachVersion]);

  const loadAlbums = React.useCallback(() => {
    apiGet<{ albums: Album[] }>('/api/albums')
      .then((d) => setAlbums(d.albums || []))
      .catch(() => {});
  }, []);
  React.useEffect(() => {
    loadAlbums();
  }, [loadAlbums]);

  const loadSmartFolders = React.useCallback(() => {
    apiGet<{ folders: DynamicFolder[] }>('/api/dynamic-folders')
      .then((d) => setSmartFolders(d.folders || []))
      .catch(() => {});
  }, []);
  React.useEffect(() => {
    loadSmartFolders();
  }, [loadSmartFolders]);
  React.useEffect(() => {
    membership.ensureLoaded();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [albums]);

  const openDir = (dir: DirInfo) => setStack((s) => [...s, { id: dir.id, name: dir.name }]);
  const gotoIndex = (i: number) => setStack((s) => s.slice(0, i + 1));

  const startRename = (target: DirInfo | Entry, isDir: boolean) => {
    setName(target.name);
    setRenaming({ isDir, target });
  };
  const requestDelete = (target: DirInfo | Entry, isDir: boolean) => setConfirmDel({ isDir, target });

  async function createDir() {
    const n = name.trim();
    if (!n) return;
    try {
      await apiPost('/api/cloud/dir', { parentId: currentDir, name: n });
      setCreating(false);
      load();
      toast('Папка создана');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function doRename() {
    const n = name.trim();
    if (!n || !renaming) return;
    try {
      if (renaming.isDir) await apiPost('/api/cloud/dir/rename', { id: (renaming.target as DirInfo).id, name: n });
      else await apiPost('/api/cloud/entry/rename', { entryId: (renaming.target as Entry).entryId, name: n });
      setRenaming(null);
      load();
      toast('Переименовано');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function doConfirmDelete() {
    if (!confirmDel) return;
    const { isDir, target } = confirmDel;
    try {
      if (isDir) {
        await apiPost('/api/cloud/dir/delete', { id: (target as DirInfo).id });
        toast('Папка удалена, файлы в корзине');
      } else {
        await apiPost('/api/cloud/entry/delete', { entryId: (target as Entry).entryId });
        toast('Перемещено в корзину');
      }
      setConfirmDel(null);
      load();
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function download(entry: Entry) {
    try {
      const d = await apiGet<{ urls: Record<string, string | null> }>('/api/files/download?ids=' + encodeURIComponent(entry.fileId));
      const url = d.urls && d.urls[entry.fileId];
      if (url) window.open(url, '_blank');
      else toast('Ссылка недоступна', 'err');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function copyLink(fileId: string) {
    try {
      const d = await apiGet<{ urls: Record<string, string | null> }>('/api/files/download?ids=' + encodeURIComponent(fileId));
      const url = d.urls && d.urls[fileId];
      if (!url) throw new Error('Ссылка недоступна');
      await navigator.clipboard.writeText(url);
      toast('Ссылка скопирована (временная)');
    } catch (e) {
      toast((e as Error).message || 'Не удалось скопировать', 'err');
    }
  }
  async function addToAlbum(fileId: string, albumId: string) {
    try {
      await apiPost('/api/albums/items/add', { album: albumId, fileIds: [fileId] });
      membership.addLocal(fileId, albumId);
      loadAlbums();
      toast('Добавлено в альбом');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function removeFromAlbum(fileId: string, albumId: string) {
    try {
      await apiPost('/api/albums/items/remove', { album: albumId, fileIds: [fileId] });
      membership.removeLocal(fileId, albumId);
      loadAlbums();
      toast('Убрано из альбома');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  function fileMenu(entry: Entry): ContextItem[] {
    const m = entry.media;
    const isMedia = m?.kind === 'photo' || m?.kind === 'video';
    const inAlbums = membership.of(entry.fileId);
    const available = albums.filter((a) => !inAlbums.has(a.id));
    const present = albums.filter((a) => inAlbums.has(a.id));
    const out: ContextItem[] = [
      { label: 'Копировать ссылку', icon: 'link', onClick: () => copyLink(entry.fileId) },
      { label: 'Создать публичную ссылку', icon: 'share', onClick: () => createShare(entry.fileId, entry.name, toast) },
      { label: 'Поделиться с пользователем', icon: 'user', onClick: () => setShareWith(entry) },
      { label: 'Переименовать', icon: 'pencil', onClick: () => startRename(entry, false) },
    ];
    if (isMedia) {
      out.push({
        label: 'Добавить в альбом',
        icon: 'plus',
        submenu: available.length
          ? available.map((a) => ({ label: a.name, onClick: () => addToAlbum(entry.fileId, a.id) }))
          : [{ label: albums.length ? 'Уже во всех альбомах' : 'Нет альбомов', disabled: true }],
      });
      if (present.length)
        out.push({ label: 'Удалить из альбома', icon: 'x', submenu: present.map((a) => ({ label: a.name, onClick: () => removeFromAlbum(entry.fileId, a.id) })) });
    }
    if (m) out.push({ label: 'Свойства', icon: 'info', onClick: () => setProps(m) });
    out.push({ divider: true });
    out.push({ label: 'Удалить', icon: 'trash', danger: true, onClick: () => requestDelete(entry, false) });
    return out;
  }
  function dirMenu(dir: DirInfo): ContextItem[] {
    return [
      { label: 'Сделать папку публичной', icon: 'share', onClick: () => createFolderShare(dir.id, dir.name, toast) },
      { label: 'Поделиться с пользователем', icon: 'user', onClick: () => setShareDirWith(dir) },
      { label: 'Переименовать', icon: 'pencil', onClick: () => startRename(dir, true) },
      { divider: true },
      { label: 'Удалить', icon: 'trash', danger: true, onClick: () => requestDelete(dir, true) },
    ];
  }
  async function doUpload(dropped?: File[]) {
    const files = dropped && dropped.length ? dropped : await pickFiles({});
    if (!files.length) return;
    enqueue(files, { dir: currentDir });
  }

  async function bulkDelete() {
    const chosen = (listing?.files || []).filter((e) => fsel.has(e.entryId));
    let ok = 0;
    for (const e of chosen) {
      try {
        await apiPost('/api/cloud/entry/delete', { entryId: e.entryId });
        ok++;
      } catch (err) {
        toast(`«${e.name}»: ${(err as Error).message}`, 'err');
      }
    }
    setBulkConfirm(false);
    fsel.clear();
    if (ok) {
      toast(`Перемещено в корзину: ${ok}`);
      load();
    }
  }
  async function bulkMove(targetDir: string) {
    const chosen = (listing?.files || []).filter((e) => fsel.has(e.entryId));
    let ok = 0;
    for (const e of chosen) {
      try {
        await apiPost('/api/cloud/entry/move', { entryId: e.entryId, dir: targetDir });
        ok++;
      } catch (err) {
        toast(`«${e.name}»: ${(err as Error).message}`, 'err');
      }
    }
    setBulkMoving(false);
    fsel.clear();
    if (ok) {
      toast(`Перемещено: ${ok}`);
      load();
    }
  }
  async function bulkCopyLinks() {
    const ids = (listing?.files || []).filter((e) => fsel.has(e.entryId)).map((e) => e.fileId);
    try {
      const d = await apiGet<{ urls: Record<string, string | null> }>('/api/files/download?ids=' + encodeURIComponent(ids.join(',')));
      const urls = ids.map((id) => d.urls && d.urls[id]).filter((u): u is string => !!u);
      if (!urls.length) throw new Error('Ссылки недоступны');
      await navigator.clipboard.writeText(urls.join('\n'));
      toast(`Скопировано ссылок: ${urls.length} (временные)`);
    } catch (e) {
      toast((e as Error).message || 'Не удалось скопировать', 'err');
    }
  }

  usePageHeader(
    () => ({
      title: 'Файлы',
      documentTitle,
      documentIconUrl,
      kicker: (
        <>
          <span>Библиотека</span>
          <span className="sep">/</span>
          <span className="cur">Файлы</span>
        </>
      ),
      contentClass: 'content-flush',
      actions: (
        <>
          <button className="btn outlined" onClick={() => { setName(''); setCreating(true); }}>
            <Icon.plus size={16} /> Создать
          </button>
          <button className="btn primary" onClick={() => doUpload()}>
            <Icon.upload size={16} /> Загрузить
          </button>
        </>
      ),
    }),
    [currentDir, documentTitle, documentIconUrl],
  );

  const dirs = listing ? listing.dirs : [];
  const files = listing ? listing.files : [];
  const isEmpty = listing && !dirs.length && !files.length;
  const allChecked = files.length > 0 && files.every((e) => fsel.has(e.entryId));

  return (
    <>
      {toastNode}
      <SelectionBar
        count={fsel.count}
        onClear={fsel.clear}
        actions={[
          { label: 'Переместить', icon: 'folder', onClick: () => setBulkMoving(true) },
          { label: 'Копировать ссылки', icon: 'link', onClick: bulkCopyLinks },
          { label: 'Удалить', icon: 'trash', danger: true, onClick: () => setBulkConfirm(true) },
        ]}
      />
      <div className={'files-shell' + (over ? ' drop-over' : '')} {...dropHandlers}>
        {over && (
          <div className="drop-overlay">
            <Icon.upload size={40} />
            <span>Отпустите файлы для загрузки</span>
          </div>
        )}
        <div className="files-main">
          <div className="files-bar">
            {searchQuery ? (
              <div className="breadcrumb" style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <Icon.search size={18} />
                <span className="cur">Результаты поиска: «{searchQuery}»</span>
                <a onClick={() => navigate('/files')} style={{ cursor: 'pointer', display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                  <Icon.x size={14} /> Очистить
                </a>
              </div>
            ) : (
              <div className="breadcrumb">
                <a onClick={() => gotoIndex(-1)} className={stack.length ? '' : 'cur'} style={{ cursor: 'pointer' }}>
                  <Icon.folder size={18} />
                </a>
                {stack.map((s, i) => (
                  <React.Fragment key={s.id}>
                    <span className="sep">/</span>
                    {i === stack.length - 1 ? (
                      <span className="cur">{s.name}</span>
                    ) : (
                      <a onClick={() => gotoIndex(i)} style={{ cursor: 'pointer' }}>
                        {s.name}
                      </a>
                    )}
                  </React.Fragment>
                ))}
              </div>
            )}
          </div>

          {!openSmart && !searchQuery && smartFolders.length > 0 && (
            <DynamicFoldersStrip folders={smartFolders} onOpen={setOpenSmart} onCreate={() => setCreatingSmart(true)} />
          )}

          {openSmart ? (
            <div className="files-list">
              <DynamicFolderDetail
                folder={openSmart}
                onBack={() => setOpenSmart(null)}
                onChanged={loadSmartFolders}
                toast={toast}
                albums={albums}
                reloadAlbums={loadAlbums}
              />
            </div>
          ) : (
          <div className="files-list">
            {listing === null ? (
              <Loading />
            ) : isEmpty ? (
              searchQuery ? (
                <EmptyState
                  icon="search"
                  title="Ничего не найдено"
                  hint={`По запросу «${searchQuery}» файлов не нашлось.`}
                />
              ) : (
                <EmptyState
                  icon="folder"
                  title="Папка пуста"
                  hint="Загрузите файлы или создайте подпапку."
                  action={
                    <button className="btn primary" onClick={() => doUpload()}>
                      <Icon.upload size={16} /> Загрузить
                    </button>
                  }
                />
              )
            ) : (
              <table className="ftable">
                <thead>
                  <tr>
                    <th style={{ width: 40 }} className="selcell">
                      <input
                        type="checkbox"
                        checked={allChecked}
                        disabled={!files.length}
                        onChange={(e) => fsel.setAll(files.map((f) => f.entryId), e.target.checked)}
                      />
                    </th>
                    <th>Имя</th>
                    <th style={{ width: 120 }}>Размер</th>
                    <th style={{ width: 140 }}>Изменён</th>
                    <th style={{ width: 120 }}></th>
                  </tr>
                </thead>
                <tbody>
                  {dirs.map((d) => (
                    <DirRow
                      key={d.id}
                      dir={d}
                      onOpen={openDir}
                      onRename={(t) => startRename(t, true)}
                      onDelete={(t) => requestDelete(t, true)}
                      onMenu={(ev, t) => openAt(ev, dirMenu(t))}
                    />
                  ))}
                  {files.map((e) => (
                    <FileRow
                      key={e.entryId}
                      entry={e}
                      selected={!!sel && sel.entryId === e.entryId}
                      bulkChecked={fsel.has(e.entryId)}
                      onBulkToggle={(t, shift) => fsel.select(t.entryId, files.map((f) => f.entryId), shift)}
                      onSelect={setSel}
                      onDownload={download}
                      onRename={(t) => startRename(t, false)}
                      onDelete={(t) => requestDelete(t, false)}
                      onMenu={(ev, t) => openAt(ev, fileMenu(t))}
                    />
                  ))}
                </tbody>
              </table>
            )}
          </div>
          )}
        </div>

        <aside className="files-inspector">
          <Inspector entry={sel} onOpen={setLightbox} onDownload={download} onRename={(t) => startRename(t, false)} onDelete={(t) => requestDelete(t, false)} />
        </aside>
      </div>

      {creatingSmart && (
        <DynamicFolderFormModal
          onClose={() => setCreatingSmart(false)}
          onSaved={() => {
            setCreatingSmart(false);
            loadSmartFolders();
            toast('Умная папка создана');
          }}
          toast={toast}
        />
      )}

      {creating && (
        <Modal
          title="Новая папка"
          onClose={() => setCreating(false)}
          actions={
            <>
              <button className="btn text" onClick={() => setCreating(false)}>
                Отмена
              </button>
              <button className="btn primary" onClick={createDir}>
                Создать
              </button>
            </>
          }
        >
          <label className="field-label">Имя папки</label>
          <input type="text" value={name} autoFocus onChange={(e) => setName(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') createDir(); }} />
        </Modal>
      )}

      {renaming && (
        <Modal
          title="Переименовать"
          onClose={() => setRenaming(null)}
          actions={
            <>
              <button className="btn text" onClick={() => setRenaming(null)}>
                Отмена
              </button>
              <button className="btn primary" onClick={doRename}>
                Сохранить
              </button>
            </>
          }
        >
          <label className="field-label">Новое имя</label>
          <input type="text" value={name} autoFocus onChange={(e) => setName(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') doRename(); }} />
        </Modal>
      )}

      {confirmDel && (
        <ConfirmModal
          title={confirmDel.isDir ? 'Удалить папку?' : 'Удалить в корзину?'}
          danger
          confirmLabel="Удалить"
          message={
            confirmDel.isDir
              ? `Папка «${confirmDel.target.name}» и файлы внутри попадут в корзину (14 дней).`
              : `«${confirmDel.target.name}» будет перемещён в корзину.`
          }
          onClose={() => setConfirmDel(null)}
          onConfirm={doConfirmDelete}
        />
      )}

      {bulkConfirm && (
        <ConfirmModal
          title="Удалить в корзину?"
          danger
          confirmLabel="Удалить"
          message={`Выбранные файлы (${fsel.count}) будут перемещены в корзину.`}
          onClose={() => setBulkConfirm(false)}
          onConfirm={bulkDelete}
        />
      )}

      {props && <PropertiesModal fileId={props.id} fallback={props} onClose={() => setProps(null)} />}

      {shareWith && (
        <ShareWithUserModal fileId={shareWith.fileId} fileName={shareWith.name} onClose={() => setShareWith(null)} toast={toast} />
      )}

      {shareDirWith && (
        <ShareWithUserModal folderId={shareDirWith.id} fileName={shareDirWith.name} onClose={() => setShareDirWith(null)} toast={toast} />
      )}

      {bulkMoving && (
        <MoveToFolderModal count={fsel.count} currentDir={currentDir} onPick={bulkMove} onClose={() => setBulkMoving(false)} />
      )}

      {menu}

      {lightbox && <Lightbox media={lightbox} onClose={() => setLightbox(null)} />}
    </>
  );
}
