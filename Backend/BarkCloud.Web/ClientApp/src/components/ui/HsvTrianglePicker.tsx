import React from 'react';
import { hsvToHex, normalizeHex, rgbToHsv } from './TagColorPicker';

type Point = { x: number; y: number };

const RING_WIDTH = 0.13; // доля радиуса под кольцо оттенка
const GAP = 0.05;        // зазор между кольцом и треугольником

/** Красный слева, оттенок растёт по часовой стрелке (как в Krita). */
const hueToAngle = (hue: number) => ((180 + hue) * Math.PI) / 180;
const angleToHue = (angle: number) => (((angle * 180) / Math.PI - 180) % 360 + 360) % 360;

function geometry(size: number) {
  const c = size / 2;
  const outer = c - 1;
  const inner = outer * (1 - RING_WIDTH);
  const tri = inner * (1 - GAP);
  const at = (deg: number): Point => ({ x: c + tri * Math.cos((deg * Math.PI) / 180), y: c + tri * Math.sin((deg * Math.PI) / 180) });
  // Ориентация фиксирована: вверху чёрный, справа снизу чистый оттенок, слева снизу белый.
  return { c, outer, inner, black: at(-90), hue: at(30), white: at(150) };
}

type Geometry = ReturnType<typeof geometry>;

/** Барицентрические веса точки относительно вершин (оттенок, белый, чёрный). */
function weights(g: Geometry, p: Point) {
  const { hue: a, white: b, black: c } = g;
  const det = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
  const wh = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / det;
  const ww = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / det;
  return { wh, ww, wb: 1 - wh - ww };
}

function closestOnSegment(p: Point, a: Point, b: Point): Point {
  const dx = b.x - a.x, dy = b.y - a.y;
  const t = Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / (dx * dx + dy * dy)));
  return { x: a.x + t * dx, y: a.y + t * dy };
}

/** S/V по точке; за пределами треугольника берётся ближайшая точка на его границе. */
export function pointToSv(g: Geometry, p: Point): { s: number; v: number } {
  let w = weights(g, p);
  if (w.wh < 0 || w.ww < 0 || w.wb < 0) {
    const candidates = [closestOnSegment(p, g.hue, g.white), closestOnSegment(p, g.white, g.black), closestOnSegment(p, g.black, g.hue)];
    const nearest = candidates.reduce((best, q) => (Math.hypot(q.x - p.x, q.y - p.y) < Math.hypot(best.x - p.x, best.y - p.y) ? q : best));
    w = weights(g, nearest);
  }
  const wh = Math.max(0, w.wh), ww = Math.max(0, w.ww);
  const v = Math.min(1, wh + ww);
  return { s: v > 0 ? Math.min(1, wh / v) : 0, v };
}

/** Обратное преобразование: w_hue = S·V, w_white = V·(1−S), w_black = 1−V. */
function svToPoint(g: Geometry, s: number, v: number): Point {
  const wh = s * v, ww = v * (1 - s), wb = 1 - v;
  return { x: wh * g.hue.x + ww * g.white.x + wb * g.black.x, y: wh * g.hue.y + ww * g.white.y + wb * g.black.y };
}

