import React from 'react';
import { Icon } from '../Icon';
import { Slider } from '../ui/Slider';
import { formatDuration } from '../../lib/format';
import { decodeSubtitleBytes, toWebVtt } from '../../lib/subtitles';

interface MediaState {
  playing: boolean;
  time: number;
  duration: number;
  volume: number;
  muted: boolean;
}

const IDLE: MediaState = { playing: false, time: 0, duration: 0, volume: 1, muted: false };
const VIDEO_QUALITY_COOKIE = 'bc_video_quality';
const PLAYBACK_RATES = [0.5, 0.75, 1, 1.25, 1.5, 2];

export interface VideoQuality {
  height: number;
  width: number;
  fileSize: number;
  isOriginal: boolean;
  url: string;
}

function supportsAv1Webm(): boolean {
  if (typeof document === 'undefined') return false;
  return !!document.createElement('video').canPlayType('video/webm; codecs="av01, opus"');
}

function readVideoQualityCookie(): string | null {
  if (typeof document === 'undefined') return null;
  const value = document.cookie.split('; ').find((cookie) => cookie.startsWith(VIDEO_QUALITY_COOKIE + '='));
  return value ? value.slice(VIDEO_QUALITY_COOKIE.length + 1) : null;
}

function selectInitialQuality(qualities: VideoQuality[] | undefined, av1Supported: boolean): string {
  const preferred = readVideoQualityCookie();
  if (!preferred) return 'original';
  if (preferred === 'original') return 'original';

  const height = Number(preferred);
  if (!Number.isInteger(height) || height <= 0 || !av1Supported) return 'original';
  const variants = (qualities || []).filter((quality) => !quality.isOriginal);
  const exact = variants.find((quality) => quality.height === height);
  if (exact) return String(exact.height);
  const lower = variants.filter((quality) => quality.height < height).sort((a, b) => b.height - a.height)[0];
  return lower ? String(lower.height) : 'original';
}

function qualityLabel(quality: VideoQuality): string {
  if (quality.isOriginal) return 'Оригинал';
  return `${quality.height}p`;
}

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

