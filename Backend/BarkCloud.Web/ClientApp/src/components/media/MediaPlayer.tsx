import React from 'react';
import { Icon } from '../Icon';
import { Slider } from '../ui/Slider';
import { formatDuration } from '../../lib/format';

interface MediaState {
  playing: boolean;
  time: number;
  duration: number;
  volume: number;
  muted: boolean;
}

const IDLE: MediaState = { playing: false, time: 0, duration: 0, volume: 1, muted: false };

/** Подписка на состояние <video>/<audio>. */
function useMediaState(el: HTMLMediaElement | null): MediaState {
  const [state, setState] = React.useState<MediaState>(IDLE);
  React.useEffect(() => {
    if (!el) return;
    const sync = () => setState({
      playing: !el.paused && !el.ended,
      time: el.currentTime,
      duration: Number.isFinite(el.duration) ? el.duration : 0,
      volume: el.volume,
      muted: el.muted,
    });
    const events = ['play', 'pause', 'ended', 'timeupdate', 'durationchange', 'loadedmetadata', 'volumechange', 'emptied'];
    events.forEach((ev) => el.addEventListener(ev, sync));
    sync();
    return () => events.forEach((ev) => el.removeEventListener(ev, sync));
  }, [el]);
  return state;
}

/** Стабильный callback-ref: кладёт элемент в state и отдаёт его внешнему onMediaRef. */
function useMediaElement<T extends HTMLMediaElement>(onMediaRef?: (el: T | null) => void) {
  const [el, setEl] = React.useState<T | null>(null);
  const external = React.useRef(onMediaRef);
  external.current = onMediaRef;
  const ref = React.useCallback((node: T | null) => {
    setEl(node);
    external.current?.(node);
  }, []);
  return [el, ref] as const;
}

function togglePlay(el: HTMLMediaElement | null) {
  if (!el) return;
  if (el.paused || el.ended) void el.play().catch(() => {});
  else el.pause();
}

function MediaControls({ el, state, filledPlay, fullscreen }: {
  el: HTMLMediaElement | null;
  state: MediaState;
  filledPlay?: boolean;
  fullscreen?: { active: boolean; toggle: () => void };
}) {
  const silent = state.muted || state.volume === 0;
  return (
    <div className="media-controls">
      <button className={'icon-btn' + (filledPlay ? ' primary' : '')} onClick={() => togglePlay(el)} title={state.playing ? 'Пауза' : 'Играть'} aria-label={state.playing ? 'Пауза' : 'Играть'}>
        {state.playing ? <Icon.pause size={20} /> : <Icon.play size={20} />}
      </button>
      <Slider
        className="media-seek"
        max={Math.max(1, state.duration)}
        value={Math.min(state.time, Math.max(1, state.duration))}
        onChange={(v) => { if (el) el.currentTime = v; }}
        aria-label="Позиция"
      />
      <span className="media-time">{formatDuration(state.time)} / {formatDuration(state.duration)}</span>
      <button className="icon-btn" onClick={() => { if (el) el.muted = !el.muted; }} title={silent ? 'Включить звук' : 'Выключить звук'} aria-label={silent ? 'Включить звук' : 'Выключить звук'}>
        {silent ? <Icon.volumeX size={20} /> : <Icon.volume size={20} />}
      </button>
      <Slider
        className="media-volume"
        value={state.muted ? 0 : state.volume}
        onChange={(v) => { if (el) { el.muted = false; el.volume = v; } }}
        aria-label="Громкость"
      />
      {fullscreen && (
        <button className="icon-btn" onClick={fullscreen.toggle} title={fullscreen.active ? 'Выйти из полноэкранного режима' : 'Во весь экран'} aria-label={fullscreen.active ? 'Выйти из полноэкранного режима' : 'Во весь экран'}>
          {fullscreen.active ? <Icon.fullscreenExit size={20} /> : <Icon.fullscreen size={20} />}
        </button>
      )}
    </div>
  );
}

interface VideoPlayerProps {
  src: string;
  poster?: string;
  autoPlay?: boolean;
  className?: string;
  /** Крупная кнопка Play по центру кадра, пока видео не играет (публичная страница файла). */
  bigPlay?: boolean;
  onMediaRef?: (el: HTMLVideoElement | null) => void;
}

/** Видеоплеер с M3-контролами поверх кадра (вместо нативных controls).
 *  Клавиши при фокусе на плеере: Space/K — пауза, M — звук, F — полный экран. */
export function VideoPlayer({ src, poster, autoPlay, className, bigPlay, onMediaRef }: VideoPlayerProps) {
  const [el, ref] = useMediaElement<HTMLVideoElement>(onMediaRef);
  const state = useMediaState(el);
  const wrap = React.useRef<HTMLDivElement>(null);
  const [isFull, setIsFull] = React.useState(false);
  const [idle, setIdle] = React.useState(false);
  const idleTimer = React.useRef<number>();

  React.useEffect(() => {
    const onChange = () => setIsFull(document.fullscreenElement === wrap.current);
    document.addEventListener('fullscreenchange', onChange);
    return () => {
      document.removeEventListener('fullscreenchange', onChange);
      window.clearTimeout(idleTimer.current);
    };
  }, []);

  const poke = () => {
    setIdle(false);
    window.clearTimeout(idleTimer.current);
    idleTimer.current = window.setTimeout(() => setIdle(true), 2500);
  };
  const toggleFull = () => {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void wrap.current?.requestFullscreen();
  };
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (!el || e.target instanceof HTMLInputElement) return;
    const key = e.key.toLowerCase();
    if ((key === ' ' && !(e.target instanceof HTMLButtonElement)) || key === 'k') togglePlay(el);
    else if (key === 'm') el.muted = !el.muted;
    else if (key === 'f') toggleFull();
    else return;
    e.preventDefault();
    poke();
  };

  return (
    <div
      ref={wrap}
      className={'video-player' + (idle && state.playing ? ' idle' : '') + (className ? ' ' + className : '')}
      tabIndex={0}
      onMouseMove={poke}
      onMouseLeave={() => setIdle(true)}
      onKeyDown={onKeyDown}
    >
      <video ref={ref} src={src} poster={poster} autoPlay={autoPlay} playsInline onClick={() => togglePlay(el)} onDoubleClick={toggleFull} />
      {bigPlay && !state.playing && (
        <button className="video-player-play" onClick={() => togglePlay(el)} title="Играть" aria-label="Играть">
          <Icon.play size={40} />
        </button>
      )}
      <div className="video-player-bar">
        <MediaControls el={el} state={state} fullscreen={{ active: isFull, toggle: toggleFull }} />
      </div>
    </div>
  );
}

interface AudioBarProps {
  src: string;
  autoPlay?: boolean;
  onEnded?: () => void;
  onMediaRef?: (el: HTMLAudioElement | null) => void;
}

/** Аудиоплеер-полоса с M3-контролами (вместо нативного <audio controls>). */
export function AudioBar({ src, autoPlay, onEnded, onMediaRef }: AudioBarProps) {
  const [el, ref] = useMediaElement<HTMLAudioElement>(onMediaRef);
  const state = useMediaState(el);
  return (
    <div className="audio-bar">
      <audio ref={ref} src={src} autoPlay={autoPlay} onEnded={onEnded} />
      <MediaControls el={el} state={state} filledPlay />
    </div>
  );
}
