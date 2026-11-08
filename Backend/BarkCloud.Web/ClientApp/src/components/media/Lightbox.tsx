import React from 'react';
import { Icon } from '../Icon';
import { useContextMenu } from '../ui/ContextMenu';
import { useOptionalAudioPlayer } from '../../hooks/useAudioPlayer';
import { apiGet, fetchClipboardImage, proxiedImageUrl } from '../../lib/api';
import { persistVolumeRef } from '../../lib/volume';
import { VideoPlayer } from './MediaPlayer';
import type { VideoQuality } from './MediaPlayer';
import { pickDocumentIcon, useDocumentHead } from '../../hooks/useDocumentHead';
import { isSvgFileName, selectPhotoViewSource } from '../../lib/imagePreviewSource';
import type { MediaActionsApi } from '../../hooks/useMediaActions';
import type { CardFile, MediaItem } from '../../lib/types';

interface DownloadResponse {
  urls: Record<string, string | null>;
  videoQualities: Record<string, VideoQuality[]>;
}

interface LightboxProps {
  items?: CardFile[];
  index?: number;
  media?: CardFile;
  onClose?: () => void;
  /** Действия панели под изображением (useMediaActions().api); без них панель не рендерится. */
  actions?: MediaActionsApi;
}

/** Касания, начатые на элементах управления, не превращаются в жесты просмотра. */
const GESTURE_IGNORE = '.lb-actions, .video-player-bar, .ctx-backdrop, .ctx-menu, button, a, input, select';
const SWIPE_MIN_PX = 50;
const MAX_SCALE = 5;

type TouchGesture =
  | { kind: 'swipe'; x: number; y: number }
  | { kind: 'pan'; x: number; y: number; px: number; py: number }
  | { kind: 'pinch'; dist: number; scale: number }
  /** Щипок закончился не всеми пальцами сразу: оставшийся палец не должен листать. */
  | { kind: 'done' };

function touchDistance(t: TouchList): number {
  return Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
}

function isTextEntry(target: EventTarget | null): boolean {
  if (target instanceof HTMLTextAreaElement) return true;
  if (target instanceof HTMLInputElement) return target.type !== 'range';
  return target instanceof HTMLElement && target.isContentEditable;
}

/** Полноэкранный просмотр ОРИГИНАЛА (временная ссылка через /api/files/download).
 *  Листание стрелками/кнопками/свайпом (на видео стрелки перематывают ±5c, листание — Shift+стрелки),
 *  зум колесом и щипком для фото, предзагрузка соседних фото.
 *  При переданных actions под изображением — плавающая панель действий. */
