import React from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { Modal } from '../components/ui/Modal';
import { ShareWithUserModal } from '../components/ui/ShareWithUserModal';
import { useAudioPlayer } from '../hooks/useAudioPlayer';
import { usePageHeader } from '../hooks/usePageHeader';
import { useToast } from '../hooks/useToast';
import { apiGet, apiPost, pickFiles, uploadFile } from '../lib/api';
import { formatDuration } from '../lib/format';
import { createMusicPlaylistShare } from '../lib/share';
import type { MediaItem, MusicPlaylist, MusicPlaylistTrack, MusicTrack, Page, SharedMusicPlaylist } from '../lib/types';

type Tab = 'tracks' | 'playlists';

export function MusicPage() {
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
  const [photos, setPhotos] = React.useState<MediaItem[]>([]);
  const [toastNode, toast] = useToast();
  const player = useAudioPlayer();

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
    append ? setLoadingMore(true) : setLoading(true);
    try {
      const params = new URLSearchParams();
      if (query.trim()) params.set('q', query.trim());
      params.set('limit', '60');
      if (append && nextCursorAt && nextCursorId) {
        params.set('cursorAt', nextCursorAt);
        params.set('cursorId', nextCursorId);
      }
      const resp = await apiGet<Page<MusicTrack>>('/api/music/tracks?' + params.toString());
      setTracks((prev) => append ? [...prev, ...resp.items] : resp.items);
      setNextCursorAt(resp.nextCursorAt);
      setNextCursorId(resp.nextCursorId);
    } finally {
      append ? setLoadingMore(false) : setLoading(false);
    }
  }, [nextCursorAt, nextCursorId, query]);

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

  React.useEffect(() => {
    loadPlaylists().catch(() => {});
  }, [loadPlaylists]);

  const play = (track: MusicTrack, queue = tracks) => player.playQueue(queue, track.file.id);

  async function createPlaylist() {
    const n = name.trim();
    if (!n) return;
    const created = await apiPost<MusicPlaylist>('/api/music/playlists', { name: n });
    setPlaylists((prev) => [created, ...prev]);
    setCreating(false);
    setTab('playlists');
  }

  async function openPlaylist(playlist: MusicPlaylist) {
    const resp = await apiGet<{ playlist: MusicPlaylist; items: MusicPlaylistTrack[] }>(
      '/api/music/playlists/tracks?playlistId=' + encodeURIComponent(playlist.id),
    );
    setDetail(resp);
  }

  async function addToPlaylist(playlistId: string, fileId: string) {
    await apiPost('/api/music/playlists/tracks/add', { playlistId, fileIds: [fileId] });
    setAddTrack(null);
    await loadPlaylists();
    if (detail?.playlist.id === playlistId) await openPlaylist(detail.playlist);
  }

  async function removeFromPlaylist(fileId: string) {
    if (!detail) return;
    await apiPost('/api/music/playlists/tracks/remove', { playlistId: detail.playlist.id, fileIds: [fileId] });
    await openPlaylist(detail.playlist);
    await loadPlaylists();
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
          nextCursorAt={nextCursorAt}
          nextCursorId={nextCursorId}
          loadingMore={loadingMore}
          loadMore={() => loadTracks(true)}
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
        />
      ) : (
        <PlaylistsView
          playlists={playlists}
          shared={sharedPlaylists}
          onOpen={openPlaylist}
          onCover={openCoverPicker}
          onPublicShare={createPublicShare}
          onShareWith={setShareWith}
        />
      )}

      {creating && (
        <Modal
          title="Новый плейлист"
          onClose={() => setCreating(false)}
          actions={<><button className="btn text" onClick={() => setCreating(false)}>Отмена</button><button className="btn primary" onClick={createPlaylist}>Создать</button></>}
        >
          <label className="field-label">Название</label>
          <input value={name} autoFocus onChange={(e) => setName(e.currentTarget.value)} onKeyDown={(e) => { if (e.key === 'Enter') createPlaylist(); }} />
        </Modal>
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
  nextCursorAt: string | null;
  nextCursorId: string | null;
  loadingMore: boolean;
  loadMore: () => void;
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
          />
        ))}
      </div>
      {props.nextCursorAt && props.nextCursorId && (
        <div className="load-more-wrap">
          <button className="btn ghost" disabled={props.loadingMore} onClick={props.loadMore}>
            {props.loadingMore ? 'Загрузка...' : 'Показать ещё'}
          </button>
        </div>
      )}
    </>
  );
}

function TrackRow({ track, index, active, playing, onPlay, onAdd }: {
  track: MusicTrack;
  index: number;
  active: boolean;
  playing: boolean;
  onPlay: () => void;
  onAdd?: () => void;
}) {
  return (
    <div className={'track-row' + (active ? ' active' : '')}>
      <button className="track-play-hit" onClick={onPlay}>
        <span className="track-index">{active && playing ? <Icon.pause size={16} /> : index + 1}</span>
        <span className="track-cover">{track.coverUrl ? <img src={track.coverUrl} alt="" /> : <Icon.music size={20} />}</span>
        <span className="track-main">
          <span className="track-title">{track.title || track.file.name}</span>
          <span className="track-sub">{track.artist || 'Неизвестный исполнитель'}</span>
        </span>
        <span className="track-album">{track.album}</span>
        <span className="track-duration">{formatDuration(track.duration)}</span>
      </button>
      {onAdd && <button className="icon-btn" title="Добавить в плейлист" onClick={onAdd}><Icon.plus size={16} /></button>}
    </div>
  );
}

function PlaylistsView({ playlists, shared, onOpen, onCover, onPublicShare, onShareWith }: {
  playlists: MusicPlaylist[];
  shared: SharedMusicPlaylist[];
  onOpen: (playlist: MusicPlaylist) => void;
  onCover: (playlist: MusicPlaylist) => void;
  onPublicShare: (playlist: MusicPlaylist) => void;
  onShareWith: (playlist: MusicPlaylist) => void;
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

function PlaylistCard({ playlist, onOpen, actions, meta }: {
  playlist: MusicPlaylist;
  onOpen: (playlist: MusicPlaylist) => void;
  actions?: React.ReactNode;
  meta?: string;
}) {
  return (
    <div className="music-playlist-card">
      <button className="music-playlist-cover" onClick={() => onOpen(playlist)}>
        {playlist.coverUrl ? <img src={playlist.coverUrl} alt="" /> : <Icon.music size={34} />}
      </button>
      <button className="music-playlist-name" onClick={() => onOpen(playlist)}>{playlist.name}</button>
      <div className="music-playlist-meta">{meta || `${playlist.count} треков`}</div>
      {actions && <div className="music-playlist-actions">{actions}</div>}
    </div>
  );
}

function PlaylistDetail({ detail, currentId, isPlaying, onBack, onPlay, onRemove, onMove, onCover, onPublicShare, onShareWith }: {
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
}) {
  const own = detail.playlist.canReorder;
  return (
    <div className="playlist-detail">
      <div className="playlist-head">
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
            <TrackRow track={item.track} index={idx} active={currentId === item.track.file.id} playing={isPlaying} onPlay={() => onPlay(item.track)} />
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
