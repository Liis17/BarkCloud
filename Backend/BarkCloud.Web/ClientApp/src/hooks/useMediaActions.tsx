import React from 'react';
import { useNavigate } from 'react-router-dom';
import { ContextMenu, type ContextItem } from '../components/ui/ContextMenu';
import { RenameModal } from '../components/ui/RenameModal';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { PropertiesModal } from '../components/ui/PropertiesModal';
import { useAlbumMembership } from './useAlbumMembership';
import { apiGet, apiPost, pickFiles, uploadFile } from '../lib/api';
import { createShare } from '../lib/share';
import { ShareWithUserModal } from '../components/ui/ShareWithUserModal';
import type { Album, MediaItem } from '../lib/types';
import type { ToastPush } from './useToast';

interface DownloadResponse {
  urls: Record<string, string | null>;
}

interface UseMediaActionsArgs {
  albums?: Album[];
  toast: ToastPush;
  onRenamed?: (media: MediaItem, name: string) => void;
  onRemoved?: (media: MediaItem) => void;
  onItemPatched?: (id: string, patch: Partial<MediaItem>) => void;
  reloadAlbums?: () => void;
}

/** Действия над медиа, экспонированные наружу (панель Lightbox). Модалки
 *  (подтверждение, свойства, шаринг) рендерятся в overlay родителя. */
export interface MediaActionsApi {
  albums: Album[];
  membership: ReturnType<typeof useAlbumMembership>;
  toast: ToastPush;
  copyTempLink: (m: MediaItem) => void;
  createPublicLink: (m: MediaItem) => void;
  shareWithUser: (m: MediaItem) => void;
  addToAlbum: (m: MediaItem, albumId: string) => void;
  removeFromAlbum: (m: MediaItem, albumId: string) => void;
  revealInFolder: (m: MediaItem) => void;
  showProperties: (m: MediaItem) => void;
  requestDelete: (m: MediaItem) => void;
}

/** Добавить cache-bust к URL превью, чтобы браузер перезапросил обновлённое изображение. */
function bustPreviews(m: MediaItem): Partial<MediaItem> {
  const t = Date.now();
  return { previews: (m.previews || []).map((p) => ({ ...p, url: p.url + (p.url.includes('?') ? '&' : '?') + 't=' + t })) };
}