export function Lightbox({ items, index = 0, media, onClose, actions }: LightboxProps) {
  const list: CardFile[] = Array.isArray(items) ? items : media ? [media] : [];
  const [i, setI] = React.useState(index);
  const [urls, setUrls] = React.useState<Record<string, string | null>>({});
  const [videoQualities, setVideoQualities] = React.useState<Record<string, VideoQuality[]>>({});
  const [err, setErr] = React.useState<string | null>(null);
  const [scale, setScale] = React.useState(1);
  const [pan, setPan] = React.useState({ x: 0, y: 0 });
  const videoRef = React.useRef<HTMLVideoElement | null>(null);
  const stageRef = React.useRef<HTMLDivElement | null>(null);
  const rootRef = React.useRef<HTMLDivElement | null>(null);
  // Запросы временных ссылок по id: общий промис не даёт дублировать запрос, если перешли на соседа во время предзагрузки.
  const urlRequestsRef = React.useRef(new Map<string, Promise<void>>());
  const gestureRef = React.useRef<TouchGesture | null>(null);
  const viewRef = React.useRef({ scale: 1, pan: { x: 0, y: 0 } });
  viewRef.current = { scale, pan };
  const dragRef = React.useRef<{ x: number; y: number; px: number; py: number } | null>(null);
  const pausedMusicRef = React.useRef(false);
  const { menu, openAt } = useContextMenu();
  const musicPlayer = useOptionalAudioPlayer();

  const cur = list[i] || null;
  const fileId = cur && cur.id;
  const isVideo = !!cur && cur.kind === 'video';
  const isSvg = !!cur && (isSvgFileName(cur.name) || isSvgFileName(cur.ext));

  React.useEffect(() => {
    if (!isVideo || !musicPlayer) return;
    if (musicPlayer.isPlaying) {
      pausedMusicRef.current = true;
      musicPlayer.pause();
    }
    return () => {
      if (pausedMusicRef.current) {
        pausedMusicRef.current = false;
        musicPlayer.resume();
      }
    };
    // Запускать только при входе/выходе из video-режима: изменения времени аудио не должны перезапускать эффект.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isVideo]);

  useDocumentHead(
    () => ({ title: cur?.name || null, iconUrl: pickDocumentIcon(cur) }),
    [cur?.id, cur?.name, cur?.jpegViewUrl, cur?.previews],
    20,
  );

  const requestUrls = React.useCallback((ids: string[]) => {
    const requests = urlRequestsRef.current;
    const missing = ids.filter((id) => !requests.has(id));
    if (missing.length) {
      const request = apiGet<DownloadResponse>('/api/files/download?ids=' + missing.map(encodeURIComponent).join(','))
        .then((d) => {
          setUrls((u) => {
            const next = { ...u };
            for (const id of missing) next[id] = (d.urls && d.urls[id]) || null;
            return next;
          });
          setVideoQualities((current) => {
            const next = { ...current };
            for (const id of missing) next[id] = d.videoQualities?.[id] || [];
            return next;
          });
          // Без ссылки — разрешаем повторный запрос при следующем открытии файла.
          for (const id of missing) if (!d.urls?.[id]) requests.delete(id);
        });
      request.catch(() => missing.forEach((id) => requests.delete(id)));
      for (const id of missing) requests.set(id, request);
    }
    return Promise.all(ids.map((id) => requests.get(id)));
  }, []);

  React.useEffect(() => {
    let alive = true;
    setErr(null);
    if (!fileId || urls[fileId]) return;
    requestUrls([fileId]).catch((e) => {
      if (alive) setErr(e.message);
    });
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileId]);

  // Предзагрузка соседних фото: показываемый источник прогревается в кэше браузера,
  // а для фото без JpegView заранее запрашивается временная ссылка на оригинал. Видео не предзагружаем.
  const prevItem = list[i - 1];
  const nextItem = list[i + 1];
  React.useEffect(() => {
    const needUrls: string[] = [];
    for (const item of [prevItem, nextItem]) {
      if (!item || item.kind === 'video') continue;
      const src = selectPhotoViewSource(item.name, item.jpegViewUrl, item.previews, urls[item.id]);
      if (src) new Image().src = src;
      else if (!isSvgFileName(item.name) && !isSvgFileName(item.ext) && !(item.id in urls)) needUrls.push(item.id);
    }
    if (needUrls.length) requestUrls(needUrls).catch(() => {});
  }, [prevItem, nextItem, urls, requestUrls]);

  React.useEffect(() => {
    setScale(1);
    setPan({ x: 0, y: 0 });
  }, [i]);

  // Родитель убрал элемент(ы) из items (например, после удаления) — корректируем индекс.
  React.useEffect(() => {
    if (list.length === 0) {
      onClose && onClose();
      return;
    }
    if (i >= list.length) setI(list.length - 1);
  }, [list.length, i, onClose]);

  const go = React.useCallback(
    (delta: number) => {
      setI((prev) => Math.min(list.length - 1, Math.max(0, prev + delta)));
    },
    [list.length],
  );

  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose && onClose();
        return;
      }
      if (e.shiftKey && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
        // Листание работает и на видео, и при фокусе на слайдере плеера; в текстовых полях Shift+стрелки выделяют текст.
        if (isTextEntry(e.target)) return;
        e.preventDefault();
        go(e.key === 'ArrowLeft' ? -1 : 1);
        return;
      }
      if (e.target instanceof HTMLInputElement) return; // слайдеры плеера сами обрабатывают стрелки
      if (e.key === 'ArrowLeft') {
        if (isVideo && videoRef.current) videoRef.current.currentTime = Math.max(0, videoRef.current.currentTime - 5);
        else go(-1);
      } else if (e.key === 'ArrowRight') {
        if (isVideo && videoRef.current) {
          const d = videoRef.current.duration || Infinity;
          videoRef.current.currentTime = Math.min(d, videoRef.current.currentTime + 5);
        } else go(1);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, isVideo, go]);

  React.useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (isVideo) return;
      e.preventDefault();
      setScale((s) => Math.min(MAX_SCALE, Math.max(1, +(s - Math.sign(e.deltaY) * 0.25).toFixed(2))));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [isVideo]);

  // Касания: свайп листает (без зума), щипок зумит фото, одним пальцем двигается увеличенное фото.
  // Нативные слушатели с passive: false — иначе preventDefault не отменит прокрутку и зум страницы.
  React.useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    const onStart = (e: TouchEvent) => {
      if (e.target instanceof Element && e.target.closest(GESTURE_IGNORE)) {
        gestureRef.current = null;
        return;
      }
      const { scale: s, pan: p } = viewRef.current;
      if (e.touches.length >= 2) {
        gestureRef.current = isVideo ? { kind: 'done' } : { kind: 'pinch', dist: touchDistance(e.touches), scale: s };
      } else if (e.touches.length === 1) {
        const t = e.touches[0];
        gestureRef.current = !isVideo && s > 1
          ? { kind: 'pan', x: t.clientX, y: t.clientY, px: p.x, py: p.y }
          : { kind: 'swipe', x: t.clientX, y: t.clientY };
      }
    };
    const onMove = (e: TouchEvent) => {
      const g = gestureRef.current;
      if (!g || g.kind === 'done') return;
      e.preventDefault();
      if (g.kind === 'pinch' && e.touches.length >= 2) {
        const next = Math.min(MAX_SCALE, Math.max(1, g.scale * (touchDistance(e.touches) / g.dist)));
        setScale(next);
        if (next === 1) setPan({ x: 0, y: 0 });
      } else if (g.kind === 'pan' && e.touches.length === 1) {
        setPan({ x: g.px + (e.touches[0].clientX - g.x), y: g.py + (e.touches[0].clientY - g.y) });
      }
    };
    const onEnd = (e: TouchEvent) => {
      const g = gestureRef.current;
      if (!g) return;
      if (e.touches.length > 0) {
        if (g.kind === 'pinch') gestureRef.current = { kind: 'done' };
        return;
      }
      gestureRef.current = null;
      if (g.kind !== 'swipe') return;
      const t = e.changedTouches[0];
      const dx = t.clientX - g.x;
      const dy = t.clientY - g.y;
      if (Math.abs(dx) >= SWIPE_MIN_PX && Math.abs(dx) > Math.abs(dy) * 1.5) go(dx < 0 ? 1 : -1);
    };
    const onCancel = () => {
      gestureRef.current = null;
    };
    el.addEventListener('touchstart', onStart, { passive: true });
    el.addEventListener('touchmove', onMove, { passive: false });
    el.addEventListener('touchend', onEnd);
    el.addEventListener('touchcancel', onCancel);
    return () => {
      el.removeEventListener('touchstart', onStart);
      el.removeEventListener('touchmove', onMove);
      el.removeEventListener('touchend', onEnd);
      el.removeEventListener('touchcancel', onCancel);
    };
  }, [isVideo, go]);

  if (!cur) return null;
  // Оригинал (временная ссылка) — для скачивания, видео и фолбэка фото.
  const url = fileId ? urls[fileId] : null;
  // Для фото показываем JpegView (HEIC и пр. браузеро-недружелюбные форматы), если он есть.
  const photoSrc = isVideo ? null : selectPhotoViewSource(cur.name, cur.jpegViewUrl, cur.previews, url);
  const ready = isVideo ? !!url : isSvg ? !!photoSrc || !!url : !!photoSrc;
  // Для действий панели: элементы галерей — MediaItem, у прочих entryIds может не быть.
  const curM = cur as MediaItem;
  const hasEntry = !!(curM.entryIds && curM.entryIds.length > 0);

  function onMouseDown(e: React.MouseEvent) {
    if (isVideo || scale <= 1) return;
    e.preventDefault();
    dragRef.current = { x: e.clientX, y: e.clientY, px: pan.x, py: pan.y };
  }
  function onMouseMove(e: React.MouseEvent) {
    if (!dragRef.current) return;
    setPan({
      x: dragRef.current.px + (e.clientX - dragRef.current.x),
      y: dragRef.current.py + (e.clientY - dragRef.current.y),
    });
  }
  function endDrag() {
    dragRef.current = null;
  }

  function openShareMenu(e: React.MouseEvent) {
    if (!actions) return;
    openAt(e, [
      { label: 'Создать публичную ссылку', icon: 'share', onClick: () => actions.createPublicLink(curM) },
      { label: 'Копировать временную ссылку', icon: 'link', onClick: () => actions.copyTempLink(curM) },
      { label: 'Поделиться с пользователем', icon: 'user', onClick: () => actions.shareWithUser(curM) },
    ]);
  }
  function openAlbumMenu(e: React.MouseEvent) {
    if (!actions) return;
    actions.membership.ensureLoaded();
    const inAlbums = actions.membership.of(curM.id);
    const available = actions.albums.filter((a) => !inAlbums.has(a.id));
    openAt(
      e,
      available.length
        ? available.map((a) => ({ label: a.name, onClick: () => actions.addToAlbum(curM, a.id) }))
        : [{ label: actions.albums.length ? 'Уже во всех альбомах' : 'Нет альбомов', disabled: true }],
    );
  }
  async function copyImage() {
    if (!actions) return;
    try {
      if (!photoSrc) throw new Error('Изображение ещё не загружено');
      // Через same-origin прокси — иначе чужой origin Files даёт CORS/tainted-canvas.
      const blob = await fetchClipboardImage(proxiedImageUrl(photoSrc));
      // Clipboard API принимает только PNG — перегоняем через canvas.
      const bitmap = await createImageBitmap(blob);
      const canvas = document.createElement('canvas');
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      canvas.getContext('2d')!.drawImage(bitmap, 0, 0);
      const png = await new Promise<Blob>((res, rej) =>
        canvas.toBlob((b) => (b ? res(b) : rej(new Error('Не удалось подготовить изображение'))), 'image/png'),
      );
      await navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]);
      actions.toast('Изображение скопировано');
    } catch (e) {
      actions.toast(e instanceof Error ? e.message : 'Не удалось скопировать изображение', 'err');
    }
  }

  return (
    <div className="lightbox" ref={rootRef} onClick={onClose} onMouseMove={onMouseMove} onMouseUp={endDrag} onMouseLeave={endDrag}>
      <button className="lb-close icon-btn" onClick={onClose} title="Закрыть">
        <Icon.x size={24} />
      </button>

      {i > 0 && (
        <button className="lb-nav left" onClick={(e) => { e.stopPropagation(); go(-1); }} title={isVideo ? 'Назад (Shift+←)' : 'Назад (←)'}>
          <Icon.chev size={30} />
        </button>
      )}
      {i < list.length - 1 && (
        <button className="lb-nav right" onClick={(e) => { e.stopPropagation(); go(1); }} title={isVideo ? 'Вперёд (Shift+→)' : 'Вперёд (→)'}>
          <Icon.chev size={30} />
        </button>
      )}

      <div className="lb-stage" ref={stageRef} onClick={(e) => e.stopPropagation()}>
        {err && <div className="lb-msg">Не удалось загрузить: {err}</div>}
        {!err && !ready && (
          <div className="lb-msg">
            <span className="spinner" /> Загрузка…
          </div>
        )}
        {ready && isVideo && (
          <VideoPlayer
            onMediaRef={(el) => {
              videoRef.current = el;
              persistVolumeRef(el);
            }}
            src={url!}
            qualities={videoQualities[fileId!]}
            autoPlay
          />
        )}
        {ready && !isVideo && (
          photoSrc ? (
            <img
              src={photoSrc}
              alt={cur.name || ''}
              draggable={false}
              className={scale > 1 ? 'zoomed' : ''}
              style={{ transform: `translate(${pan.x}px, ${pan.y}px) scale(${scale})` }}
              onMouseDown={onMouseDown}
              onDoubleClick={() => {
                setScale((s) => (s > 1 ? 1 : 2));
                setPan({ x: 0, y: 0 });
              }}
            />
          ) : (
            <div className="lb-msg">Безопасное превью недоступно</div>
          )
        )}
      </div>

      {actions ? (
        <div className="lb-actions" onClick={(e) => e.stopPropagation()}>
          <button className="icon-btn" title="Поделиться" onClick={openShareMenu}>
            <Icon.share size={20} />
          </button>
          <button className="icon-btn" title="Добавить в альбом" onClick={openAlbumMenu}>
            <Icon.plus size={20} />
          </button>
          <button
            className="icon-btn"
            title={hasEntry ? 'Показать в папке' : 'Файл не привязан к папке'}
            disabled={!hasEntry}
            onClick={() => {
              onClose && onClose();
              actions.revealInFolder(curM);
            }}
          >
            <Icon.folder size={20} />
          </button>
          {url && (
            <a className="icon-btn" href={url} download={cur.name} title="Скачать оригинал">
              <Icon.download size={20} />
            </a>
          )}
          {!isVideo && photoSrc && (
            <button className="icon-btn" title="Скопировать в буфер обмена" onClick={copyImage}>
              <Icon.copy size={20} />
            </button>
          )}
          <button className="icon-btn" title="Свойства" onClick={() => actions.showProperties(curM)}>
            <Icon.info size={20} />
          </button>
          <button className="icon-btn danger" title="Удалить" onClick={() => actions.requestDelete(curM)}>
            <Icon.trash size={20} />
          </button>
        </div>
      ) : (
        url && (
          <a className="lb-download btn" href={url} download={cur.name} onClick={(e) => e.stopPropagation()}>
            <Icon.download size={16} /> Скачать оригинал
          </a>
        )
      )}

      {menu && (
        <div onClick={(e) => e.stopPropagation()} onMouseDown={(e) => e.stopPropagation()}>
          {menu}
        </div>
      )}
    </div>
  );
}
