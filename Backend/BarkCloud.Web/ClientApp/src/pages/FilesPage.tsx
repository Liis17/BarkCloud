import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { Modal } from '../components/ui/Modal';
import { TextField } from '../components/ui/TextField';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { FolderPropertiesModal } from '../components/ui/FolderPropertiesModal';
import { FolderIcon } from '../components/ui/FolderIcon';
import { FileTypeIcon } from '../components/ui/FileTypeIcon';
import { FolderIconEditorModal, type FolderAppearance } from '../components/ui/FolderIconEditorModal';
import { PropertiesModal } from '../components/ui/PropertiesModal';
import { ShareWithUserModal } from '../components/ui/ShareWithUserModal';
import { PlaylistPickerModal } from '../components/music/PlaylistPickerModal';
import { MoveToFolderModal } from '../components/ui/MoveToFolderModal';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { FileDropOverlay } from '../components/ui/FileDropOverlay';
import { useContextMenu, type ContextItem } from '../components/ui/ContextMenu';
import { SelectionBar } from '../components/ui/SelectionBar';
import { useToast } from '../hooks/useToast';
import { useTextFileViewer } from '../hooks/useTextFileViewer';
import { getTextFileKind, textContentUrl } from '../lib/textFiles';
import { isTextEntry, overlayOpen } from '../lib/keyboard';
import { useAlbumMembership } from '../hooks/useAlbumMembership';
import { CLOUD_ITEMS_MIME, useFileDrop, type FileDropHandlers, type FileDropTarget } from '../hooks/useFileDrop';
import { useSelection } from '../hooks/useSelection';
import { usePageHeader } from '../hooks/usePageHeader';
import { useAudioPlayer } from '../hooks/useAudioPlayer';
import { pickDocumentIcon } from '../hooks/useDocumentHead';
import { useUploadActions } from '../hooks/useUploadManager';
import { DynamicFoldersSkeleton, DynamicFoldersStrip } from '../components/dynamic-folders/DynamicFoldersStrip';
import { DynamicFolderDetail } from '../components/dynamic-folders/DynamicFolderDetail';
import { DynamicFolderFormModal } from '../components/dynamic-folders/DynamicFolderFormModal';
import { apiGet, apiPost, deleteEntriesBatch, downloadArchive, pickFiles } from '../lib/api';
import { createShare, createFolderShare } from '../lib/share';
import { GRID_SIZES } from '../lib/format';
import { FILES_VIEW_KEY, FILES_VIEWS, useStoredChoice } from '../lib/viewPrefs';
import type { Album, CardFile, DirInfo, DynamicFolder, Entry, Listing, MusicTrack } from '../lib/types';

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

const DIRECTORY_PAGE_SIZE = 50;
type DirectoryCursor = { name: string; id: string };

function directoryCursorOf(listing: Listing): DirectoryCursor | null {
  return listing.nextCursorName && listing.nextCursorId
    ? { name: listing.nextCursorName, id: listing.nextCursorId }
    : null;
}

type RenameTarget = { isDir: boolean; target: DirInfo | Entry };

/** Ключ папки в общем выборе с файлами (файлы выбираются по entryId). */
const dirKey = (id: string) => 'dir:' + id;

/** Общие свойства строки/плитки: roving tabindex (Tab попадает в список на один элемент, дальше — стрелками)
 *  и перетаскивание мышью для перемещения в папку. */
type ItemNav = {
  'data-fkey': string;
  tabIndex: number;
  onFocus: () => void;
  draggable: boolean;
  onDragStart: (e: React.DragEvent<HTMLElement>) => void;
  onDragEnd: () => void;
};

function DirRow({ dir, nav, onOpen, onMenu, dropHandlers, active, checked, onToggle }: {
  dir: DirInfo;
  nav: ItemNav;
  onOpen: (d: DirInfo) => void;
  onMenu: (e: React.MouseEvent, d: DirInfo) => void;
  dropHandlers: FileDropHandlers;
  active: boolean;
  checked: boolean;
  onToggle: (shift: boolean) => void;
}) {
  return (
    <tr
      {...nav}
      className={(active ? 'drop-target' : '') + (checked ? ' checked' : '')}
      {...dropHandlers}
      onClick={(e) => (e.shiftKey ? onToggle(true) : onOpen(dir))}
      onContextMenu={(e) => onMenu(e, dir)}
    >
      <td className="selcell" onClick={(e) => e.stopPropagation()}>
        <input type="checkbox" checked={checked} onChange={() => {}} onClick={(e) => onToggle(e.shiftKey)} />
      </td>
      <td className="name">
        <div className="name-cell">
          <FolderIcon iconKey={dir.iconKey} iconColor={dir.iconColor} />
          <div className="file-name-col">
            <div className="fn">{dir.name}</div>
          </div>
        </div>
      </td>
      <td className="size">{dir.sizeLabel || '—'}</td>
      <td className="modified">{fmtDate(dir.updatedAt)}</td>
      <td />
    </tr>
  );
}

