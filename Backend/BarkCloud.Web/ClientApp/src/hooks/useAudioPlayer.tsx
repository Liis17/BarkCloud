import React from 'react';
import type { MusicTrack } from '../lib/types';

const COOKIE = 'bark_audio_vol';
const EQ_KEY = 'bark_audio_eq';
const MAX_AGE = 60 * 60 * 24 * 365;
const MIN_EQ_GAIN = -12;
const MAX_EQ_GAIN = 12;

export interface EqualizerBand {
  frequency: number;
  label: string;
  type: BiquadFilterType;
}

export interface EqualizerPreset {
  id: string;
  label: string;
  gains: number[];
}

export interface EqualizerState {
  enabled: boolean;
  preset: string;
  gains: number[];
}

export const EQUALIZER_BANDS: EqualizerBand[] = [
  { frequency: 60, label: '60', type: 'lowshelf' },
  { frequency: 250, label: '250', type: 'peaking' },
  { frequency: 1000, label: '1K', type: 'peaking' },
  { frequency: 4000, label: '4K', type: 'peaking' },
  { frequency: 12000, label: '12K', type: 'highshelf' },
];

export const EQUALIZER_PRESETS: EqualizerPreset[] = [
  { id: 'flat', label: 'Ровно', gains: [0, 0, 0, 0, 0] },
  { id: 'bass', label: 'Бас', gains: [6, 4, 0, -1, -2] },
  { id: 'vocal', label: 'Вокал', gains: [-2, -1, 3, 4, 2] },
  { id: 'bright', label: 'Ярко', gains: [-3, -1, 0, 4, 6] },
  { id: 'rock', label: 'Рок', gains: [4, 2, -1, 3, 4] },
];

const DEFAULT_EQUALIZER: EqualizerState = {
  enabled: false,
  preset: 'flat',
  gains: EQUALIZER_PRESETS[0].gains,
};

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
  equalizer: EqualizerState;
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
  setEqualizerEnabled: (value: boolean) => void;
  setEqualizerGain: (index: number, value: number) => void;
  applyEqualizerPreset: (presetId: string) => void;
  resetEqualizer: () => void;
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
  const audioContextRef = React.useRef<AudioContext | null>(null);
  const sourceRef = React.useRef<MediaElementAudioSourceNode | null>(null);
  const filtersRef = React.useRef<BiquadFilterNode[]>([]);
  const [queue, setQueue] = React.useState<MusicTrack[]>([]);
  const [index, setIndex] = React.useState(0);
  const [isPlaying, setIsPlaying] = React.useState(false);
  const [shuffle, setShuffle] = React.useState(false);
  const [currentTime, setCurrentTime] = React.useState(0);
  const [duration, setDuration] = React.useState(0);
  const saved = React.useMemo(readAudioVolume, []);
  const [volume, setVolumeState] = React.useState(saved.volume);
  const [muted, setMutedState] = React.useState(saved.muted);
  const [equalizer, setEqualizerState] = React.useState<EqualizerState>(() => readEqualizer());
  const current = queue[index] || null;

  const ensureAudioGraph = React.useCallback(() => {
    const audio = audioRef.current;
    if (!audio || sourceRef.current) return audioContextRef.current;

    const AudioContextCtor =
      window.AudioContext || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioContextCtor) return null;

    const ctx = new AudioContextCtor();
    const source = ctx.createMediaElementSource(audio);
    const filters = EQUALIZER_BANDS.map((band) => {
      const filter = ctx.createBiquadFilter();
      filter.type = band.type;
      filter.frequency.value = band.frequency;
      filter.Q.value = 1;
      return filter;
    });

    source.connect(filters[0]);
    for (let i = 0; i < filters.length - 1; i++) filters[i].connect(filters[i + 1]);
    filters[filters.length - 1].connect(ctx.destination);

    audioContextRef.current = ctx;
    sourceRef.current = source;
    filtersRef.current = filters;
    return ctx;
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
    const src = trackStreamUrl(current.file.id);
    if (audio.src !== new URL(src, window.location.href).href) audio.src = src;
    if (isPlaying) {
      const ctx = equalizer.enabled ? ensureAudioGraph() : audioContextRef.current;
      ctx?.resume().catch(() => {});
      audio.play().catch(() => setIsPlaying(false));
    } else {
      audio.pause();
    }
  }, [current, equalizer.enabled, ensureAudioGraph, isPlaying]);

  React.useEffect(() => {
    writeEqualizer(equalizer);

    const audio = audioRef.current;
    if (audio && equalizer.enabled) ensureAudioGraph();

    const ctx = audioContextRef.current;
    filtersRef.current.forEach((filter, i) => {
      const gain = equalizer.enabled ? equalizer.gains[i] ?? 0 : 0;
      if (ctx) filter.gain.setTargetAtTime(gain, ctx.currentTime, 0.015);
      else filter.gain.value = gain;
    });
  }, [equalizer, ensureAudioGraph]);

  React.useEffect(() => () => {
    audioContextRef.current?.close().catch(() => {});
  }, []);

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
    equalizer,
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
    setEqualizerEnabled: (enabled) => setEqualizerState((prev) => ({ ...prev, enabled })),
    setEqualizerGain: (bandIndex, value) => setEqualizerState((prev) => ({
      ...prev,
      enabled: true,
      preset: 'custom',
      gains: prev.gains.map((gain, i) => (i === bandIndex ? clampEqGain(value) : gain)),
    })),
    applyEqualizerPreset: (presetId) => {
      const preset = EQUALIZER_PRESETS.find((item) => item.id === presetId);
      if (!preset) return;
      setEqualizerState({
        enabled: preset.id !== 'flat',
        preset: preset.id,
        gains: preset.gains,
      });
    },
    resetEqualizer: () => setEqualizerState(DEFAULT_EQUALIZER),
  }), [current, currentTime, duration, equalizer, isPlaying, muted, next, pause, previous, queue, resume, shuffle, volume]);

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

function trackStreamUrl(fileId: string): string {
  return `/api/music/track/stream/${encodeURIComponent(fileId)}`;
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

function readEqualizer(): EqualizerState {
  try {
    const raw = localStorage.getItem(EQ_KEY);
    if (!raw) return DEFAULT_EQUALIZER;

    const parsed = JSON.parse(raw) as Partial<EqualizerState>;
    const gains = normalizeEqGains(parsed.gains);
    const preset = typeof parsed.preset === 'string' ? parsed.preset : 'custom';
    return { enabled: !!parsed.enabled, preset, gains };
  } catch {
    return DEFAULT_EQUALIZER;
  }
}

function writeEqualizer(state: EqualizerState) {
  try {
    localStorage.setItem(EQ_KEY, JSON.stringify({
      enabled: state.enabled,
      preset: state.preset,
      gains: normalizeEqGains(state.gains),
    }));
  } catch {
    // localStorage может быть недоступен в приватном режиме; звук должен работать и без него.
  }
}

function normalizeEqGains(value: unknown): number[] {
  const source = Array.isArray(value) ? value : DEFAULT_EQUALIZER.gains;
  return EQUALIZER_BANDS.map((_, i) => clampEqGain(Number(source[i] ?? 0)));
}

function clampEqGain(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.min(MAX_EQ_GAIN, Math.max(MIN_EQ_GAIN, Math.round(value)));
}
