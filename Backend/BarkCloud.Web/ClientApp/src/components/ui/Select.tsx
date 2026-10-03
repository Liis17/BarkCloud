import React from 'react';
import { createPortal } from 'react-dom';
import { Icon } from '../Icon';

export interface SelectOption<T extends string | number> {
  value: T;
  label: string;
}

interface SelectProps<T extends string | number> {
  value: T;
  options: SelectOption<T>[];
  onChange: (value: T) => void;
  'aria-label'?: string;
  disabled?: boolean;
  className?: string;
}

interface MenuPos {
  left: number;
  width: number;
  top?: number;
  bottom?: number;
  maxHeight: number;
}

/** M3 outlined select: поле-кнопка + меню-список. Меню рендерится в портал, чтобы
 *  его не обрезали модалки и прокручиваемые контейнеры. */
export function Select<T extends string | number>({ value, options, onChange, disabled, className, ...rest }: SelectProps<T>) {
  const id = React.useId();
  const button = React.useRef<HTMLButtonElement>(null);
  const list = React.useRef<HTMLUListElement>(null);
  const [open, setOpen] = React.useState(false);
  const [active, setActive] = React.useState(0);
  const [pos, setPos] = React.useState<MenuPos | null>(null);
  const selectedIndex = options.findIndex((o) => o.value === value);
  const current = options[selectedIndex];

  const close = React.useCallback((focusButton: boolean) => {
    setOpen(false);
    if (focusButton) button.current?.focus();
  }, []);

  function openMenu(index = Math.max(0, selectedIndex)) {
    const r = button.current?.getBoundingClientRect();
    if (disabled || !r) return;
    const below = window.innerHeight - r.bottom - 8;
    const above = r.top - 8;
    const up = below < 200 && above > below;
    setPos({
      left: r.left,
      width: r.width,
      top: up ? undefined : r.bottom + 4,
      bottom: up ? window.innerHeight - r.top + 4 : undefined,
      maxHeight: Math.min(320, up ? above : below),
    });
    setActive(index);
    setOpen(true);
  }

  function choose(index: number) {
    const option = options[index];
    close(true);
    if (option && option.value !== value) onChange(option.value);
  }

  React.useEffect(() => {
    if (!open) return;
    list.current?.focus();
    const outside = (e: Event) => {
      const t = e.target as Node;
      if (!list.current?.contains(t) && !button.current?.contains(t)) close(false);
    };
    const onResize = () => close(false);
    document.addEventListener('mousedown', outside);
    document.addEventListener('scroll', outside, true);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('mousedown', outside);
      document.removeEventListener('scroll', outside, true);
      window.removeEventListener('resize', onResize);
    };
  }, [open, close]);

  React.useEffect(() => {
    if (open) list.current?.children[active]?.scrollIntoView?.({ block: 'nearest' });
  }, [open, active]);

  function onButtonKey(e: React.KeyboardEvent) {
    if (['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(e.key)) {
      e.preventDefault();
      openMenu();
    }
  }

  function onListKey(e: React.KeyboardEvent) {
    const last = options.length - 1;
    if (e.key === 'ArrowDown') setActive((i) => Math.min(last, i + 1));
    else if (e.key === 'ArrowUp') setActive((i) => Math.max(0, i - 1));
    else if (e.key === 'Home') setActive(0);
    else if (e.key === 'End') setActive(last);
    else if (e.key === 'Enter' || e.key === ' ') choose(active);
    else if (e.key === 'Escape') close(true);
    else if (e.key === 'Tab') { close(false); return; }
    else return;
    e.preventDefault();
    e.stopPropagation(); // Escape не должен закрывать модалку под меню
  }

  return (
    <>
      <button
        ref={button}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        aria-label={rest['aria-label']}
        disabled={disabled}
        className={'m3-select' + (open ? ' open' : '') + (className ? ' ' + className : '')}
        onClick={() => (open ? close(false) : openMenu())}
        onKeyDown={onButtonKey}
      >
        <span className="m3-select-value">{current?.label ?? ''}</span>
        <Icon.chevDown size={18} />
      </button>
      {open && pos && createPortal(
        <ul
          ref={list}
          id={id}
          role="listbox"
          tabIndex={-1}
          aria-label={rest['aria-label']}
          aria-activedescendant={`${id}-${active}`}
          className="m3-select-menu"
          style={{ left: pos.left, minWidth: pos.width, top: pos.top, bottom: pos.bottom, maxHeight: pos.maxHeight }}
          onKeyDown={onListKey}
        >
          {options.map((o, i) => (
            <li
              key={String(o.value)}
              id={`${id}-${i}`}
              role="option"
              aria-selected={i === selectedIndex}
              className={(i === selectedIndex ? 'selected' : '') + (i === active ? ' active' : '')}
              onMouseEnter={() => setActive(i)}
              onClick={() => choose(i)}
            >
              {o.label}
            </li>
          ))}
        </ul>,
        document.body,
      )}
    </>
  );
}