/** Полный набор действий контекстного меню для медиа галереи (Фото/Видео). */
export function useMediaActions({ albums, toast, onRenamed, onRemoved, onItemPatched, reloadAlbums }: UseMediaActionsArgs) {
  const navigate = useNavigate();
  const [menu, setMenu] = React.useState<{ x: number; y: number; media: MediaItem } | null>(null);
  const [rename, setRename] = React.useState<MediaItem | null>(null);
  const [confirm, setConfirm] = React.useState<MediaItem | null>(null);
  const [props, setProps] = React.useState<MediaItem | null>(null);
  const [shareWith, setShareWith] = React.useState<MediaItem | null>(null);
  const membership = useAlbumMembership(albums);

  const openMenu = React.useCallback(
    (e: React.MouseEvent, media: MediaItem) => {
      e.preventDefault();
      e.stopPropagation();
      membership.ensureLoaded();
      setMenu({ x: e.clientX, y: e.clientY, media });
    },
    [membership],
  );

  async function copyLink(m: MediaItem) {
    try {
      const d = await apiGet<DownloadResponse>('/api/files/download?ids=' + encodeURIComponent(m.id));
      const url = d.urls && d.urls[m.id];
      if (!url) throw new Error('Ссылка недоступна');
      await navigator.clipboard.writeText(url);
      toast('Ссылка скопирована (временная)');
    } catch (e) {
      toast((e as Error).message || 'Не удалось скопировать', 'err');
    }
  }
  async function doRename(m: MediaItem, name: string) {
    const entryId = m.entryIds && m.entryIds[0];
    if (!entryId) {
      toast('Файл не привязан к папке', 'err');
      return;
    }
    try {
      await apiPost('/api/cloud/entry/rename', { entryId, name });
      setRename(null);
      onRenamed && onRenamed(m, name);
      toast('Переименовано');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function doDelete(m: MediaItem) {
    try {
      await apiPost('/api/cloud/media/delete', { fileId: m.id });
      setConfirm(null);
      onRemoved && onRemoved(m);
      toast('Перемещено в корзину');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function addToAlbum(m: MediaItem, albumId: string) {
    try {
      await apiPost('/api/albums/items/add', { album: albumId, fileIds: [m.id] });
      membership.addLocal(m.id, albumId);
      reloadAlbums && reloadAlbums();
      toast('Добавлено в альбом');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function removeFromAlbum(m: MediaItem, albumId: string) {
    try {
      await apiPost('/api/albums/items/remove', { album: albumId, fileIds: [m.id] });
      membership.removeLocal(m.id, albumId);
      reloadAlbums && reloadAlbums();
      toast('Убрано из альбома');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function addToFavorites(m: MediaItem) {
    try {
      await apiPost('/api/cloud/favorites/add', { fileId: m.id });
      toast('Добавлено в избранное');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function replaceThumb(m: MediaItem) {
    const [img] = await pickFiles({ accept: 'image/*', multiple: false });
    if (!img) return;
    try {
      const up = await uploadFile(img);
      await apiPost('/api/cloud/video/thumbnail', { videoFileId: m.id, imageFileId: up.fileId });
      onItemPatched && onItemPatched(m.id, bustPreviews(m));
      toast('Превью видео обновлено');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function revealInFolder(m: MediaItem) {
    const entryId = m.entryIds && m.entryIds[0];
    if (!entryId) return;
    try {
      const p = await apiGet<{ segments: { id: string; name: string }[] }>('/api/cloud/path?entry=' + encodeURIComponent(entryId));
      navigate('/files', { state: { stack: p.segments, selectEntryId: entryId } });
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  function buildItems(m: MediaItem): ContextItem[] {
    const isMedia = m.kind === 'photo' || m.kind === 'video';
    const hasEntry = (m.entryIds || []).length > 0;
    const inAlbums = membership.of(m.id);
    const available = (albums || []).filter((a) => !inAlbums.has(a.id));
    const present = (albums || []).filter((a) => inAlbums.has(a.id));
    const out: ContextItem[] = [
      { label: 'Копировать ссылку', icon: 'link', onClick: () => copyLink(m) },
      { label: 'Создать публичную ссылку', icon: 'share', onClick: () => createShare(m.id, m.name, toast) },
      { label: 'Поделиться с пользователем', icon: 'user', onClick: () => setShareWith(m) },
      { label: 'Переименовать', icon: 'pencil', disabled: !hasEntry, onClick: () => setRename(m) },
      { label: 'Показать в папке', icon: 'folder', disabled: !hasEntry, onClick: () => revealInFolder(m) },
    ];
    if (m.kind === 'video') {
      out.push({ label: 'Заменить превью', icon: 'photo', onClick: () => replaceThumb(m) });
    }
    if (isMedia) {
      out.push({
        label: 'Добавить в альбом',
        icon: 'plus',
        submenu: available.length
          ? available.map((a) => ({ label: a.name, onClick: () => addToAlbum(m, a.id) }))
          : [{ label: albums && albums.length ? 'Уже во всех альбомах' : 'Нет альбомов', disabled: true }],
      });
      if (present.length) {
        out.push({
          label: 'Удалить из альбома',
          icon: 'x',
          submenu: present.map((a) => ({ label: a.name, onClick: () => removeFromAlbum(m, a.id) })),
        });
      }
    }
    out.push({ label: 'Добавить в избранное', icon: 'star', onClick: () => addToFavorites(m) });
    out.push({ label: 'Свойства', icon: 'info', onClick: () => setProps(m) });
    out.push({ divider: true });
    out.push({ label: 'Удалить', icon: 'trash', danger: true, onClick: () => setConfirm(m) });
    return out;
  }

  const api: MediaActionsApi = {
    albums: albums || [],
    membership,
    toast,
    copyTempLink: copyLink,
    createPublicLink: (m) => createShare(m.id, m.name, toast),
    shareWithUser: setShareWith,
    addToAlbum,
    removeFromAlbum,
    revealInFolder,
    showProperties: setProps,
    requestDelete: setConfirm,
  };

  const overlay = (
    <>
      {menu && <ContextMenu x={menu.x} y={menu.y} items={buildItems(menu.media)} onClose={() => setMenu(null)} />}
      {rename && (
        <RenameModal
          title="Переименовать"
          label="Имя"
          initial={(rename.entryNames && rename.entryNames[0]) || rename.name}
          onClose={() => setRename(null)}
          onSave={(name) => doRename(rename, name)}
        />
      )}
      {confirm && (
        <ConfirmModal
          title="Удалить в корзину?"
          danger
          confirmLabel="Удалить"
          message={`«${(confirm.entryNames && confirm.entryNames[0]) || confirm.name}» будет перемещён в корзину.`}
          onClose={() => setConfirm(null)}
          onConfirm={() => doDelete(confirm)}
        />
      )}
      {props && <PropertiesModal fileId={props.id} fallback={props} onClose={() => setProps(null)} />}
      {shareWith && (
        <ShareWithUserModal fileId={shareWith.id} fileName={shareWith.name} onClose={() => setShareWith(null)} toast={toast} />
      )}
    </>
  );

  return { overlay, openMenu, api };
}