function hexToRgb(hex: string) {
  return [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
}

function paint(canvas: HTMLCanvasElement, size: number, hue: number) {
  const ctx = canvas.getContext?.('2d');
  if (!ctx) return; // jsdom и окружения без canvas
  const ratio = window.devicePixelRatio || 1;
  const px = Math.round(size * ratio);
  canvas.width = px;
  canvas.height = px;
  const g = geometry(size);
  const image = ctx.createImageData(px, px);
  const [hr, hg, hb] = hexToRgb(hsvToHex(hue, 1, 1));
  for (let y = 0; y < px; y++) {
    for (let x = 0; x < px; x++) {
      const p = { x: (x + 0.5) / ratio, y: (y + 0.5) / ratio };
      const dist = Math.hypot(p.x - g.c, p.y - g.c);
      let rgb: number[] | null = null;
      let alpha = 255;
      if (dist <= g.outer + 0.5 && dist >= g.inner - 0.5) {
        rgb = hexToRgb(hsvToHex(angleToHue(Math.atan2(p.y - g.c, p.x - g.c)), 1, 1));
        const edge = Math.min(g.outer - dist, dist - g.inner) + 0.5;
        alpha = Math.round(255 * Math.max(0, Math.min(1, edge)));
      } else if (dist < g.inner) {
        const w = weights(g, p);
        if (w.wh >= -0.004 && w.ww >= -0.004 && w.wb >= -0.004) {
          const wh = Math.max(0, w.wh), ww = Math.max(0, w.ww);
          rgb = [wh * hr + ww * 255, wh * hg + ww * 255, wh * hb + ww * 255];
        }
      }
      if (!rgb) continue;
      const i = (y * px + x) * 4;
      image.data[i] = rgb[0];
      image.data[i + 1] = rgb[1];
      image.data[i + 2] = rgb[2];
      image.data[i + 3] = alpha;
    }
  }
  ctx.putImageData(image, 0, 0);
}

interface HsvTrianglePickerProps {
  /** Текущий цвет #RRGGBB. */
  value: string;
  onChange: (hex: string) => void;
  size?: number;
}

/** Круговой выбор цвета: кольцо оттенка и треугольник насыщенности/яркости. */
export function HsvTrianglePicker({ value, onChange, size = 216 }: HsvTrianglePickerProps) {
  const canvas = React.useRef<HTMLCanvasElement>(null);
  const drag = React.useRef<{ pointer: number; mode: 'ring' | 'triangle' } | null>(null);
  const normalized = normalizeHex(value) || '#000000';
  const [hue, setHue] = React.useState(() => rgbToHsv(normalized).h);
  const hsv = rgbToHsv(normalized, hue);
  const emitted = React.useRef(normalized);
  // Оттенок берём из цвета только при внешнем изменении (HEX, пресет): у серых его нет,
  // а HEX после перетаскивания квантован — пересчёт сдвигал бы кольцо.
  React.useEffect(() => {
    if (normalized === emitted.current) return;
    emitted.current = normalized;
    const external = rgbToHsv(normalized);
    if (external.s > 0) setHue(external.h);
  }, [normalized]);
  const emit = (hex: string) => {
    emitted.current = hex;
    onChange(hex);
  };
  React.useEffect(() => {
    if (canvas.current) paint(canvas.current, size, hue);
  }, [hue, size]);

  const g = geometry(size);
  const local = (e: React.PointerEvent<HTMLElement>): Point => {
    const rect = e.currentTarget.getBoundingClientRect();
    return { x: ((e.clientX - rect.left) * size) / (rect.width || size), y: ((e.clientY - rect.top) * size) / (rect.height || size) };
  };
  function apply(mode: 'ring' | 'triangle', p: Point) {
    if (mode === 'ring') {
      const nextHue = angleToHue(Math.atan2(p.y - g.c, p.x - g.c));
      setHue(nextHue);
      emit(hsvToHex(nextHue, hsv.s, hsv.v));
    } else {
      const sv = pointToSv(g, p);
      emit(hsvToHex(hue, sv.s, sv.v));
    }
  }
  function onPointerDown(e: React.PointerEvent<HTMLDivElement>) {
    const p = local(e);
    const dist = Math.hypot(p.x - g.c, p.y - g.c);
    if (dist > g.outer + 4) return;
    e.preventDefault();
    const mode = dist >= g.inner - 2 ? 'ring' : 'triangle';
    drag.current = { pointer: e.pointerId, mode };
    e.currentTarget.setPointerCapture?.(e.pointerId);
    apply(mode, p);
  }
  function onPointerMove(e: React.PointerEvent<HTMLDivElement>) {
    if (drag.current?.pointer === e.pointerId) apply(drag.current.mode, local(e));
  }
  function onPointerEnd(e: React.PointerEvent<HTMLDivElement>) {
    if (drag.current?.pointer !== e.pointerId) return;
    drag.current = null;
    e.currentTarget.releasePointerCapture?.(e.pointerId);
  }

  const ringRadius = (g.outer + g.inner) / 2;
  const ringThumb = { x: g.c + ringRadius * Math.cos(hueToAngle(hue)), y: g.c + ringRadius * Math.sin(hueToAngle(hue)) };
  const svThumb = svToPoint(g, hsv.s, hsv.v);
  return (
    <div
      className="hsv-triangle"
      style={{ width: size, height: size }}
      aria-hidden="true"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerEnd}
      onPointerCancel={onPointerEnd}
    >
      <canvas ref={canvas} style={{ width: size, height: size }} />
      <span className="hsv-thumb ring" style={{ left: ringThumb.x, top: ringThumb.y, background: hsvToHex(hue, 1, 1) }} />
      <span className="hsv-thumb" style={{ left: svThumb.x, top: svThumb.y, background: normalized }} />
    </div>
  );
}
