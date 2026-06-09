import React from 'react';
import type { CardFile } from '../lib/types';

const APP_NAME = 'BarkCloud';
const DEFAULT_ICON_URL = '/favicon.svg';
const DYNAMIC_ICON_ATTR = 'data-barkcloud-dynamic-icon';

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

function applyDocumentHead(head: DocumentHeadDescriptor | null): void {
  document.title = formatTitle(head?.title);

  const iconUrl = (head?.iconUrl || '').trim();
  const link = ensureIconLink();
  if (iconUrl) {
    link.href = iconUrl;
    link.type = 'image/jpeg';
    link.removeAttribute('sizes');
    return;
  }

  link.href = DEFAULT_ICON_URL;
  link.type = 'image/svg+xml';
  link.setAttribute('sizes', 'any');
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
    applyDocumentHead(head);
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
