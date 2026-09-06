import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { useContextMenu, type ContextItem } from '../components/ui/ContextMenu';
import { Modal } from '../components/ui/Modal';
import { PropertiesModal } from '../components/ui/PropertiesModal';
import { RenameModal } from '../components/ui/RenameModal';
import { ShareWithUserModal } from '../components/ui/ShareWithUserModal';
import { useAudioPlayer } from '../hooks/useAudioPlayer';
import { usePageHeader } from '../hooks/usePageHeader';
import { useToast } from '../hooks/useToast';
import { apiGet, apiPost, pickFiles, uploadFile } from '../lib/api';
import { formatDuration } from '../lib/format';
import { createMusicPlaylistShare, createShare } from '../lib/share';
import type { SearchHit } from '../lib/search';
import type { MediaItem, MusicPlaylist, MusicPlaylistTrack, MusicTrack, Page, SharedMusicPlaylist } from '../lib/types';

type Tab = 'tracks' | 'playlists';

function trackDisplayName(track: MusicTrack): string {
  return track.file.entryNames?.[0] || track.title || track.file.name;
}

function titleFromEntryName(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(0, dot) : name;
}

function trackDurationLabel(seconds: number): string {
  return seconds > 0 ? formatDuration(seconds) : '—';
}

export function MusicPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const openTrackId = new URLSearchParams(location.search).get('track') || '';
  const openPlaylistId = new URLSearchParams(location.search).get('playlist') || '';
  const [tab, setTab] = React.useState<Tab>('tracks');
  const [tracks, setTracks] = React.useState<MusicTrack[]>([]);
  const [playlists, setPlaylists] = React.useState<MusicPlaylist[]>([]);
  const [sharedPlaylists, setSharedPlaylists] = React.useState<SharedMusicPlaylist[]>([]);
  const [detail, setDetail] = React.useState<{ playlist: MusicPlaylist; items: MusicPlaylistTrack[] } | null>(null);
  const [query, setQuery] = React.useState('');
  const [nextCursorAt, setNextCursorAt] = React.useState<string | null>(null);
  const [nextCursorId, setNextCursorId] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [loadingMore, setLoadingMore] = React.useState(false);
  const [creating, setCreating] = React.useState(false);
  const [name, setName] = React.useState('');
  const [addTrack, setAddTrack] = React.useState<MusicTrack | null>(null);
  const [coverTarget, setCoverTarget] = React.useState<MusicPlaylist | null>(null);
  const [shareWith, setShareWith] = React.useState<MusicPlaylist | null>(null);
  const [propsTrack, setPropsTrack] = React.useState<MusicTrack | null>(null);
  const [renameTrack, setRenameTrack] = React.useState<MusicTrack | null>(null);
  const [deleteTrack, setDeleteTrack] = React.useState<MusicTrack | null>(null);
  const [deletePlaylistTarget, setDeletePlaylistTarget] = React.useState<MusicPlaylist | null>(null);
  const [photos, setPhotos] = React.useState<MediaItem[]>([]);
  const [toastNode, toast] = useToast();
  const { menu, openAt } = useContextMenu();
  const player = useAudioPlayer();
  const trackCursorRef = React.useRef<{ at: string; id: string } | null>(null);
  const trackBusyRef = React.useRef(false);
  const trackRequestRef = React.useRef(0);
  const trackObserverRef = React.useRef<IntersectionObserver | null>(null);
  const resolvedDeepLink = React.useRef('');

  usePageHeader(() => ({
    title: 'Музыка',
    search: false,
    actions: (
      <>
        {tab === 'tracks' && (
          <label className="top-search music-search">
            <Icon.search size={18} />
            <input value={query} onChange={(e) => setQuery(e.currentTarget.value)} placeholder="Поиск по трекам" />
          </label>
        )}
        <button className="btn primary" onClick={() => { setName(''); setCreating(true); }}>
          <Icon.plus size={16} /> Плейлист
        </button>
      </>
    )
  }), [query, tab]);

  const loadTracks = React.useCallback(async (append: boolean) => {
    if (append && trackBusyRef.current) return;
    const cursor = append ? trackCursorRef.current : null;
    if (append && !cursor) return;

    const requestId = append ? trackRequestRef.current : trackRequestRef.current + 1;
    if (!append) trackRequestRef.current = requestId;
    else trackBusyRef.current = true;

    if (!append) {
      trackCursorRef.current = null;
      setNextCursorAt(null);
      setNextCursorId(null);
    }
    append ? setLoadingMore(true) : setLoading(true);
    try {
      const params = new URLSearchParams();
      if (query.trim()) params.set('q', query.trim());
      params.set('limit', '60');
      if (cursor) {
        params.set('cursorAt', cursor.at);
        params.set('cursorId', cursor.id);
      }
      const resp = await apiGet<Page<MusicTrack>>('/api/music/tracks?' + params.toString());
      if (requestId !== trackRequestRef.current) return;
      setTracks((prev) => append ? [...prev, ...resp.items] : resp.items);
      const next = resp.nextCursorAt && resp.nextCursorId ? { at: resp.nextCursorAt, id: resp.nextCursorId } : null;
      trackCursorRef.current = next;
      setNextCursorAt(next?.at ?? null);
      setNextCursorId(next?.id ?? null);
    } finally {
      if (append) trackBusyRef.current = false;
      if (append) setLoadingMore(false);
      else if (requestId === trackRequestRef.current) setLoading(false);
    }
  }, [query]);

  const loadPlaylists = React.useCallback(async () => {
    const [own, shared] = await Promise.all([
      apiGet<Page<MusicPlaylist>>('/api/music/playlists?limit=100'),
      apiGet<{ items: SharedMusicPlaylist[] }>('/api/music/shared/with-me'),
    ]);
    setPlaylists(own.items || []);
    setSharedPlaylists(shared.items || []);
  }, []);

  React.useEffect(() => {
    const t = window.setTimeout(() => loadTracks(false).catch(() => setLoading(false)), 180);
    return () => window.clearTimeout(t);
  }, [loadTracks]);

  const loadTracksRef = React.useRef(loadTracks);
  loadTracksRef.current = loadTracks;

  const trackSentinelRef = React.useCallback((node: HTMLDivElement | null) => {
    trackObserverRef.current?.disconnect();
    if (!node) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting) loadTracksRef.current(true).catch(() => {});
      },
      { rootMargin: '600px' },
    );
    io.observe(node);
    trackObserverRef.current = io;
  }, []);

  React.useEffect(() => () => trackObserverRef.current?.disconnect(), []);

  React.useEffect(() => {
    loadPlaylists().catch(() => {});
  }, [loadPlaylists]);

  const play = (track: MusicTrack, queue = tracks) => player.playQueue(queue, track.file.id);

  function patchTrack(fileId: string, patch: (track: MusicTrack) => MusicTrack) {
    setTracks((prev) => prev.map((track) => track.file.id === fileId ? patch(track) : track));
    setDetail((prev) => prev
      ? {
          ...prev,
          items: prev.items.map((item) => item.track.file.id === fileId ? { ...item, track: patch(item.track) } : item),
        }
      : prev);
  }

  async function createPlaylist() {
    const n = name.trim();
    if (!n) return;
    const created = await apiPost<MusicPlaylist>('/api/music/playlists', { name: n });
    setPlaylists((prev) => [created, ...prev]);
    setCreating(false);
    setTab('playlists');
  }

  function nextCopyName(source: MusicPlaylist) {
    const base = `${source.name} (копия)`;
    const names = new Set(playlists.map((p) => p.name));
    if (!names.has(base)) return base;

    let n = 2;
    while (names.has(`${source.name} (копия ${n})`)) n++;
    return `${source.name} (копия ${n})`;
  }

  async function openPlaylist(playlist: MusicPlaylist) {
    const resp = await apiGet<{ playlist: MusicPlaylist; items: MusicPlaylistTrack[] }>(
      '/api/music/playlists/tracks?playlistId=' + encodeURIComponent(playlist.id),
    );
    setDetail(resp);
  }

  React.useEffect(() => {
    if (!openPlaylistId || !playlists.length || resolvedDeepLink.current === `playlist:${openPlaylistId}`) return;
    resolvedDeepLink.current = `playlist:${openPlaylistId}`;
    const playlist = playlists.find((item) => item.id === openPlaylistId);
    if (playlist) {
      setTab('playlists');
      openPlaylist(playlist).catch((e) => toast((e as Error).message || 'Плейлист больше недоступен', 'err'));
      return;
    }
    apiGet(`/api/search/hit?kind=playlist&id=${encodeURIComponent(openPlaylistId)}`)
      .then(() => toast('Плейлист больше недоступен', 'err'))
      .catch((e) => toast((e as Error).message || 'Плейлист больше недоступен', 'err'))
      .finally(() => navigate('/music', { replace: true }));
  }, [openPlaylistId, playlists, navigate, toast]);

  React.useEffect(() => {
    if (!openTrackId || resolvedDeepLink.current === `track:${openTrackId}`) return;
    const known = tracks.find((item) => item.file.id === openTrackId);
    if (known) {
      resolvedDeepLink.current = `track:${openTrackId}`;
      setTab('tracks');
      play(known);
      return;
    }
    resolvedDeepLink.current = `track:${openTrackId}`;
    apiGet<SearchHit>(`/api/search/hit?kind=track&id=${encodeURIComponent(openTrackId)}`)
      .then((hit) => apiGet<Page<MusicTrack>>('/api/music/tracks?q=' + encodeURIComponent(hit.title) + '&limit=200'))
      .then((page) => {
        const track = page.items.find((item) => item.file.id === openTrackId);
        if (track) {
          setTracks((current) => current.some((item) => item.file.id === track.file.id) ? current : [track, ...current]);
          setTab('tracks');
          play(track, [track]);
        } else {
          toast('Трек больше недоступен', 'err');
          navigate('/music', { replace: true });
        }
      })
      .catch((e) => { toast((e as Error).message || 'Трек больше недоступен', 'err'); navigate('/music', { replace: true }); });
  }, [openTrackId, tracks, navigate, toast]);

  async function duplicatePlaylist(playlist: MusicPlaylist) {
    try {
      const source = await apiGet<{ playlist: MusicPlaylist; items: MusicPlaylistTrack[] }>(
        '/api/music/playlists/tracks?playlistId=' + encodeURIComponent(playlist.id),
      );
      let created = await apiPost<MusicPlaylist>('/api/music/playlists', {
        name: nextCopyName(playlist),
        description: playlist.description,
      });
      const fileIds = source.items.map((item) => item.track.file.id);
      if (fileIds.length) {
        await apiPost('/api/music/playlists/tracks/add', { playlistId: created.id, fileIds });
      }
      if (playlist.coverFileId) {
        created = await apiPost<MusicPlaylist>('/api/music/playlists/update', {
          playlistId: created.id,
          coverFileId: playlist.coverFileId,
        });
      }
      setPlaylists((prev) => [created, ...prev]);
      await loadPlaylists();
      toast('Дубликат плейлиста создан');
    } catch (e) {
      toast((e as Error).message || 'Не удалось создать дубликат', 'err');
    }
  }

  async function addToPlaylist(playlistId: string, fileId: string) {
    try {
      await apiPost('/api/music/playlists/tracks/add', { playlistId, fileIds: [fileId] });
      setAddTrack(null);
      await loadPlaylists();
      if (detail?.playlist.id === playlistId) await openPlaylist(detail.playlist);
      toast('Трек добавлен в плейлист');
    } catch (e) {
      toast((e as Error).message || 'Не удалось добавить трек', 'err');
    }
  }

  async function removeFromPlaylist(fileId: string) {
    if (!detail) return;
    try {
      await apiPost('/api/music/playlists/tracks/remove', { playlistId: detail.playlist.id, fileIds: [fileId] });
      await openPlaylist(detail.playlist);
      await loadPlaylists();
      toast('Трек убран из плейлиста');
    } catch (e) {
      toast((e as Error).message || 'Не удалось убрать трек', 'err');
    }
  }

  async function deletePlaylist(playlist: MusicPlaylist) {
    try {
      await apiPost('/api/music/playlists/delete', { playlistId: playlist.id });
      setDeletePlaylistTarget(null);
      setPlaylists((prev) => prev.filter((p) => p.id !== playlist.id));
      if (detail?.playlist.id === playlist.id) setDetail(null);
      toast('Плейлист удалён');
    } catch (e) {
      toast((e as Error).message || 'Не удалось удалить плейлист', 'err');
    }
  }

  async function renameTrackEntry(track: MusicTrack, newName: string) {
    const entryId = track.file.entryIds?.[0];
    if (!entryId) {
      toast('Трек не привязан к папке', 'err');
      return;
    }

    try {
      await apiPost('/api/cloud/entry/rename', { entryId, name: newName });
      setRenameTrack(null);
      patchTrack(track.file.id, (item) => ({
        ...item,
        title: titleFromEntryName(newName),
        file: {
          ...item.file,
          entryNames: [newName, ...(item.file.entryNames || []).slice(1)],
        },
      }));
      toast('Переименовано');
    } catch (e) {
      toast((e as Error).message || 'Не удалось переименовать трек', 'err');
    }
  }

  async function revealTrack(track: MusicTrack) {
    const entryId = track.file.entryIds?.[0];
    if (!entryId) {
      toast('Трек не привязан к папке', 'err');
      return;
    }

    try {
      const path = await apiGet<{ segments: { id: string; name: string }[] }>(
        '/api/cloud/path?entry=' + encodeURIComponent(entryId),
      );
      navigate('/files', { state: { stack: path.segments, selectEntryId: entryId } });
    } catch (e) {
      toast((e as Error).message || 'Не удалось открыть папку', 'err');
    }
  }

  async function deleteTrackFile(track: MusicTrack) {
    try {
      await apiPost('/api/cloud/media/delete', { fileId: track.file.id });
      setDeleteTrack(null);
      setTracks((prev) => prev.filter((item) => item.file.id !== track.file.id));
      setDetail((prev) => prev
        ? { ...prev, items: prev.items.filter((item) => item.track.file.id !== track.file.id) }
        : prev);
      await loadPlaylists();
      toast('Файл перемещён в корзину');
    } catch (e) {
      toast((e as Error).message || 'Не удалось удалить файл', 'err');
    }
  }

  function trackMenu(track: MusicTrack, opts: { canManageFile?: boolean; canRemoveFromPlaylist?: boolean } = {}): ContextItem[] {
    const canManageFile = opts.canManageFile ?? true;
    const hasEntry = (track.file.entryIds || []).length > 0;
    const playlistItems = playlists.length
      ? playlists.map((playlist) => ({ label: playlist.name, onClick: () => addToPlaylist(playlist.id, track.file.id) }))
      : [{ label: 'Нет плейлистов', disabled: true }];
    const items: ContextItem[] = [
      { label: 'Добавить в плейлист', icon: 'plus', submenu: playlistItems },
      { label: 'Свойства', icon: 'info', onClick: () => setPropsTrack(track) },
      { label: 'Публичная ссылка', icon: 'link', onClick: () => createShare(track.file.id, trackDisplayName(track), toast) },
      { label: 'Переименовать', icon: 'pencil', disabled: !canManageFile || !hasEntry, onClick: () => setRenameTrack(track) },
      { label: 'Показать в папке', icon: 'folder', disabled: !canManageFile || !hasEntry, onClick: () => revealTrack(track) },
    ];

    if (opts.canRemoveFromPlaylist) {
      items.push({ label: 'Убрать из плейлиста', icon: 'x', onClick: () => removeFromPlaylist(track.file.id) });
    }

    if (canManageFile) {
      items.push({ divider: true });
      items.push({ label: 'Удалить файл', icon: 'trash', danger: true, onClick: () => setDeleteTrack(track) });
    }

    return items;
  }

  function playlistMenu(playlist: MusicPlaylist): ContextItem[] {
    return [
      { label: 'Открыть', icon: 'music', onClick: () => openPlaylist(playlist) },
      { label: 'Создать дубликат', icon: 'copy', onClick: () => duplicatePlaylist(playlist) },
      { label: 'Сменить обложку', icon: 'photo', onClick: () => openCoverPicker(playlist) },
      { label: 'Публичная ссылка', icon: 'link', onClick: () => createPublicShare(playlist) },
      { label: 'Поделиться с пользователем', icon: 'share', onClick: () => setShareWith(playlist) },
      { divider: true },
      { label: 'Удалить плейлист', icon: 'trash', danger: true, onClick: () => setDeletePlaylistTarget(playlist) },
    ];
  }

  async function moveTrack(fileId: string, direction: -1 | 1) {
    if (!detail) return;
    const ids = detail.items.map((i) => i.track.file.id);
    const i = ids.indexOf(fileId);
    const j = i + direction;
    if (i < 0 || j < 0 || j >= ids.length) return;
    [ids[i], ids[j]] = [ids[j], ids[i]];
    await apiPost('/api/music/playlists/tracks/reorder', { playlistId: detail.playlist.id, fileIds: ids });
    await openPlaylist(detail.playlist);
  }

  async function chooseCover(fileId: string) {
    if (!coverTarget) return;
    const updated = await apiPost<MusicPlaylist>('/api/music/playlists/update', { playlistId: coverTarget.id, coverFileId: fileId });
    setPlaylists((prev) => prev.map((p) => p.id === updated.id ? updated : p));
    if (detail?.playlist.id === updated.id) await openPlaylist(updated);
    setCoverTarget(null);
  }

  async function openCoverPicker(playlist: MusicPlaylist) {
    setCoverTarget(playlist);
    const resp = await apiGet<Page<MediaItem>>('/api/cloud/media?kind=photo&limit=80');
    setPhotos(resp.items || []);
  }

  async function uploadCover() {
    const picked = await pickFiles({ accept: 'image/*', multiple: false });
    if (!picked.length) return;
    const uploaded = await uploadFile(picked[0]);
    await chooseCover(uploaded.fileId);
  }

  function createPublicShare(playlist: MusicPlaylist) {
    createMusicPlaylistShare(playlist.id, playlist.name, toast);
  }

  return (
    <div className="music-page">
      <div className="music-tabs">
        <button className={tab === 'tracks' ? 'active' : ''} onClick={() => setTab('tracks')}>Треки</button>
        <button className={tab === 'playlists' ? 'active' : ''} onClick={() => setTab('playlists')}>Плейлисты</button>
      </div>

      {tab === 'tracks' ? (
        <TracksView
          tracks={tracks}
          loading={loading}
          query={query}
          playerCurrentId={player.current?.file.id}
          isPlaying={player.isPlaying}
          onPlay={(track) => play(track)}
          onAdd={setAddTrack}
          onMenu={(e, track) => openAt(e, trackMenu(track))}
          hasMore={!!nextCursorAt && !!nextCursorId}
          loadingMore={loadingMore}
          sentinelRef={trackSentinelRef}
        />
      ) : detail ? (
        <PlaylistDetail
          detail={detail}
          currentId={player.current?.file.id}
          isPlaying={player.isPlaying}
          onBack={() => setDetail(null)}
          onPlay={(track) => play(track, detail.items.map((i) => i.track))}
          onRemove={removeFromPlaylist}
          onMove={moveTrack}
          onCover={() => openCoverPicker(detail.playlist)}
          onPublicShare={() => createPublicShare(detail.playlist)}
          onShareWith={() => setShareWith(detail.playlist)}
          onTrackMenu={(e, track) => openAt(e, trackMenu(track, { canManageFile: detail.playlist.canReorder, canRemoveFromPlaylist: detail.playlist.canReorder }))}
          onPlaylistMenu={(e) => openAt(e, playlistMenu(detail.playlist))}
        />
      ) : (
        <PlaylistsView
          playlists={playlists}
          shared={sharedPlaylists}
          onOpen={openPlaylist}
          onCover={openCoverPicker}
          onPublicShare={createPublicShare}
          onShareWith={setShareWith}
          onMenu={(e, playlist) => openAt(e, playlistMenu(playlist))}
        />
      )}

      {creating && (
        <Modal
          title="Новый плейлист"
          onClose={() => setCreating(false)}
          actions={<><button className="btn text" onClick={() => setCreating(false)}>Отмена</button><button className="btn primary" onClick={createPlaylist}>Создать</button></>}
        >
          <label className="field-label">Название</label>
          <input type="text" value={name} autoFocus onChange={(e) => setName(e.currentTarget.value)} onKeyDown={(e) => { if (e.key === 'Enter') createPlaylist(); }} />
        </Modal>
      )}

      {renameTrack && (
        <RenameModal
          title="Переименовать трек"
          label="Имя в папке"
          initial={trackDisplayName(renameTrack)}
          onClose={() => setRenameTrack(null)}
          onSave={(newName) => renameTrackEntry(renameTrack, newName)}
        />
      )}

      {deleteTrack && (
        <ConfirmModal
          title="Удалить файл?"
          danger
          confirmLabel="Удалить"
          message={`«${trackDisplayName(deleteTrack)}» будет перемещён в корзину.`}
          onClose={() => setDeleteTrack(null)}
          onConfirm={() => deleteTrackFile(deleteTrack)}
        />
      )}

      {deletePlaylistTarget && (
        <ConfirmModal
          title="Удалить плейлист?"
          danger
          confirmLabel="Удалить"
          message={`Плейлист «${deletePlaylistTarget.name}» будет удалён. Файлы треков останутся в облаке.`}
          onClose={() => setDeletePlaylistTarget(null)}
          onConfirm={() => deletePlaylist(deletePlaylistTarget)}
        />
      )}

      {addTrack && (
        <Modal title="Добавить в плейлист" onClose={() => setAddTrack(null)}>
          <div className="playlist-pick-list">
            {playlists.length === 0 ? <EmptyState icon="music" title="Плейлистов пока нет" /> : playlists.map((p) => (
              <button key={p.id} className="playlist-pick-row" onClick={() => addToPlaylist(p.id, addTrack.file.id)}>
                <span>{p.name}</span>
                <span>{p.count}</span>
              </button>
            ))}
          </div>
        </Modal>
      )}

      {coverTarget && (
        <Modal
          title="Обложка плейлиста"
          onClose={() => setCoverTarget(null)}
          actions={<button className="btn outlined" onClick={uploadCover}><Icon.upload size={16} /> Загрузить изображение</button>}
        >
          <div className="cover-pick-grid">
            {photos.map((p) => {
              const url = p.previews[p.previews.length - 1]?.url;
              return (
                <button key={p.id} onClick={() => chooseCover(p.id)}>
                  {url ? <img src={url} alt="" /> : <Icon.photo size={24} />}
                </button>
              );
            })}
          </div>
        </Modal>
      )}

      {shareWith && (
        <ShareWithUserModal
          playlistId={shareWith.id}
          fileName={shareWith.name}
          onClose={() => setShareWith(null)}
          toast={toast}
        />
      )}

      {propsTrack && <PropertiesModal fileId={propsTrack.file.id} fallback={propsTrack.file} onClose={() => setPropsTrack(null)} />}
      {menu}
      {toastNode}
    </div>
  );
}

