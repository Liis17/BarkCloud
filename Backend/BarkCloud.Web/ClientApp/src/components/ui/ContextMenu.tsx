import React from 'react';
import { Icon } from '../Icon';

export interface ContextItem {
  label?: string;
  icon?: string;
  onClick?: () => void;
  danger?: boolean;
  disabled?: boolean;
  submenu?: ContextItem[];
  divider?: boolean;
}

interface ContextMenuProps {
  x: number;
  y: number;
  items: ContextItem[];
  onClose?: () => void;
}

/** Меню по координатам (x,y). */
export function ContextMenu({ x, y, items, onClose }: ContextMenuProps) {
  const ref = React.useRef<HTMLDivElement | null>(null);
  const subRef = React.useRef<HTMLDivElement | null>(null);
  const [pos, setPos] = React.useState({ x, y });
  const [openSub, setOpenSub] = React.useState<number | null>(null);
  const [subPlacement, setSubPlacement] = React.useState<{ index: number; flip: boolean; left: number | null } | null>(null);

  React.useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    let nx = x,
      ny = y;
    if (x + r.width > window.innerWidth - 8) nx = Math.max(8, window.innerWidth - r.width - 8);
    if (y + r.height > window.innerHeight - 8) ny = Math.max(8, window.innerHeight - r.height - 8);
    setPos({ x: nx, y: ny });
  }, [x, y, items]);

  React.useLayoutEffect(() => {
    if (openSub === null) return;
    const subEl = subRef.current;
    const itemEl = subEl?.parentElement;
    if (!subEl || !itemEl) return;
    const width = subEl.getBoundingClientRect().width;
    const itemRect = itemEl.getBoundingClientRect();
    const rightEdge = window.innerWidth - 8;
    if (itemRect.right + 4 + width <= rightEdge) {
      setSubPlacement(null);
      return;
    }
    if (itemRect.left - 4 - width >= 8) {
      setSubPlacement({ index: openSub, flip: true, left: null });
      return;
    }
    const leftSpace = itemRect.left - 12;
    const rightSpace = rightEdge - itemRect.right - 4;
    if (leftSpace >= rightSpace) {
      setSubPlacement({ index: openSub, flip: false, left: Math.max(-width - 4, 8 - itemRect.left) });
    } else {
      setSubPlacement({ index: openSub, flip: false, left: Math.min(itemRect.width + 4, rightEdge - width - itemRect.left) });
    }
  }, [openSub]);

  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose && onClose();
    };
    const onScroll = () => onClose && onClose();
    window.addEventListener('keydown', onKey);
    window.addEventListener('resize', onScroll);
    window.addEventListener('scroll', onScroll, true);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('resize', onScroll);
      window.removeEventListener('scroll', onScroll, true);
    };
  }, [onClose]);

  function runItem(it: ContextItem) {
    if (it.disabled || (it.submenu && it.submenu.length)) return;
    onClose && onClose();
    it.onClick && it.onClick();
  }

  return (
    <div
      className="ctx-backdrop"
      onMouseDown={onClose}
      onContextMenu={(e) => {
        e.preventDefault();
        onClose && onClose();
      }}
    >
      <div
        className="ctx-menu"
        ref={ref}
        style={{ left: pos.x, top: pos.y }}
        onMouseDown={(e) => e.stopPropagation()}
        onContextMenu={(e) => {
          e.preventDefault();
          e.stopPropagation();
        }}
      >
        {items.map((it, idx) => {
          if (it.divider) return <div key={idx} className="ctx-divider" />;
          const IconC = it.icon ? Icon[it.icon] : null;
          const hasSub = !!(it.submenu && it.submenu.length);
          return (
            <div
              key={idx}
              className={
                'ctx-item' +
                (it.danger ? ' danger' : '') +
                (it.disabled ? ' disabled' : '') +
                (hasSub ? ' has-sub' : '')
              }
              onMouseEnter={() => {
                setOpenSub(hasSub ? idx : null);
                setSubPlacement(null);
              }}
              onClick={() => runItem(it)}
            >
              <span className="ci-ico">{IconC ? <IconC size={17} /> : null}</span>
              <span className="ci-label">{it.label}</span>
              {hasSub && (
                <span className="ci-chev">
                  <Icon.chev size={15} />
                </span>
              )}
              {hasSub && openSub === idx && (
                <div
                  className={'ctx-sub' + (subPlacement?.index === idx && subPlacement.flip ? ' flip' : '')}
                  ref={subRef}
                  style={subPlacement?.index === idx && subPlacement.left !== null ? { left: subPlacement.left, marginLeft: 0 } : undefined}
                >
                  {it.submenu!.map((s, j) => (
                    <div
                      key={j}
                      className={'ctx-item' + (s.disabled ? ' disabled' : '')}
                      onClick={(e) => {
                        e.stopPropagation();
                        if (s.disabled) return;
                        onClose && onClose();
                        s.onClick && s.onClick();
                      }}
                    >
                      <span className="ci-label">{s.label}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

interface ContextMenuState {
  x: number;
  y: number;
  items: ContextItem[];
}

export function useContextMenu() {
  const [state, setState] = React.useState<ContextMenuState | null>(null);
  const openAt = React.useCallback((e: React.MouseEvent, items: ContextItem[]) => {
    e.preventDefault();
    e.stopPropagation();
    setState({ x: e.clientX, y: e.clientY, items });
  }, []);
  const close = React.useCallback(() => setState(null), []);
  const menu = state ? <ContextMenu x={state.x} y={state.y} items={state.items} onClose={close} /> : null;
  return { menu, openAt, close };
}
