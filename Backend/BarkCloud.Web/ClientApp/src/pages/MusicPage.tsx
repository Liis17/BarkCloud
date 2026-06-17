import React from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { useAudioPlayer } from '../hooks/useAudioPlayer';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet } from '../lib/api';
import { formatDuration } from '../lib/format';
import type { MusicTrack, Page } from '../lib/types';

export function MusicPage() {
  const [tracks, setTracks] = React.useState<MusicTrack[]>([]);
  const [query, setQuery] = React.useState('');
  const [nextCursorAt, setNextCursorAt] = React.useState<string | null>(null);
  const [nextCursorId, setNextCursorId] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [loadingMore, setLoadingMore] = React.useState(false);
  const player = useAudioPlayer();

  usePageHeader(() => ({
    title: 'Музыка',
    search: false,
    actions: (
      <label className="top-search music-search">
        <Icon.search size={18} />
        <input value={query} onChange={(e) => setQuery(e.currentTarget.value)} placeholder="Поиск по трекам" />
      </label>
    )
  }), [query]);

  const load = React.useCallback(async (append: boolean) => {
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

  React.useEffect(() => {
    const t = window.setTimeout(() => load(false).catch(() => setLoading(false)), 180);
    return () => window.clearTimeout(t);
  }, [load]);

  const play = (track: MusicTrack) => player.playQueue(tracks, track.file.id);

  return (
    <div className="music-page">
      {loading ? (
        <Loading label="Загрузка музыки..." />
      ) : tracks.length === 0 ? (
        <EmptyState
          icon="music"
          title={query.trim() ? 'Ничего не найдено' : 'Музыка ещё не загружена'}
          hint={query.trim() ? 'Попробуйте другой запрос.' : 'Загрузите аудиофайлы в облако, и они появятся здесь.'}
        />
      ) : (
        <>
          <div className="track-list">
            {tracks.map((track, idx) => {
              const isCurrent = player.current?.file.id === track.file.id;
              return (
                <button
                  key={track.file.id}
                  className={'track-row' + (isCurrent ? ' active' : '')}
                  onClick={() => play(track)}
                >
                  <span className="track-index">{isCurrent && player.isPlaying ? <Icon.pause size={16} /> : idx + 1}</span>
                  <span className="track-cover">
                    {track.coverUrl ? <img src={track.coverUrl} alt="" /> : <Icon.music size={20} />}
                  </span>
                  <span className="track-main">
                    <span className="track-title">{track.title || track.file.name}</span>
                    <span className="track-sub">{track.artist || 'Неизвестный исполнитель'}</span>
                  </span>
                  <span className="track-album">{track.album}</span>
                  <span className="track-duration">{formatDuration(track.duration)}</span>
                </button>
              );
            })}
          </div>
          {nextCursorAt && nextCursorId && (
            <div className="load-more-wrap">
              <button className="btn ghost" disabled={loadingMore} onClick={() => load(true)}>
                {loadingMore ? 'Загрузка...' : 'Показать ещё'}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
