import React from 'react';
import { apiPost } from '../lib/api';
import type { MusicTrack } from '../lib/types';

const COOKIE = 'bark_audio_vol';
const MAX_AGE = 60 * 60 * 24 * 365;

interface AudioVolume {
  volume: number;
  muted: boolean;
}

interface AudioPlayerContextValue {
  queue: MusicTrack[];
  current: MusicTrack | null;
  isPlaying: boolean;
  shuffle: boolean;
  volume: number;
  muted: boolean;
  currentTime: number;
  duration: number;
  playQueue: (tracks: MusicTrack[], startId?: string) => void;
  playTrack: (track: MusicTrack) => void;
  pause: () => void;
  resume: () => void;
  toggle: () => void;
  next: () => void;
  previous: () => void;
  setShuffle: (value: boolean) => void;
  setVolume: (value: number) => void;
  setMuted: (value: boolean) => void;
  seek: (value: number) => void;
}

const AudioPlayerContext = React.createContext<AudioPlayerContextValue | null>(null);

export function useAudioPlayer() {
  const ctx = React.useContext(AudioPlayerContext);
  if (!ctx) throw new Error('useAudioPlayer must be used inside AudioPlayerProvider');
  return ctx;
}

export function useOptionalAudioPlayer() {
  return React.useContext(AudioPlayerContext);
}

export function AudioPlayerProvider({ children }: { children: React.ReactNode }) {
  const audioRef = React.useRef<HTMLAudioElement | null>(null);
  const [queue, setQueue] = React.useState<MusicTrack[]>([]);
  const [index, setIndex] = React.useState(0);
  const [isPlaying, setIsPlaying] = React.useState(false);
  const [shuffle, setShuffle] = React.useState(false);
  const [currentTime, setCurrentTime] = React.useState(0);
  const [duration, setDuration] = React.useState(0);
  const saved = React.useMemo(readAudioVolume, []);
  const [volume, setVolumeState] = React.useState(saved.volume);
  const [muted, setMutedState] = React.useState(saved.muted);
  const current = queue[index] || null;

  const updateTrackUrl = React.useCallback((fileId: string, url: string) => {
    setQueue((prev) => prev.map((t) => (t.file.id === fileId ? { ...t, url } : t)));
  }, []);

  React.useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;
    audio.volume = volume;
    audio.muted = muted;
    writeAudioVolume({ volume, muted });
  }, [volume, muted]);

  React.useEffect(() => {
    const audio = audioRef.current;
    if (!audio || !current) return;
    if (!current.url) {
      refreshTrackUrl(current.file.id).then((url) => {
        if (url) updateTrackUrl(current.file.id, url);
        else setIsPlaying(false);
      });
      return;
    }
    if (audio.src !== current.url) audio.src = current.url;
    if (isPlaying) {
      audio.play().catch(async () => {
        const refreshed = await refreshTrackUrl(current.file.id);
        if (refreshed) {
          updateTrackUrl(current.file.id, refreshed);
          audio.src = refreshed;
          await audio.play().catch(() => setIsPlaying(false));
        } else {
          setIsPlaying(false);
        }
      });
    } else {
      audio.pause();
    }
  }, [current, isPlaying, updateTrackUrl]);

  const next = React.useCallback(() => {
    setIndex((prev) => {
      if (queue.length <= 1) return prev;
      if (shuffle) {
        let nextIndex = prev;
        while (nextIndex === prev) nextIndex = Math.floor(Math.random() * queue.length);
        return nextIndex;
      }
      return (prev + 1) % queue.length;
    });
    setIsPlaying(true);
  }, [queue.length, shuffle]);

  const previous = React.useCallback(() => {
    setIndex((prev) => (queue.length ? (prev - 1 + queue.length) % queue.length : 0));
    setIsPlaying(true);
  }, [queue.length]);

  const pause = React.useCallback(() => setIsPlaying(false), []);
  const resume = React.useCallback(() => setIsPlaying(true), []);

  const value = React.useMemo<AudioPlayerContextValue>(() => ({
    queue,
    current,
    isPlaying,
    shuffle,
    volume,
    muted,
    currentTime,
    duration,
    playQueue: (tracks, startId) => {
      if (!tracks.length) return;
      const start = startId ? Math.max(0, tracks.findIndex((t) => t.file.id === startId)) : 0;
      setQueue(tracks);
      setIndex(start);
      setIsPlaying(true);
    },
    playTrack: (track) => {
      setQueue([track]);
      setIndex(0);
      setIsPlaying(true);
    },
    pause,
    resume,
    toggle: () => setIsPlaying((v) => !v),
    next,
    previous,
    setShuffle,
    setVolume: (v) => setVolumeState(Math.min(1, Math.max(0, v))),
    setMuted: setMutedState,
    seek: (v) => {
      const audio = audioRef.current;
      if (!audio) return;
      audio.currentTime = v;
      setCurrentTime(v);
    },
  }), [current, currentTime, duration, isPlaying, muted, next, pause, previous, queue, resume, shuffle, volume]);

  return (
    <AudioPlayerContext.Provider value={value}>
      {children}
      <audio
        ref={audioRef}
        onPlay={() => setIsPlaying(true)}
        onPause={() => setIsPlaying(false)}
        onEnded={next}
        onTimeUpdate={(e) => setCurrentTime(e.currentTarget.currentTime)}
        onLoadedMetadata={(e) => setDuration(Number.isFinite(e.currentTarget.duration) ? e.currentTarget.duration : 0)}
      />
    </AudioPlayerContext.Provider>
  );
}

async function refreshTrackUrl(fileId: string): Promise<string | null> {
  try {
    const resp = await apiPost<{ url?: string }>('/api/music/track/url', { fileId });
    return resp.url || null;
  } catch {
    return null;
  }
}

function readAudioVolume(): AudioVolume {
  const m = document.cookie.match(/(?:^|;\s*)bark_audio_vol=([^;]+)/);
  if (!m) return { volume: 0.8, muted: false };
  try {
    const p = JSON.parse(decodeURIComponent(m[1]));
    const volume = Math.min(1, Math.max(0, Number(p.v)));
    return { volume: Number.isNaN(volume) ? 0.8 : volume, muted: !!p.m };
  } catch {
    return { volume: 0.8, muted: false };
  }
}

function writeAudioVolume(state: AudioVolume) {
  const payload = encodeURIComponent(JSON.stringify({ v: state.volume, m: state.muted }));
  document.cookie = `${COOKIE}=${payload}; path=/; max-age=${MAX_AGE}; samesite=lax`;
}