function TracksView(props: {
  tracks: MusicTrack[];
  loading: boolean;
  query: string;
  playerCurrentId?: string;
  isPlaying: boolean;
  onPlay: (track: MusicTrack) => void;
  onAdd: (track: MusicTrack) => void;
  onMenu: (e: React.MouseEvent, track: MusicTrack) => void;
  hasMore: boolean;
  loadingMore: boolean;
  sentinelRef: (node: HTMLDivElement | null) => void;
}) {
  if (props.loading) return <Loading label="Загрузка музыки..." />;
  if (props.tracks.length === 0) {
    return (
      <EmptyState
        icon="music"
        title={props.query.trim() ? 'Ничего не найдено' : 'Музыка ещё не загружена'}
        hint={props.query.trim() ? 'Попробуйте другой запрос.' : 'Загрузите аудиофайлы в облако, и они появятся здесь.'}
      />
    );
  }

  return (
    <>
      <div className="track-list">
        {props.tracks.map((track, idx) => (
          <TrackRow
            key={track.file.id}
            track={track}
            index={idx}
            active={props.playerCurrentId === track.file.id}
            playing={props.isPlaying}
            onPlay={() => props.onPlay(track)}
            onAdd={() => props.onAdd(track)}
            onMenu={(e) => props.onMenu(e, track)}
          />
        ))}
      </div>
      {props.hasMore && (
        <div className="infinite-sentinel" ref={props.sentinelRef}>
          {props.loadingMore && <Loading label="Загрузка..." />}
        </div>
      )}
    </>
  );
}