function FileRow({ entry, nav, selected, bulkChecked, onBulkToggle, onSelect, onOpen, onRename, onDelete, onDownload, onMenu }: {
  entry: Entry;
  nav: ItemNav;
  selected: boolean;
  bulkChecked: boolean;
  onBulkToggle: (e: Entry, shift: boolean) => void;
  onSelect: (e: Entry) => void;
  onOpen: (e: Entry) => void;
  onRename: (e: Entry) => void;
  onDelete: (e: Entry) => void;
  onDownload: (e: Entry) => void;
  onMenu: (e: React.MouseEvent<HTMLTableRowElement>, entry: Entry) => void;
}) {
  const m = entry.media;
  const isMedia = m?.kind === 'photo' || m?.kind === 'video';
  const hasPreview = (m?.previews || []).length > 0;
  return (
    <tr
      {...nav}
      className={(selected ? 'selected' : '') + (bulkChecked ? ' checked' : '')}
      onClick={(e) => (e.shiftKey ? onBulkToggle(entry, true) : onSelect(entry))}
      onDoubleClick={(e) => { e.currentTarget.focus(); onOpen(entry); }}
      onContextMenu={(e) => { e.currentTarget.focus(); onMenu(e, entry); }}
    >
      <td className="selcell" onClick={(e) => e.stopPropagation()}>
        <input
          type="checkbox"
          checked={bulkChecked}
          onChange={() => {}}
          onClick={(e) => onBulkToggle(entry, e.shiftKey)}
        />
      </td>
      <td className="name">
        <div className="name-cell">
          {isMedia && hasPreview ? (
            <MediaThumb media={m} className="thumb file-thumb" sizes="40px" />
          ) : (
            <div className={'file-icon ' + (m?.iconKind || 'doc')}>{m?.ext || 'FILE'}</div>
          )}
          <div className="file-name-col">
            <div className="fn">{entry.name}</div>
            <div className="meta">{kindLabel(m?.kind)}</div>
          </div>
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

function TileSelbox({ checked, onToggle }: { checked: boolean; onToggle: (shift: boolean) => void }) {
  return (
    <button className="selbox" onClick={(e) => { e.stopPropagation(); onToggle(e.shiftKey); }} title="Выбрать" aria-pressed={checked}>
      {checked ? <Icon.check size={14} /> : null}
    </button>
  );
}

/** Плитка папки в режиме сетки: те же действия, что у DirRow. */
function DirTile({ dir, nav, onOpen, onMenu, dropHandlers, active, checked, onToggle }: React.ComponentProps<typeof DirRow>) {
  return (
    <div
      {...nav}
      className={'file-tile' + (active ? ' drop-target' : '') + (checked ? ' checked' : '')}
      {...dropHandlers}
      onClick={(e) => (e.shiftKey ? onToggle(true) : onOpen(dir))}
      onContextMenu={(e) => onMenu(e, dir)}
    >
      <div className="tile-media">
        <FolderIcon iconKey={dir.iconKey} iconColor={dir.iconColor} size={48} className="big" />
        <TileSelbox checked={checked} onToggle={onToggle} />
      </div>
      <div className="tile-name" title={dir.name}>{dir.name}</div>
    </div>
  );
}

/** Плитка файла в режиме сетки: превью фото/видео или иконка типа из пака FolderIcons. */
function FileTile({ entry, nav, selected, bulkChecked, onBulkToggle, onSelect, onOpen, onMenu }: {
  entry: Entry;
  nav: ItemNav;
  selected: boolean;
  bulkChecked: boolean;
  onBulkToggle: (e: Entry, shift: boolean) => void;
  onSelect: (e: Entry) => void;
  onOpen: (e: Entry) => void;
  onMenu: (e: React.MouseEvent<HTMLElement>, entry: Entry) => void;
}) {
  const m = entry.media;
  const isMedia = m?.kind === 'photo' || m?.kind === 'video';
  const hasPreview = (m?.previews || []).length > 0;
  return (
    <div
      {...nav}
      className={'file-tile' + (selected ? ' selected' : '') + (bulkChecked ? ' checked' : '')}
      onClick={(e) => (e.shiftKey ? onBulkToggle(entry, true) : onSelect(entry))}
      onDoubleClick={(e) => { e.currentTarget.focus(); onOpen(entry); }}
      onContextMenu={(e) => { e.currentTarget.focus(); onMenu(e, entry); }}
    >
      <div className="tile-media">
        {isMedia && hasPreview ? (
          <MediaThumb media={m} sizes={GRID_SIZES} colorPlaceholder />
        ) : (
          <FileTypeIcon media={m} size={48} className="big" />
        )}
        <TileSelbox checked={bulkChecked} onToggle={(shift) => onBulkToggle(entry, shift)} />
        {m?.kind === 'video' && hasPreview && <div className="vbadge"><Icon.play size={10} /> видео</div>}
      </div>
      <div className="tile-name" title={entry.name}>{entry.name}</div>
    </div>
  );
}

function Inspector({ entry, onOpen, onRename, onDelete, onDownload }: {
  entry: Entry | null;
  onOpen: (e: Entry) => void;
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
        {((isMedia && m) || getTextFileKind(entry.name)) && (
          <button className="btn outlined" onClick={() => onOpen(entry)}>
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
  const directDirectoryId = new URLSearchParams(location.search).get('dir') || '';
  const smartFolderId = new URLSearchParams(location.search).get('smart') || '';
  const stack = directDirectoryId ? [{ id: directDirectoryId, name: 'Папка' }] : (navState.stack ?? []);
  const pendingSelect = React.useRef<string | null>(navState.selectEntryId || null);
  const [listing, setListing] = React.useState<Listing | null>(null);
  const [sel, setSel] = React.useState<Entry | null>(null);
  const [view, setView] = useStoredChoice(FILES_VIEW_KEY, FILES_VIEWS, 'list');
  const [lightbox, setLightbox] = React.useState<CardFile | null>(null);
  const { openTextFile, textViewer } = useTextFileViewer();
  const [creating, setCreating] = React.useState(false);
  const [renaming, setRenaming] = React.useState<RenameTarget | null>(null);
  const [name, setName] = React.useState('');
  const [albums, setAlbums] = React.useState<Album[]>([]);
  const [props, setProps] = React.useState<CardFile | null>(null);
  const [dirProps, setDirProps] = React.useState<DirInfo | null>(null);
  const [iconDir, setIconDir] = React.useState<DirInfo | null>(null);
  const [playlistPick, setPlaylistPick] = React.useState<Entry | null>(null);
  const [shareWith, setShareWith] = React.useState<Entry | null>(null);
  const [shareDirWith, setShareDirWith] = React.useState<DirInfo | null>(null);
  const [confirmDel, setConfirmDel] = React.useState<RenameTarget | null>(null);
  const [bulkConfirm, setBulkConfirm] = React.useState(false);
  const [bulkMoving, setBulkMoving] = React.useState(false);
  const [movingDir, setMovingDir] = React.useState<DirInfo | null>(null);
  const [moveProgress, setMoveProgress] = React.useState<{ done: number; total: number } | null>(null);
  const [archiving, setArchiving] = React.useState(false);
  const [smartFolders, setSmartFolders] = React.useState<DynamicFolder[]>([]);
  const [smartFoldersLoading, setSmartFoldersLoading] = React.useState(true);
  const [openSmart, setOpenSmart] = React.useState<DynamicFolder | null>(null);
  const [creatingSmart, setCreatingSmart] = React.useState(false);
  const [directoryCursor, setDirectoryCursor] = React.useState<DirectoryCursor | null>(null);
  const [directoryMore, setDirectoryMore] = React.useState(false);
  const directorySentinelRef = React.useRef<HTMLDivElement | null>(null);
  const loadMoreDirectoryRef = React.useRef<() => void>(() => {});
  const [searchCursor, setSearchCursor] = React.useState<{ at: string; id: string } | null>(null);
  const [searchMore, setSearchMore] = React.useState(false);
  const [toastNode, toast] = useToast();
  const { enqueue, attachVersion } = useUploadActions();
  const audioPlayer = useAudioPlayer();
  const { menu, openAt } = useContextMenu();
  const membership = useAlbumMembership(albums);
  const { over, target: dropTarget, dropHandlers, getTargetHandlers } = useFileDrop((files, target) => doUpload(files, target));
  const fsel = useSelection();
  const [focusKey, setFocusKey] = React.useState<string | null>(null);
  const shellRef = React.useRef<HTMLDivElement | null>(null);
  const listRef = React.useRef<HTMLDivElement | null>(null);
  const hotkeysRef = React.useRef<(e: KeyboardEvent) => void>(() => {});
  // Перетаскиваемые мышью записи (ключи выбора) и папка под курсором.
  const dragKeysRef = React.useRef<string[] | null>(null);
  const [moveOver, setMoveOver] = React.useState<string | null>(null);

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
    setDirectoryCursor(null);
    setDirectoryMore(false);
    // Режим поиска: результаты по имени (по всему облаку), без подпапок; cursor-пагинация.
    if (searchQuery) {
      setSearchCursor(null);
      apiGet<{ files: Entry[]; nextCursorAt: string | null; nextCursorId: string }>(
        '/api/cloud/search?q=' + encodeURIComponent(searchQuery),
      )
        .then((d) => {
          setListing({ dirs: [], files: d.files || [] });
          setSearchCursor(d.nextCursorAt && d.nextCursorId ? { at: d.nextCursorAt, id: d.nextCursorId } : null);
        })
        .catch((e) => {
          toast((e as Error).message, 'err');
          setListing({ dirs: [], files: [] });
        });
      return;
    }
    apiGet<Listing>(
      '/api/cloud/list?dir=' + encodeURIComponent(currentDir) + '&limit=' + DIRECTORY_PAGE_SIZE,
    )
      .then((d) => {
        setListing(d);
        setDirectoryCursor(directoryCursorOf(d));
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
      .catch(() => {})
      .finally(() => setSmartFoldersLoading(false));
  }, []);
  React.useEffect(() => {
    loadSmartFolders();
  }, [loadSmartFolders]);
  React.useEffect(() => {
    if (!smartFolderId || !smartFolders.length) return;
    const folder = smartFolders.find((item) => item.id === smartFolderId);
    if (folder) setOpenSmart(folder);
    else toast('Умная папка больше недоступна', 'err');
  }, [smartFolderId, smartFolders, toast]);
  React.useEffect(() => {
    membership.ensureLoaded();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [albums]);

  function loadMoreSearch() {
    if (!searchCursor || searchMore) return;
    setSearchMore(true);
    apiGet<{ files: Entry[]; nextCursorAt: string | null; nextCursorId: string }>(
      `/api/cloud/search?q=${encodeURIComponent(searchQuery)}&cursorAt=${encodeURIComponent(searchCursor.at)}&cursorId=${encodeURIComponent(searchCursor.id)}`,
    )
      .then((d) => {
        setListing((prev) => ({ dirs: prev?.dirs || [], files: [...(prev?.files || []), ...(d.files || [])] }));
        setSearchCursor(d.nextCursorAt && d.nextCursorId ? { at: d.nextCursorAt, id: d.nextCursorId } : null);
      })
      .catch((e) => toast((e as Error).message, 'err'))
      .finally(() => setSearchMore(false));
  }

  function loadMoreDirectory() {
    if (!directoryCursor || directoryMore || searchQuery) return;
    setDirectoryMore(true);
    apiGet<Listing>(
      `/api/cloud/list?dir=${encodeURIComponent(currentDir)}&limit=${DIRECTORY_PAGE_SIZE}`
        + `&cursorName=${encodeURIComponent(directoryCursor.name)}&cursorId=${encodeURIComponent(directoryCursor.id)}`,
    )
      .then((d) => {
        setListing((prev) => ({
          dirs: prev?.dirs || d.dirs || [],
          files: [...(prev?.files || []), ...(d.files || [])],
          nextCursorName: d.nextCursorName,
          nextCursorId: d.nextCursorId,
        }));
        setDirectoryCursor(directoryCursorOf(d));
      })
      .catch((e) => toast((e as Error).message, 'err'))
      .finally(() => setDirectoryMore(false));
  }

  loadMoreDirectoryRef.current = loadMoreDirectory;
  React.useEffect(() => {
    const sentinel = directorySentinelRef.current;
    if (!sentinel || !directoryCursor || searchQuery || typeof IntersectionObserver === 'undefined') return;

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) loadMoreDirectoryRef.current();
      },
      { rootMargin: '400px 0px' },
    );
    observer.observe(sentinel);
    return () => observer.disconnect();
  }, [directoryCursor, searchQuery]);

  const openDir = (dir: DirInfo) => navigate('/files', { state: { stack: [...stack, { id: dir.id, name: dir.name }] } });
  const gotoIndex = (i: number) => {
    if (i < 0 && !stack.length) return;
    navigate('/files', { state: { stack: stack.slice(0, i + 1) } });
  };

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
  // Двойной клик: текст — в модалке, фото/видео — в Lightbox, аудио — в миниплеере.
  async function openEntry(entry: Entry) {
    const m = entry.media;
    if (openTextFile({ name: entry.name, size: m?.size, contentUrl: textContentUrl({ kind: 'owned', fileId: entry.fileId }), onDownload: () => download(entry) })) return;
    if (m && (m.kind === 'photo' || m.kind === 'video')) setLightbox(m);
    else if (m?.kind === 'audio') await playAudioEntry(entry);
    else download(entry);
  }

  async function playAudioEntry(entry: Entry) {
    if (!entry.media) return;
    try {
      const d = await apiPost<{ url?: string }>('/api/music/track/url', { fileId: entry.fileId });
      if (!d.url) throw new Error('Ссылка недоступна');
      const folderTracks: MusicTrack[] = (listing?.files || [])
        .filter((e) => e.media?.kind === 'audio')
        .map((e) => ({
          file: e.media!,
          title: e.name.replace(/\.[^.]+$/, ''),
          artist: '',
          album: '',
          duration: 0,
          coverUrl: '',
          largeCoverUrl: '',
          url: e.fileId === entry.fileId ? d.url! : '',
        }));
      audioPlayer.playQueue(folderTracks.length ? folderTracks : [{
        file: entry.media,
        title: entry.name.replace(/\.[^.]+$/, ''),
        artist: '',
        album: '',
        duration: 0,
        coverUrl: '',
        largeCoverUrl: '',
        url: d.url,
      }], entry.fileId);
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

  function fileMenu(entry: Entry, opener: HTMLElement): ContextItem[] {
    const m = entry.media;
    const isMedia = m?.kind === 'photo' || m?.kind === 'video';
    const inAlbums = membership.of(entry.fileId);
    const available = albums.filter((a) => !inAlbums.has(a.id));
    const present = albums.filter((a) => inAlbums.has(a.id));
    const out: ContextItem[] = [
      ...(getTextFileKind(entry.name) ? [{ label: 'Открыть', icon: 'eye' as const, onClick: () => { opener.focus(); openEntry(entry); } }] : []),
      { label: 'Скачать', icon: 'download', onClick: () => download(entry) },
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
    if (m?.kind === 'audio')
      out.push({ label: 'Добавить в плейлист…', icon: 'music', onClick: () => setPlaylistPick(entry) });
    if (m) out.push({ label: 'Свойства', icon: 'info', onClick: () => setProps(m) });
    out.push({ divider: true });
    out.push({ label: 'Удалить', icon: 'trash', danger: true, onClick: () => requestDelete(entry, false) });
    return out;
  }
  function dirMenu(dir: DirInfo): ContextItem[] {
    return [
      { label: 'Скачать папку', icon: 'download', onClick: () => archiveFolder(dir) },
      { label: 'Сделать папку публичной', icon: 'share', onClick: () => createFolderShare(dir.id, dir.name, toast) },
      { label: 'Поделиться с пользователем', icon: 'user', onClick: () => setShareDirWith(dir) },
      { label: 'Переименовать', icon: 'pencil', onClick: () => startRename(dir, true) },
      { label: 'Переместить', icon: 'folder', onClick: () => setMovingDir(dir) },
      { label: 'Изменить значок', icon: 'palette', onClick: () => setIconDir(dir) },
      { label: 'Свойства', icon: 'info', onClick: () => setDirProps(dir) },
      { divider: true },
      { label: 'Удалить', icon: 'trash', danger: true, onClick: () => requestDelete(dir, true) },
    ];
  }
  async function doUpload(dropped?: File[], target: FileDropTarget | null = null) {
    const files = dropped && dropped.length ? dropped : await pickFiles({});
    if (!files.length) return;
    enqueue(files, { dir: target?.id || currentDir });
  }

  const chosenDirs = () => (listing?.dirs || []).filter((d) => fsel.has(dirKey(d.id)));

  async function bulkDelete() {
    const chosen = (listing?.files || []).filter((e) => fsel.has(e.entryId));
    const dirsToDelete = chosenDirs();
    try {
      // Пакетного удаления папок нет: каждая папка — отдельный запрос, файлы — одним пакетом.
      let succeeded = 0;
      let failed = 0;
      for (const d of dirsToDelete) {
        try {
          await apiPost('/api/cloud/dir/delete', { id: d.id });
          succeeded++;
        } catch {
          failed++;
        }
      }
      const result = await deleteEntriesBatch(chosen.map((e) => e.entryId));
      succeeded += result.succeeded;
      failed += result.failed;
      setBulkConfirm(false);
      fsel.clear();
      if (succeeded) {
        toast(failed ? `Перемещено в корзину: ${succeeded}, не удалось: ${failed}` : `Перемещено в корзину: ${succeeded}`);
        load();
      } else if (failed) {
        toast('Не удалось переместить выбранное в корзину', 'err');
      }
    } catch (err) {
      toast((err as Error).message, 'err');
    }
  }

  /** Перемещение выбранного (или перетащенных мышью ключей) в папку targetDir. */
  async function bulkMove(targetDir: string, keys?: string[]) {
    if (moveProgress) return;
    const picked = keys ? (k: string) => keys.includes(k) : fsel.has;
    const chosen = [
      ...(listing?.dirs || [])
        .filter((d) => picked(dirKey(d.id)))
        .map((d) => ({ name: d.name, move: () => apiPost('/api/cloud/dir/move', { id: d.id, parentId: targetDir }) })),
      ...(listing?.files || [])
        .filter((e) => picked(e.entryId))
        .map((e) => ({ name: e.name, move: () => apiPost('/api/cloud/entry/move', { entryId: e.entryId, dir: targetDir }) })),
    ];
    let ok = 0;
    let failed = 0;
    let firstError: { name: string; message: string } | null = null;
    setMoveProgress({ done: 0, total: chosen.length });
    try {
      for (const e of chosen) {
        try {
          await e.move();
          ok++;
        } catch (err) {
          failed++;
          if (!firstError) firstError = { name: e.name, message: (err as Error).message };
        }
        setMoveProgress((p) => (p ? { ...p, done: p.done + 1 } : p));
      }
    } finally {
      setMoveProgress(null);
      setBulkMoving(false);
      fsel.clear();
    }
    if (ok) {
      toast(failed ? `Перемещено: ${ok}, не удалось: ${failed}` : `Перемещено: ${ok}`);
      load();
    } else if (failed === 1 && firstError) {
      toast(`«${firstError.name}»: ${firstError.message}`, 'err');
    } else if (failed) {
      toast(`Не удалось переместить выбранное (${failed})`, 'err');
    }
  }
  async function moveDir(targetDir: string) {
    const dir = movingDir;
    if (!dir) return;
    setMovingDir(null);
    try {
      await apiPost('/api/cloud/dir/move', { id: dir.id, parentId: targetDir });
      toast(`Папка «${dir.name}» перемещена`);
      load();
    } catch (err) {
      toast((err as Error).message, 'err');
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
  async function bulkArchive() {
    const entryIds = (listing?.files || []).filter((e) => fsel.has(e.entryId)).map((e) => e.entryId);
    if (!entryIds.length || archiving) return;
    setArchiving(true);
    toast('Готовлю архив…');
    try {
      await downloadArchive({ entryIds });
      fsel.clear();
      toast('Архив готов, скачивание началось');
    } catch (e) {
      toast((e as Error).message, 'err');
    } finally {
      setArchiving(false);
    }
  }
  async function saveDirAppearance(dir: DirInfo, appearance: FolderAppearance) {
    try {
      const updated = await apiPost<DirInfo>('/api/cloud/dir/appearance', { id: dir.id, ...appearance });
      setListing((prev) => prev && ({
        ...prev,
        dirs: prev.dirs.map((d) => (d.id === dir.id ? { ...d, iconKey: updated.iconKey, iconColor: updated.iconColor } : d)),
      }));
      setIconDir(null);
    } catch (e) {
      toast((e as Error).message || 'Не удалось изменить значок', 'err');
    }
  }
  async function archiveFolder(dir: DirInfo) {
    if (archiving) return;
    setArchiving(true);
    toast('Готовлю архив папки…');
    try {
      await downloadArchive({ directoryId: dir.id, name: dir.name });
      toast('Архив готов, скачивание началось');
    } catch (e) {
      toast((e as Error).message, 'err');
    } finally {
      setArchiving(false);
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
  const isEmpty = listing && !dirs.length && !files.length && !directoryCursor;
  // Ключи выбора: папки с префиксом dirKey, файлы — по entryId; общий порядок нужен для Shift-диапазона.
  const orderedKeys = [...dirs.map((d) => dirKey(d.id)), ...files.map((f) => f.entryId)];
  const allChecked = orderedKeys.length > 0 && orderedKeys.every((k) => fsel.has(k));
  const selectedDirs = dirs.filter((d) => fsel.has(dirKey(d.id)));
  const selectedFileCount = fsel.count - selectedDirs.length;
  // Сервер архивирует либо набор файлов, либо одну папку целиком; смешанный выбор не поддерживается.
  const archiveDir = selectedDirs.length === 1 && !selectedFileCount ? selectedDirs[0] : null;
  const canArchive = !selectedDirs.length || !!archiveDir;
  const dropDirectoryName = dropTarget?.label || (stack.length ? stack[stack.length - 1].name : 'корень облака');

  const navKey = focusKey && orderedKeys.includes(focusKey) ? focusKey : orderedKeys[0];
  const navOf = (key: string): ItemNav => ({
    'data-fkey': key,
    tabIndex: key === navKey ? 0 : -1,
    onFocus: () => setFocusKey(key),
    draggable: !moveProgress,
    onDragStart: (e) => {
      // Отмеченная запись тянет за собой весь выбор, иначе — только себя.
      const keys = fsel.has(key) ? orderedKeys.filter((k) => fsel.has(k)) : [key];
      dragKeysRef.current = keys;
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData(CLOUD_ITEMS_MIME, keys.join('\n'));
    },
    onDragEnd: () => {
      dragKeysRef.current = null;
      setMoveOver(null);
    },
  });
  /** Папка-цель перемещения: принимает перетаскиваемые записи, кроме самой себя; файлы с диска — как раньше. */
  function moveTargetHandlers(targetId: string, uploadHandlers?: FileDropHandlers): FileDropHandlers {
    const accepts = (e: React.DragEvent<HTMLElement>) => {
      const keys = dragKeysRef.current;
      return !!keys && Array.from(e.dataTransfer.types || []).includes(CLOUD_ITEMS_MIME)
        && targetId !== currentDir && !keys.includes(dirKey(targetId));
    };
    const internal = (e: React.DragEvent<HTMLElement>) => !!dragKeysRef.current && Array.from(e.dataTransfer.types || []).includes(CLOUD_ITEMS_MIME);
    const over = (e: React.DragEvent<HTMLElement>) => {
      if (!internal(e)) return false;
      if (accepts(e)) {
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        setMoveOver(targetId);
      }
      return true;
    };
    return {
      onDragEnter: (e) => { if (!over(e)) uploadHandlers?.onDragEnter(e); },
      onDragOver: (e) => { if (!over(e)) uploadHandlers?.onDragOver(e); },
      onDragLeave: (e) => {
        if (!internal(e)) return uploadHandlers?.onDragLeave(e);
        if (!(e.relatedTarget instanceof Node && e.currentTarget.contains(e.relatedTarget))) setMoveOver((cur) => (cur === targetId ? null : cur));
      },
      onDrop: (e) => {
        if (!internal(e)) return uploadHandlers?.onDrop(e);
        e.preventDefault();
        e.stopPropagation();
        const keys = accepts(e) ? dragKeysRef.current : null;
        dragKeysRef.current = null;
        setMoveOver(null);
        if (keys) void bulkMove(targetId, keys);
      },
    };
  }
  const itemOf = (key: string | null | undefined): RenameTarget | null => {
    if (!key) return null;
    const dir = dirs.find((d) => dirKey(d.id) === key);
    if (dir) return { isDir: true, target: dir };
    const file = files.find((f) => f.entryId === key);
    return file ? { isDir: false, target: file } : null;
  };
  const focusedKeyOf = (target: EventTarget | null) =>
    target instanceof HTMLElement ? target.closest<HTMLElement>('[data-fkey]')?.dataset.fkey : undefined;
  function focusItem(key: string) {
    setFocusKey(key);
    listRef.current?.querySelector<HTMLElement>(`[data-fkey="${key}"]`)?.focus();
  }
  function gridColumns(): number {
    const grid = listRef.current?.querySelector('.files-grid');
    return grid ? getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length || 1 : 1;
  }

  // Навигация по списку, когда фокус на самой строке/плитке (не на её чекбоксе или кнопке).
  function onListKeyDown(e: React.KeyboardEvent<HTMLDivElement>) {
    const key = (e.target as HTMLElement).dataset?.fkey;
    if (!key || e.ctrlKey || e.metaKey || e.altKey) return;
    const i = orderedKeys.indexOf(key);
    if (i < 0) return;
    const cols = view === 'grid' ? gridColumns() : 1;
    const moves: Record<string, number> = {
      ArrowDown: cols,
      ArrowUp: -cols,
      ...(view === 'grid' ? { ArrowRight: 1, ArrowLeft: -1 } : {}),
      Home: -i,
      End: orderedKeys.length - 1 - i,
    };
    if (e.key in moves) {
      e.preventDefault();
      const next = Math.min(orderedKeys.length - 1, Math.max(0, i + moves[e.key]));
      focusItem(orderedKeys[next]);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const item = itemOf(key);
      if (item?.isDir) openDir(item.target as DirInfo);
      else if (item) openEntry(item.target as Entry);
    } else if (e.key === ' ') {
      e.preventDefault();
      fsel.select(key, orderedKeys, e.shiftKey);
    }
  }

  // Del, F2 и Ctrl+A — на всей странице, но не в полях ввода и не под открытым диалогом/просмотрщиком.
  hotkeysRef.current = (e) => {
    if (openSmart || !listing || e.altKey || isTextEntry(e.target) || overlayOpen()) return;
    const t = e.target;
    if (t !== document.body && !(t instanceof Node && shellRef.current?.contains(t))) return;
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.code === 'KeyA') {
      if (!orderedKeys.length) return;
      e.preventDefault();
      fsel.setAll(orderedKeys, true);
      return;
    }
    if (mod) return;
    const single = () => itemOf(focusedKeyOf(t)) ?? (sel ? { isDir: false, target: sel } : null);
    if (e.key === 'Delete') {
      if (fsel.count) {
        e.preventDefault();
        setBulkConfirm(true);
        return;
      }
      const item = single();
      if (!item) return;
      e.preventDefault();
      requestDelete(item.target, item.isDir);
    } else if (e.key === 'F2') {
      if (fsel.count > 1) return;
      const item = fsel.count === 1 ? itemOf(fsel.list[0]) : single();
      if (!item) return;
      e.preventDefault();
      startRename(item.target, item.isDir);
    }
  };
  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => hotkeysRef.current(e);
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  return (
    <>
      {toastNode}
      <SelectionBar
        count={fsel.count}
        onClear={fsel.clear}
        actions={[
          { label: 'Переместить', icon: 'folder', onClick: () => setBulkMoving(true) },
          {
            label: archiving ? 'Архивирую…' : 'Скачать архивом',
            icon: 'download',
            disabled: archiving || !canArchive,
            onClick: () => (archiveDir ? archiveFolder(archiveDir).then(fsel.clear) : bulkArchive()),
          },
          { label: 'Копировать ссылки', icon: 'link', disabled: !selectedFileCount, onClick: bulkCopyLinks },
          { label: 'Удалить', icon: 'trash', danger: true, onClick: () => setBulkConfirm(true) },
        ]}
      />
      <div ref={shellRef} className={'files-shell' + (over ? ' drop-over' : '')} {...dropHandlers}>
        {over && <FileDropOverlay detail={`Файлы будут загружены в папку «${dropDirectoryName}»`} />}
        <div className="files-main">
          {!openSmart && !searchQuery && !currentDir && (smartFoldersLoading || smartFolders.length > 0) && (
            <div className="smart-block">
              {smartFoldersLoading ? (
                <DynamicFoldersSkeleton />
              ) : (
                <DynamicFoldersStrip folders={smartFolders} onOpen={setOpenSmart} onCreate={() => setCreatingSmart(true)} />
              )}
            </div>
          )}
          <div className="files-block">
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
                  <a onClick={() => gotoIndex(-1)} className={(stack.length ? '' : 'cur') + (moveOver === '' ? ' drop-target' : '')} style={{ cursor: 'pointer' }} title="Корень облака" {...moveTargetHandlers('')}>
                    <Icon.folder size={18} />
                  </a>
                  {stack.map((s, i) => (
                    <React.Fragment key={s.id}>
                      <span className="sep">/</span>
                      {i === stack.length - 1 ? (
                        <span className="cur">{s.name}</span>
                      ) : (
                        <a onClick={() => gotoIndex(i)} className={moveOver === s.id ? 'drop-target' : ''} style={{ cursor: 'pointer' }} {...moveTargetHandlers(s.id)}>
                          {s.name}
                        </a>
                      )}
                    </React.Fragment>
                  ))}
                </div>
              )}
              {!openSmart && (
                <div className="files-view">
                  {view === 'grid' && orderedKeys.length > 0 && (
                    <label className="files-select-all" title="Выбрать все">
                      <input type="checkbox" checked={allChecked} onChange={(e) => fsel.setAll(orderedKeys, e.target.checked)} />
                    </label>
                  )}
                  <button className={'icon-btn' + (view === 'list' ? ' active' : '')} title="Списком" aria-pressed={view === 'list'} onClick={() => setView('list')}>
                    <Icon.list size={18} />
                  </button>
                  <button className={'icon-btn' + (view === 'grid' ? ' active' : '')} title="Сеткой" aria-pressed={view === 'grid'} onClick={() => setView('grid')}>
                    <Icon.grid size={18} />
                  </button>
                </div>
              )}
            </div>

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
            <div className="files-list" ref={listRef} onKeyDown={onListKeyDown}>
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
              ) : view === 'grid' ? (
                <div className="photo-grid files-grid">
                  {dirs.map((d) => (
                    <DirTile
                      key={d.id}
                      dir={d}
                      nav={navOf(dirKey(d.id))}
                      active={dropTarget?.id === d.id || moveOver === d.id}
                      dropHandlers={moveTargetHandlers(d.id, getTargetHandlers({ id: d.id, label: d.name }))}
                      onOpen={openDir}
                      onMenu={(ev, t) => openAt(ev, dirMenu(t))}
                      checked={fsel.has(dirKey(d.id))}
                      onToggle={(shift) => fsel.select(dirKey(d.id), orderedKeys, shift)}
                    />
                  ))}
                  {files.map((e) => (
                    <FileTile
                      key={e.entryId}
                      entry={e}
                      nav={navOf(e.entryId)}
                      selected={!!sel && sel.entryId === e.entryId}
                      bulkChecked={fsel.has(e.entryId)}
                      onBulkToggle={(t, shift) => fsel.select(t.entryId, orderedKeys, shift)}
                      onSelect={(t) => { setSel(t); fsel.setAnchor(t.entryId); }}
                      onOpen={openEntry}
                      onMenu={(ev, t) => openAt(ev, fileMenu(t, ev.currentTarget))}
                    />
                  ))}
                </div>
              ) : (
                <table className="ftable">
                  <thead>
                    <tr>
                      <th style={{ width: 40 }} className="selcell">
                        <input
                          type="checkbox"
                          checked={allChecked}
                          disabled={!orderedKeys.length}
                          onChange={(e) => fsel.setAll(orderedKeys, e.target.checked)}
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
                        nav={navOf(dirKey(d.id))}
                        active={dropTarget?.id === d.id || moveOver === d.id}
                        dropHandlers={moveTargetHandlers(d.id, getTargetHandlers({ id: d.id, label: d.name }))}
                        onOpen={openDir}
                        onMenu={(ev, t) => openAt(ev, dirMenu(t))}
                        checked={fsel.has(dirKey(d.id))}
                        onToggle={(shift) => fsel.select(dirKey(d.id), orderedKeys, shift)}
                      />
                    ))}
                    {files.map((e) => (
                      <FileRow
                        key={e.entryId}
                        entry={e}
                        nav={navOf(e.entryId)}
                        selected={!!sel && sel.entryId === e.entryId}
                        bulkChecked={fsel.has(e.entryId)}
                        onBulkToggle={(t, shift) => fsel.select(t.entryId, orderedKeys, shift)}
                        onSelect={(t) => { setSel(t); fsel.setAnchor(t.entryId); }}
                        onOpen={openEntry}
                        onDownload={download}
                        onRename={(t) => startRename(t, false)}
                        onDelete={(t) => requestDelete(t, false)}
                        onMenu={(ev, t) => openAt(ev, fileMenu(t, ev.currentTarget))}
                      />
                    ))}
                  </tbody>
                </table>
              )}
              {searchQuery && searchCursor && (
                <div style={{ display: 'flex', justifyContent: 'center', padding: '14px 0' }}>
                  <button className="btn outlined" onClick={loadMoreSearch} disabled={searchMore}>
                    {searchMore ? 'Загрузка…' : 'Показать ещё'}
                  </button>
                </div>
              )}
              {!searchQuery && directoryCursor && (
                <div
                  ref={directorySentinelRef}
                  role={directoryMore ? 'status' : undefined}
                  aria-label={directoryMore ? 'Загрузка файлов' : undefined}
                  style={{ display: 'flex', justifyContent: 'center', minHeight: 24, padding: '14px 0' }}
                >
                  {directoryMore && <span>Загрузка…</span>}
                </div>
              )}
            </div>
            )}
          </div>
        </div>

        <aside className="files-inspector">
          <Inspector entry={sel} onOpen={openEntry} onDownload={download} onRename={(t) => startRename(t, false)} onDelete={(t) => requestDelete(t, false)} />
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
          <TextField label="Имя папки" value={name} autoFocus onChange={(e) => setName(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') createDir(); }} />
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
          <TextField label="Новое имя" value={name} autoFocus onChange={(e) => setName(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') doRename(); }} />
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
          message={`Выбранное (${fsel.count}) будет перемещено в корзину.`}
          onClose={() => setBulkConfirm(false)}
          onConfirm={bulkDelete}
        />
      )}

      {props && <PropertiesModal fileId={props.id} fallback={props} onClose={() => setProps(null)} />}

      {iconDir && (
        <FolderIconEditorModal
          folderName={iconDir.name}
          initial={{ iconKey: iconDir.iconKey || null, iconColor: iconDir.iconColor || null }}
          onClose={() => setIconDir(null)}
          onSave={(appearance) => saveDirAppearance(iconDir, appearance)}
        />
      )}
      {dirProps && <FolderPropertiesModal directoryId={dirProps.id} onClose={() => setDirProps(null)} toast={toast} />}

      {playlistPick && <PlaylistPickerModal fileId={playlistPick.fileId} onClose={() => setPlaylistPick(null)} toast={toast} />}

      {shareWith && (
        <ShareWithUserModal fileId={shareWith.fileId} fileName={shareWith.name} onClose={() => setShareWith(null)} toast={toast} />
      )}

      {shareDirWith && (
        <ShareWithUserModal folderId={shareDirWith.id} fileName={shareDirWith.name} onClose={() => setShareDirWith(null)} toast={toast} />
      )}

      {bulkMoving && (
        <MoveToFolderModal
          count={fsel.count}
          currentDir={currentDir}
          progress={moveProgress ?? undefined}
          excludeDirs={selectedDirs}
          onPick={bulkMove}
          onClose={() => setBulkMoving(false)}
        />
      )}

      {movingDir && (
        <MoveToFolderModal
          count={1}
          currentDir={currentDir}
          excludeDirs={[movingDir]}
          onPick={moveDir}
          onClose={() => setMovingDir(null)}
        />
      )}

      {menu}

      {lightbox && <Lightbox media={lightbox} onClose={() => setLightbox(null)} />}
      {textViewer}
    </>
  );
}