function MediaControls({ el, state, filledPlay, fullscreen, extraControls, qualityMenu }: {
  el: HTMLMediaElement | null;
  state: MediaState;
  filledPlay?: boolean;
  fullscreen?: { active: boolean; toggle: () => void };
  extraControls?: React.ReactNode;
  qualityMenu?: React.ReactNode;
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
      {extraControls}
      {qualityMenu}
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
  qualities?: VideoQuality[];
  poster?: string;
  autoPlay?: boolean;
  className?: string;
  /** Крупная кнопка Play по центру кадра, пока видео не играет (публичная страница файла). */
  bigPlay?: boolean;
  onMediaRef?: (el: HTMLVideoElement | null) => void;
}

/** Видеоплеер с M3-контролами поверх кадра (вместо нативных controls).
 *  Клавиши при фокусе на плеере: Space/K — пауза, M — звук, F — полный экран.
 *  Есть выбор скорости, картинка-в-картинке (где браузер поддерживает) и локальные субтитры .vtt/.srt. */
export function VideoPlayer({ src, qualities, poster, autoPlay, className, bigPlay, onMediaRef }: VideoPlayerProps) {
  const [el, ref] = useMediaElement<HTMLVideoElement>(onMediaRef);
  const state = useMediaState(el);
  const wrap = React.useRef<HTMLDivElement>(null);
  const [isFull, setIsFull] = React.useState(false);
  const [idle, setIdle] = React.useState(false);
  const av1Supported = React.useMemo(supportsAv1Webm, []);
  const [selectedQuality, setSelectedQuality] = React.useState(() => selectInitialQuality(qualities, av1Supported));
  const [switchingQuality, setSwitchingQuality] = React.useState(false);
  const pendingPlayback = React.useRef<{ time: number; paused: boolean } | null>(null);
  const previousSource = React.useRef({ src, qualities });
  const idleTimer = React.useRef<number>();
  const [rate, setRate] = React.useState(1);
  // blob: URL загруженного пользователем файла субтитров (WebVTT); null — субтитров нет.
  const [subtitlesUrl, setSubtitlesUrl] = React.useState<string | null>(null);
  const subtitlesInput = React.useRef<HTMLInputElement>(null);
  const [inPip, setInPip] = React.useState(false);
  const pipSupported = typeof document !== 'undefined' && !!document.pictureInPictureEnabled;
  const originalQuality = qualities?.find((quality) => quality.isOriginal);
  const qualityOptions: VideoQuality[] = [
    { height: originalQuality?.height || 0, width: originalQuality?.width || 0, fileSize: originalQuality?.fileSize || 0, isOriginal: true, url: src },
    ...(av1Supported ? (qualities || []).filter((quality) => !quality.isOriginal).sort((a, b) => a.height - b.height) : []),
  ];
  const selectedQualityOption = qualityOptions.find((quality) => (quality.isOriginal ? 'original' : String(quality.height)) === selectedQuality)
    || qualityOptions[0];

  React.useEffect(() => {
    if (previousSource.current.src === src && previousSource.current.qualities === qualities) return;
    const previousSrc = previousSource.current.src;
    previousSource.current = { src, qualities };
    pendingPlayback.current = null;
    setSwitchingQuality(false);
    setSelectedQuality(selectInitialQuality(qualities, av1Supported));
    // Субтитры относятся к конкретному видео; смена качества src не меняет и их сохраняет.
    if (previousSrc !== src) setSubtitlesUrl(null);
  }, [src, qualities, av1Supported]);

  // Отзыв blob: URL при замене/снятии субтитров и размонтировании.
  React.useEffect(() => () => {
    if (subtitlesUrl) URL.revokeObjectURL(subtitlesUrl);
  }, [subtitlesUrl]);

  // Динамически добавленный <track default> браузеры часто не включают — показываем дорожку явно.
  const showSubtitles = React.useCallback(() => {
    if (!el || !subtitlesUrl) return;
    for (let n = 0; n < el.textTracks.length; n++) el.textTracks[n].mode = 'showing';
  }, [el, subtitlesUrl]);
  React.useEffect(showSubtitles, [showSubtitles]);

  React.useEffect(() => {
    if (!el) return;
    const enter = () => setInPip(true);
    const leave = () => setInPip(false);
    el.addEventListener('enterpictureinpicture', enter);
    el.addEventListener('leavepictureinpicture', leave);
    return () => {
      el.removeEventListener('enterpictureinpicture', enter);
      el.removeEventListener('leavepictureinpicture', leave);
    };
  }, [el]);

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
    if (!el || e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement) return;
    const key = e.key.toLowerCase();
    if ((key === ' ' && !(e.target instanceof HTMLButtonElement)) || key === 'k') togglePlay(el);
    else if (key === 'm') el.muted = !el.muted;
    else if (key === 'f') toggleFull();
    else return;
    e.preventDefault();
    poke();
  };

  const changeQuality = (qualityKey: string) => {
    if (qualityKey === selectedQuality) return;
    if (el) {
      pendingPlayback.current = { time: el.currentTime, paused: el.paused };
      setSwitchingQuality(true);
    }
    setSelectedQuality(qualityKey);
    document.cookie = `${VIDEO_QUALITY_COOKIE}=${encodeURIComponent(qualityKey)}; Path=/; Max-Age=31536000; SameSite=Lax${location.protocol === 'https:' ? '; Secure' : ''}`;
  };

  const restorePlayback = () => {
    const pending = pendingPlayback.current;
    if (!el || !pending) return;
    pendingPlayback.current = null;
    if (Number.isFinite(el.duration)) el.currentTime = Math.min(pending.time, el.duration);
    else el.currentTime = pending.time;
    if (pending.paused) el.pause();
    else void el.play().catch(() => {});
    setSwitchingQuality(false);
    showSubtitles();
  };

  // Скорость задаём и как defaultPlaybackRate: при смене src (например, качества) load() сбрасывает playbackRate к нему.
  const changeRate = (value: number) => {
    setRate(value);
    if (el) {
      el.defaultPlaybackRate = value;
      el.playbackRate = value;
    }
  };

  const togglePip = () => {
    if (!el) return;
    if (document.pictureInPictureElement) void document.exitPictureInPicture().catch(() => {});
    else void el.requestPictureInPicture().catch(() => {});
  };

  const loadSubtitles = async (file: File) => {
    const vtt = toWebVtt(decodeSubtitleBytes(await file.arrayBuffer()));
    setSubtitlesUrl(URL.createObjectURL(new Blob([vtt], { type: 'text/vtt' })));
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
      <video ref={ref} src={selectedQualityOption.url || src} poster={poster} autoPlay={autoPlay && !switchingQuality} playsInline onLoadedMetadata={restorePlayback} onClick={() => togglePlay(el)} onDoubleClick={toggleFull}>
        {subtitlesUrl && <track key={subtitlesUrl} kind="subtitles" src={subtitlesUrl} srcLang="ru" label="Субтитры" default />}
      </video>
      {bigPlay && !state.playing && (
        <button className="video-player-play" onClick={() => togglePlay(el)} title="Играть" aria-label="Играть">
          <Icon.play size={40} />
        </button>
      )}
      <div className="video-player-bar">
        <MediaControls
          el={el}
          state={state}
          fullscreen={{ active: isFull, toggle: toggleFull }}
          extraControls={(
            <>
              <select
                className="media-rate"
                aria-label="Скорость воспроизведения"
                value={rate}
                onChange={(event) => changeRate(Number(event.target.value))}
                onMouseDown={(event) => event.stopPropagation()}
              >
                {PLAYBACK_RATES.map((value) => <option key={value} value={value}>{value}×</option>)}
              </select>
              <button
                className={'icon-btn' + (subtitlesUrl ? ' active' : '')}
                onClick={() => (subtitlesUrl ? setSubtitlesUrl(null) : subtitlesInput.current?.click())}
                title={subtitlesUrl ? 'Убрать субтитры' : 'Загрузить субтитры (.vtt, .srt)'}
                aria-label={subtitlesUrl ? 'Убрать субтитры' : 'Загрузить субтитры'}
              >
                <Icon.subtitles size={20} />
              </button>
              <input
                ref={subtitlesInput}
                type="file"
                accept=".vtt,.srt,text/vtt"
                hidden
                aria-label="Файл субтитров"
                onChange={(event) => {
                  const file = event.target.files?.[0];
                  event.target.value = '';
                  if (file) void loadSubtitles(file);
                }}
              />
              {pipSupported && (
                <button className="icon-btn" onClick={togglePip} title={inPip ? 'Вернуть из окна' : 'Картинка в картинке'} aria-label={inPip ? 'Вернуть из окна' : 'Картинка в картинке'}>
                  <Icon.pip size={20} />
                </button>
              )}
            </>
          )}
          qualityMenu={qualityOptions.length > 1 ? (
            <select
              className="media-quality"
              aria-label="Качество видео"
              value={selectedQuality}
              onChange={(event) => changeQuality(event.target.value)}
              onMouseDown={(event) => event.stopPropagation()}
            >
              {qualityOptions.map((quality) => {
                const key = quality.isOriginal ? 'original' : String(quality.height);
                return <option key={key} value={key}>{qualityLabel(quality)}</option>;
              })}
            </select>
          ) : null}
        />
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