function TrackRow({ track, index, active, playing, onPlay, onAdd, onMenu }: {
  track: MusicTrack;
  index: number;
  active: boolean;
  playing: boolean;
  onPlay: () => void;
  onAdd?: () => void;
  onMenu?: (e: React.MouseEvent) => void;
}) {
  return (
    <div className={'track-row' + (active ? ' active' : '')} onContextMenu={onMenu}>
      <button className="track-play-hit" onClick={onPlay}>
        <span className="track-index">{active && playing ? <Icon.pause size={16} /> : index + 1}</span>
        <span className="track-cover">{track.coverUrl ? <img src={track.coverUrl} alt="" /> : <Icon.music size={20} />}</span>
        <span className="track-main">
          <span className="track-title">{track.title || track.file.name}</span>
          <span className="track-sub">{track.artist || 'Неизвестный исполнитель'}</span>
        </span>
        <span className="track-album">{track.album}</span>
        <span className="track-duration">{trackDurationLabel(track.duration)}</span>
      </button>
      {onAdd && <button className="icon-btn" title="Добавить в плейлист" onClick={onAdd}><Icon.plus size={16} /></button>}
    </div>
  );
}

function PlaylistsView({ playlists, shared, onOpen, onCover, onPublicShare, onShareWith, onMenu }: {
  playlists: MusicPlaylist[];
  shared: SharedMusicPlaylist[];
  onOpen: (playlist: MusicPlaylist) => void;
  onCover: (playlist: MusicPlaylist) => void;
  onPublicShare: (playlist: MusicPlaylist) => void;
  onShareWith: (playlist: MusicPlaylist) => void;
  onMenu: (e: React.MouseEvent, playlist: MusicPlaylist) => void;
}) {
  if (!playlists.length && !shared.length) return <EmptyState icon="music" title="Плейлистов пока нет" hint="Создайте первый плейлист и добавьте в него треки." />;
  return (
    <>
      {playlists.length > 0 && (
        <>
          <div className="music-section-title">Мои плейлисты</div>
          <div className="music-playlist-grid">
            {playlists.map((p) => (
              <PlaylistCard
                key={p.id}
                playlist={p}
                onOpen={onOpen}
                onMenu={(e) => onMenu(e, p)}
                actions={
                  <>
                    <button className="icon-btn" title="Публичная ссылка" onClick={() => onPublicShare(p)}><Icon.link size={16} /></button>
                    <button className="icon-btn" title="Поделиться с пользователем" onClick={() => onShareWith(p)}><Icon.share size={16} /></button>
                    <button className="icon-btn" title="Обложка" onClick={() => onCover(p)}><Icon.photo size={16} /></button>
                  </>
                }
              />
            ))}
          </div>
        </>
      )}
      {shared.length > 0 && (
        <>
          <div className="music-section-title">Доступные мне</div>
          <div className="music-playlist-grid">
            {shared.map((item) => (
              <PlaylistCard
                key={item.grantId}
                playlist={item.playlist}
                onOpen={onOpen}
                meta={`от пользователя #${item.ownerUserId}`}
              />
            ))}
          </div>
        </>
      )}
    </>
  );
}

