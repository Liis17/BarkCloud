import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { countLabel } from '../components/public/publicFile';
import { useDocumentHead } from '../hooks/useDocumentHead';
import { AudioBar } from '../components/media/MediaPlayer';
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

function totalDurationLabel(seconds: number): string {
  const minutes = Math.max(1, Math.round(seconds / 60));
  return minutes >= 60 ? `${Math.floor(minutes / 60)} ч ${minutes % 60} мин` : `${minutes} мин`;
}

export function PublicMusicPlaylistPage() {
  const { token } = useParams<{ token: string }>();
  const [name, setName] = React.useState('');
  const [description, setDescription] = React.useState('');
  const [coverUrl, setCoverUrl] = React.useState('');
  const [tracks, setTracks] = React.useState<MusicTrack[]>([]);
  const [state, setState] = React.useState<'loading' | 'notfound' | 'ok'>('loading');
  const [currentId, setCurrentId] = React.useState<string | null>(null);
  const [playing, setPlaying] = React.useState(false);
  const audioRef = React.useRef<HTMLAudioElement | null>(null);
  const current = tracks.find((t) => t.file.id === currentId) || null;
  const hasCurrent = !!current;

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

  // Индикатор в строке трека следует за реальным состоянием <audio> (его ведёт AudioBar).
  React.useEffect(() => {
    const el = audioRef.current;
    if (!el) return;
    const sync = () => setPlaying(!el.paused && !el.ended);
    const events = ['play', 'pause', 'ended', 'emptied'];
    events.forEach((ev) => el.addEventListener(ev, sync));
    sync();
    return () => events.forEach((ev) => el.removeEventListener(ev, sync));
  }, [hasCurrent]);

  function play(track: MusicTrack) {
    setCurrentId(track.file.id);
    window.setTimeout(() => audioRef.current?.play().catch(() => {}), 0);
  }

  function playStep(delta: number) {
    if (!currentId || tracks.length === 0) return;
    const idx = tracks.findIndex((t) => t.file.id === currentId);
    const next = tracks[(idx + delta + tracks.length) % tracks.length];
    if (next) play(next);
  }

  if (state === 'loading') return <PublicStatus title="Открываем плейлист" loading />;
  if (state === 'notfound') {
    return (
      <PublicStatus
        title="Плейлист недоступен"
        text="Плейлист не найден или владелец отозвал доступ."
      />
    );
  }

  const totalSeconds = tracks.reduce((sum, t) => sum + (t.duration > 0 ? t.duration : 0), 0);
  return (
    <PublicShareShell narrow>
      <PublicShareHeader
        variant="collection"
        icon={Icon.music}
        label="Плейлист"
        coverUrl={coverUrl || undefined}
        title={name || 'Музыкальный плейлист'}
        subtitle={description || undefined}
        chips={[
          countLabel(tracks.length, 'трек', 'трека', 'треков'),
          totalSeconds > 0 && totalDurationLabel(totalSeconds),
        ]}
      />

      {tracks.length === 0 ? (
        <div className="public-empty">Плейлист пуст.</div>
      ) : (
        <section className="public-music-list" aria-label="Треки плейлиста">
          {tracks.map((track, idx) => {
            const active = currentId === track.file.id;
            const title = track.title || track.file.name;
            const artist = track.artist || 'Неизвестный исполнитель';
            return (
              <button
                key={track.file.id}
                type="button"
                className={'public-music-row public-st' + (active ? ' active' : '')}
                aria-label={`Воспроизвести «${title}» — ${artist}`}
                onClick={() => play(track)}
              >
                <span className="public-music-idx">
                  {active ? playing ? <Icon.equalizer size={20} /> : <Icon.play size={20} /> : idx + 1}
                </span>
                <span className="public-music-cover public-ph">{track.coverUrl ? <img src={track.coverUrl} alt="" /> : <Icon.music size={20} />}</span>
                <span className="public-music-main">
                  <span className="public-music-title">{title}</span>
                  <span className="public-music-sub">{artist}</span>
                </span>
                <span className="public-music-album">{track.album}</span>
                <span className="public-music-dur">{trackDurationLabel(track.duration)}</span>
              </button>
            );
          })}
        </section>
      )}

      {current && (
        <section className="public-music-player" aria-label="Плеер">
          <div className="public-music-now">
            <span className="public-music-cover is-lg public-ph">
              {(current.largeCoverUrl || current.coverUrl) ? <img src={current.largeCoverUrl || current.coverUrl} alt="" /> : <Icon.music size={22} />}
            </span>
            <div className="public-music-meta">
              <div className="public-music-title">{current.title || current.file.name}</div>
              <div className="public-music-sub">{current.artist || 'Неизвестный исполнитель'}</div>
            </div>
            <div className="public-music-nav">
              <button className="icon-btn" type="button" onClick={() => playStep(-1)} title="Предыдущий трек" aria-label="Предыдущий трек">
                <Icon.skipBack size={24} />
              </button>
              <button className="icon-btn" type="button" onClick={() => playStep(1)} title="Следующий трек" aria-label="Следующий трек">
                <Icon.skipForward size={24} />
              </button>
            </div>
          </div>
          <AudioBar onMediaRef={(el) => { audioRef.current = el; persistAudioVolumeRef(el); }} src={current.url} autoPlay onEnded={() => playStep(1)} />
        </section>
      )}
    </PublicShareShell>
  );
}
