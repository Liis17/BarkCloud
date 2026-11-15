import React from 'react';

export const SWIPE_MIN_PX = 50;
export const MAX_SCALE = 5;

type Pan = { x: number; y: number };

type TouchGesture =
  | { kind: 'swipe'; x: number; y: number }
  | { kind: 'pan'; x: number; y: number; px: number; py: number }
  | { kind: 'pinch'; dist: number; scale: number }
  /** Щипок закончился не всеми пальцами сразу: оставшийся палец не должен листать. */
  | { kind: 'done' };

function touchDistance(t: TouchList): number {
  return Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
}

interface ViewerGestures {
  /** Касания, начатые на этих элементах (кнопки, панель плеера), не становятся жестами. */
  ignore: string;
  /** Фото можно зумить щипком и двигать пальцем; у видео — только свайп. */
  zoomable: boolean;
  scale: number;
  pan: Pan;
  setScale: (scale: number) => void;
  setPan: (pan: Pan) => void;
  go: (delta: number) => void;
}

/**
 * Касания просмотрщика медиа (Lightbox, публичный просмотр): свайп листает (без зума),
 * щипок зумит фото, одним пальцем двигается увеличенное фото.
 * Нативные слушатели с passive: false — иначе preventDefault не отменит прокрутку и зум страницы.
 */
export function useViewerGestures(rootRef: React.RefObject<HTMLElement | null>, { ignore, zoomable, scale, pan, setScale, setPan, go }: ViewerGestures) {
  const gestureRef = React.useRef<TouchGesture | null>(null);
  const viewRef = React.useRef({ scale, pan });
  viewRef.current = { scale, pan };

  React.useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    const onStart = (e: TouchEvent) => {
      if (e.target instanceof Element && e.target.closest(ignore)) {
        gestureRef.current = null;
        return;
      }
      const { scale: s, pan: p } = viewRef.current;
      if (e.touches.length >= 2) {
        gestureRef.current = zoomable ? { kind: 'pinch', dist: touchDistance(e.touches), scale: s } : { kind: 'done' };
      } else if (e.touches.length === 1) {
        const t = e.touches[0];
        gestureRef.current = zoomable && s > 1
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
  }, [rootRef, ignore, zoomable, setScale, setPan, go]);
}