function PlaylistCard({ playlist, onOpen, actions, meta, onMenu }: {
  playlist: MusicPlaylist;
  onOpen: (playlist: MusicPlaylist) => void;
  actions?: React.ReactNode;
  meta?: string;
  onMenu?: (e: React.MouseEvent) => void;
}) {
  return (
    <div className="music-playlist-card" onContextMenu={onMenu}>
      <button className="music-playlist-cover" onClick={() => onOpen(playlist)}>
        {playlist.coverUrl ? <img src={playlist.coverUrl} alt="" /> : <Icon.music size={34} />}
      </button>
      <button className="music-playlist-name" onClick={() => onOpen(playlist)}>{playlist.name}</button>
      <div className="music-playlist-meta">{meta || `${playlist.count} треков`}</div>
      {actions && <div className="music-playlist-actions">{actions}</div>}
    </div>
  );
}

function PlaylistDetail({ detail, currentId, isPlaying, onBack, onPlay, onRemove, onMove, onCover, onPublicShare, onShareWith, onTrackMenu, onPlaylistMenu }: {
  detail: { playlist: MusicPlaylist; items: MusicPlaylistTrack[] };
  currentId?: string;
  isPlaying: boolean;
  onBack: () => void;
  onPlay: (track: MusicTrack) => void;
  onRemove: (fileId: string) => void;
  onMove: (fileId: string, direction: -1 | 1) => void;
  onCover: () => void;
  onPublicShare: () => void;
  onShareWith: () => void;
  onTrackMenu: (e: React.MouseEvent, track: MusicTrack) => void;
  onPlaylistMenu: (e: React.MouseEvent) => void;
}) {
  const own = detail.playlist.canReorder;
  return (
    <div className="playlist-detail">
      <div className="playlist-head" onContextMenu={own ? onPlaylistMenu : undefined}>
        <button className="icon-btn" onClick={onBack}><Icon.arrow size={18} style={{ transform: 'rotate(180deg)' }} /></button>
        <div className="playlist-hero-cover">{detail.playlist.coverUrl ? <img src={detail.playlist.coverUrl} alt="" /> : <Icon.music size={38} />}</div>
        <div>
          <h2>{detail.playlist.name}</h2>
          <p>{detail.playlist.count} треков</p>
        </div>
        {own && (
          <div className="playlist-head-actions">
            <button className="btn outlined" onClick={onPublicShare}><Icon.link size={16} /> Ссылка</button>
            <button className="btn outlined" onClick={onShareWith}><Icon.share size={16} /> Поделиться</button>
            <button className="btn outlined" onClick={onCover}>Обложка</button>
          </div>
        )}
      </div>
      <div className="track-list">
        {detail.items.map((item, idx) => (
          <div className={'playlist-track-line' + (own ? '' : ' readonly')} key={item.track.file.id}>
            <TrackRow
              track={item.track}
              index={idx}
              active={currentId === item.track.file.id}
              playing={isPlaying}
              onPlay={() => onPlay(item.track)}
              onMenu={own ? (e) => onTrackMenu(e, item.track) : undefined}
            />
            {own && (
              <>
                <button className="icon-btn" disabled={idx === 0} onClick={() => onMove(item.track.file.id, -1)}>↑</button>
                <button className="icon-btn" disabled={idx === detail.items.length - 1} onClick={() => onMove(item.track.file.id, 1)}>↓</button>
                <button className="icon-btn" onClick={() => onRemove(item.track.file.id)}><Icon.x size={16} /></button>
              </>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
