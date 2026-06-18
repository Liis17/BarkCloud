import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { useDocumentHead } from '../hooks/useDocumentHead';
import { formatDuration } from '../lib/format';
import type { MusicTrack } from '../lib/types';

interface PlaylistListing {
  found: boolean;
  playlistName: string;
  description: string;
  coverUrl: string;
  items: MusicTrack[];
}

function persistAudioVolumeRef(audio: HTMLAudioElement | null): void {
  if (!audio || (audio as unknown as { _volBound?: boolean })._volBound) return;
  (audio as unknown as { _volBound?: boolean })._volBound = true;

  const m = document.cookie.match(/(?:^|;\s*)bark_audio_vol=([^;]+)/);
  if (m) {
    try {
      const p = JSON.parse(decodeURIComponent(m[1]));
      const volume = Math.min(1, Math.max(0, Number(p.v)));
      audio.volume = Number.isNaN(volume) ? 0.8 : volume;
      audio.muted = !!p.m;
    } catch {
      audio.volume = 0.8;
    }
  }
  audio.addEventListener('volumechange', () => {
    const payload = encodeURIComponent(JSON.stringify({ v: audio.volume, m: audio.muted }));
    document.cookie = `bark_audio_vol=${payload}; path=/; max-age=31536000; samesite=lax`;
  });
}

function trackDurationLabel(seconds: number): string {
  return seconds > 0 ? formatDuration(seconds) : '—';
}

export function PublicMusicPlaylistPage() {
  const { token } = useParams<{ token: string }>();
  const [name, setName] = React.useState('');
  const [description, setDescription] = React.useState('');
  const [coverUrl, setCoverUrl] = React.useState('');
  const [tracks, setTracks] = React.useState<MusicTrack[]>([]);
  const [state, setState] = React.useState<'loading' | 'notfound' | 'ok'>('loading');
  const [currentId, setCurrentId] = React.useState<string | null>(null);
  const audioRef = React.useRef<HTMLAudioElement | null>(null);
  const current = tracks.find((t) => t.file.id === currentId) || null;

  useDocumentHead(
    () => ({ title: name || (state === 'notfound' ? 'Плейлист недоступен' : 'Музыкальный плейлист'), iconUrl: coverUrl || current?.coverUrl }),
    [name, state, coverUrl, current?.coverUrl],
  );

  React.useEffect(() => {
    let alive = true;
    setState('loading');
    fetch(`/mpl/${token}/list`)
      .then((r) => {
        if (!r.ok) throw new Error('not found');
        return r.json() as Promise<PlaylistListing>;
      })
      .then((d) => {
        if (!alive || !d.found) return;
        setName(d.playlistName);
        setDescription(d.description);
        setCoverUrl(d.coverUrl);
        setTracks(d.items || []);
        setState('ok');
      })
      .catch(() => alive && setState('notfound'));
    return () => {
      alive = false;
    };
  }, [token]);

  function play(track: MusicTrack) {
    setCurrentId(track.file.id);
    window.setTimeout(() => audioRef.current?.play().catch(() => {}), 0);
  }

  function playNext() {
    if (!currentId || tracks.length === 0) return;
    const idx = tracks.findIndex((t) => t.file.id === currentId);
    const next = tracks[(idx + 1) % tracks.length];
    if (next) play(next);
  }

  if (state === 'loading') return <PublicStatus icon={Icon.music} title="Открываем плейлист" loading />;
  if (state === 'notfound') {
    return (
      <PublicStatus
        icon={Icon.music}
        title="Плейлист недоступен"
        text="Плейлист не найден или владелец отозвал доступ."
      />
    );
  }

  return (
    <PublicShareShell>
      <PublicShareHeader
        icon={Icon.music}
        coverUrl={coverUrl || undefined}
        title={name || 'Музыкальный плейлист'}
        subtitle={description || undefined}
        meta={`${tracks.length} ${tracks.length === 1 ? 'трек' : 'треков'}`}
      />

      {tracks.length === 0 ? (
        <div className="public-empty">Плейлист пуст.</div>
      ) : (
        <div className="public-music-list">
          {tracks.map((track, idx) => {
            const active = currentId === track.file.id;
            return (
              <button key={track.file.id} className={'public-music-row' + (active ? ' active' : '')} onClick={() => play(track)}>
                <span className="track-index">{active ? <Icon.pause size={16} /> : idx + 1}</span>
                <span className="track-cover">{track.coverUrl ? <img src={track.coverUrl} alt="" /> : <Icon.music size={20} />}</span>
                <span className="track-main">
                  <span className="track-title">{track.title || track.file.name}</span>
                  <span className="track-sub">{track.artist || 'Неизвестный исполнитель'}</span>
                </span>
                <span className="track-album">{track.album}</span>
                <span className="track-duration">{trackDurationLabel(track.duration)}</span>
              </button>
            );
          })}
        </div>
      )}

      {current && (
        <div className="public-music-player">
          <div className="mp-cover">{(current.largeCoverUrl || current.coverUrl) ? <img src={current.largeCoverUrl || current.coverUrl} alt="" /> : <Icon.music size={24} />}</div>
          <div className="mp-meta">
            <div className="mp-title">{current.title || current.file.name}</div>
            <div className="mp-sub">{current.artist || 'Неизвестный исполнитель'}</div>
            <audio ref={(el) => { audioRef.current = el; persistAudioVolumeRef(el); }} src={current.url} controls autoPlay onEnded={playNext} />
          </div>
        </div>
      )}
    </PublicShareShell>
  );
}
