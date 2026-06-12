import React from 'react';
import type { CardFile } from '../lib/types';

const APP_NAME = 'BarkCloud';
const DEFAULT_ICON_URL = '/barkcloud-icon-brand-b.png';
const DYNAMIC_ICON_ATTR = 'data-barkcloud-dynamic-icon';
const ICON_SIZE = 64;
const ICON_RADIUS = 14;
const roundedIconCache = new Map<string, string>();

export interface DocumentHeadDescriptor {
  title?: string | null;
  iconUrl?: string | null;
}

interface DocumentHeadEntry {
  id: symbol;
  order: number;
  priority: number;
  head: DocumentHeadDescriptor;
}

interface DocumentHeadContextValue {
  setHead: (id: symbol, priority: number, head: DocumentHeadDescriptor) => void;
  clearHead: (id: symbol) => void;
}

const DocumentHeadContext = React.createContext<DocumentHeadContextValue>({
  setHead: () => {},
  clearHead: () => {},
});

function formatTitle(title: string | null | undefined): string {
  const t = (title || '').trim();
  return t ? `${t} - ${APP_NAME}` : APP_NAME;
}

function activeEntry(entries: DocumentHeadEntry[]): DocumentHeadDescriptor | null {
  if (entries.length === 0) return null;
  return entries.reduce((best, item) => {
    if (item.priority !== best.priority) return item.priority > best.priority ? item : best;
    return item.order > best.order ? item : best;
  }).head;
}

function ensureIconLink(): HTMLLinkElement {
  const selector = `link[rel~="icon"][${DYNAMIC_ICON_ATTR}]`;
  let link = document.head.querySelector<HTMLLinkElement>(selector);
  if (!link) link = document.head.querySelector<HTMLLinkElement>('link[rel~="icon"]');
  if (!link) {
    link = document.createElement('link');
    document.head.appendChild(link);
  }
  link.rel = 'icon';
  link.setAttribute(DYNAMIC_ICON_ATTR, 'true');
  return link;
}

function setIconLink(href: string, type = 'image/png'): void {
  const link = ensureIconLink();
  link.href = href;
  link.type = type;
  link.removeAttribute('sizes');
}

function roundedRect(ctx: CanvasRenderingContext2D, size: number, radius: number): void {
  const r = Math.min(radius, size / 2);
  ctx.beginPath();
  ctx.moveTo(r, 0);
  ctx.lineTo(size - r, 0);
  ctx.quadraticCurveTo(size, 0, size, r);
  ctx.lineTo(size, size - r);
  ctx.quadraticCurveTo(size, size, size - r, size);
  ctx.lineTo(r, size);
  ctx.quadraticCurveTo(0, size, 0, size - r);
  ctx.lineTo(0, r);
  ctx.quadraticCurveTo(0, 0, r, 0);
  ctx.closePath();
}

function loadIconImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('icon load failed'));
    img.src = src;
  });
}

function iconCanvasSource(src: string): string {
  const url = new URL(src, window.location.href);
  if (url.origin === window.location.origin) return url.href;
  return `/api/head/icon?url=${encodeURIComponent(url.href)}`;
}

async function makeRoundedIcon(src: string): Promise<string> {
  const cached = roundedIconCache.get(src);
  if (cached) return cached;

  const img = await loadIconImage(iconCanvasSource(src));
  const canvas = document.createElement('canvas');
  canvas.width = ICON_SIZE;
  canvas.height = ICON_SIZE;

  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('canvas unavailable');

  const scale = Math.max(ICON_SIZE / img.naturalWidth, ICON_SIZE / img.naturalHeight);
  const width = img.naturalWidth * scale;
  const height = img.naturalHeight * scale;
  const x = (ICON_SIZE - width) / 2;
  const y = (ICON_SIZE - height) / 2;

  ctx.clearRect(0, 0, ICON_SIZE, ICON_SIZE);
  roundedRect(ctx, ICON_SIZE, ICON_RADIUS);
  ctx.clip();
  ctx.drawImage(img, x, y, width, height);

  const dataUrl = canvas.toDataURL('image/png');
  roundedIconCache.set(src, dataUrl);
  return dataUrl;
}

async function applyDocumentHead(head: DocumentHeadDescriptor | null, cancelled: () => boolean): Promise<void> {
  document.title = formatTitle(head?.title);

  const iconUrl = (head?.iconUrl || DEFAULT_ICON_URL).trim();
  try {
    const rounded = await makeRoundedIcon(iconUrl);
    if (!cancelled()) setIconLink(rounded);
  } catch {
    if (!cancelled()) setIconLink(DEFAULT_ICON_URL);
  }
}

export function pickDocumentIcon(media: Pick<CardFile, 'previews' | 'jpegViewUrl'> | null | undefined): string | null {
  const previews = media?.previews || [];
  if (previews.length > 0) {
    const smallest = previews.reduce((best, item) => {
      const bestWidth = best.target || best.w || Number.MAX_SAFE_INTEGER;
      const itemWidth = item.target || item.w || Number.MAX_SAFE_INTEGER;
      return itemWidth < bestWidth ? item : best;
    }, previews[0]);
    return smallest.url || null;
  }
  return media?.jpegViewUrl || null;
}

export function DocumentHeadProvider({ children }: { children: React.ReactNode }) {
  const [entries, setEntries] = React.useState<DocumentHeadEntry[]>([]);
  const nextOrder = React.useRef(1);

  const setHead = React.useCallback((id: symbol, priority: number, head: DocumentHeadDescriptor) => {
    setEntries((prev) => {
      const index = prev.findIndex((item) => item.id === id);
      if (index >= 0) {
        const next = prev.slice();
        next[index] = { ...next[index], priority, head };
        return next;
      }
      return [...prev, { id, priority, head, order: nextOrder.current++ }];
    });
  }, []);

  const clearHead = React.useCallback((id: symbol) => {
    setEntries((prev) => prev.filter((item) => item.id !== id));
  }, []);

  const head = React.useMemo(() => activeEntry(entries), [entries]);

  React.useEffect(() => {
    let cancelled = false;
    applyDocumentHead(head, () => cancelled);
    return () => {
      cancelled = true;
    };
  }, [head]);

  const value = React.useMemo(() => ({ setHead, clearHead }), [setHead, clearHead]);

  return <DocumentHeadContext.Provider value={value}>{children}</DocumentHeadContext.Provider>;
}

export function useDocumentHead(factory: () => DocumentHeadDescriptor, deps: React.DependencyList, priority = 0): void {
  const { setHead, clearHead } = React.useContext(DocumentHeadContext);
  const id = React.useRef<symbol | null>(null);
  if (!id.current) id.current = Symbol('document-head');

  React.useEffect(() => {
    const currentId = id.current!;
    setHead(currentId, priority, factory());
    return () => clearHead(currentId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}
