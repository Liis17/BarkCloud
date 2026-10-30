import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ContextItem, ContextMenu } from './ContextMenu';

const originalInnerWidth = Object.getOwnPropertyDescriptor(window, 'innerWidth');

function rect(left: number, top: number, width: number, height: number): DOMRect {
  return { x: left, y: top, left, top, right: left + width, bottom: top + height, width, height, toJSON: () => ({}) } as DOMRect;
}

function mockRects(positions: Record<string, { left: number; width: number }>) {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    if (this.classList.contains('ctx-sub')) return rect(0, 0, 200, 100);
    if (this.classList.contains('ctx-item') && this.classList.contains('has-sub')) {
      const label = this.querySelector('.ci-label')?.textContent ?? '';
      const position = positions[label];
      return position ? rect(position.left, 100, position.width, 32) : rect(0, 0, 0, 0);
    }
    return rect(0, 0, 0, 0);
  });
}

function hoverItem(label: string) {
  const item = screen.getByText(label, { exact: true }).closest('.ctx-item');
  if (!item) throw new Error(`Context item not found: ${label}`);
  fireEvent.mouseEnter(item);
}

function submenu() {
  const el = document.querySelector('.ctx-sub');
  if (!el) throw new Error('Submenu not found');
  return el as HTMLDivElement;
}

afterEach(() => {
  cleanup();
  if (originalInnerWidth) Object.defineProperty(window, 'innerWidth', originalInnerWidth);
  vi.restoreAllMocks();
});

describe('ContextMenu', () => {
  it('открывает подменю влево у правого края окна', () => {
    mockRects({ Edge: { left: 850, width: 150 } });
    render(<ContextMenu x={850} y={100} items={[{ label: 'Edge', submenu: [{ label: 'Action' }] }]} />);

    hoverItem('Edge');

    expect(submenu().classList.contains('flip')).toBe(true);
  });

  it('оставляет подменю справа, когда для него хватает места', () => {
    mockRects({ Middle: { left: 350, width: 150 } });
    render(<ContextMenu x={350} y={100} items={[{ label: 'Middle', submenu: [{ label: 'Action' }] }]} />);

    hoverItem('Middle');

    expect(submenu().classList.contains('flip')).toBe(false);
  });

  it('сбрасывает сторону при переходе между подменю в обоих направлениях', () => {
    mockRects({ Edge: { left: 850, width: 150 }, Middle: { left: 350, width: 150 } });
    render(
      <ContextMenu
        x={350}
        y={100}
        items={[
          { label: 'Edge', submenu: [{ label: 'Edge action' }] },
          { label: 'Middle', submenu: [{ label: 'Middle action' }] },
        ]}
      />,
    );

    hoverItem('Edge');
    expect(submenu().classList.contains('flip')).toBe(true);
    hoverItem('Middle');
    expect(submenu().classList.contains('flip')).toBe(false);
    hoverItem('Edge');
    expect(submenu().classList.contains('flip')).toBe(true);
    hoverItem('Middle');
    expect(submenu().classList.contains('flip')).toBe(false);
  });

  it('прижимает подменю к краю в узком окне', () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 360 });
    const positions = { Middle: { left: 150, width: 160 } };
    mockRects(positions);
    render(<ContextMenu x={150} y={100} items={[{ label: 'Middle', submenu: [{ label: 'Action' }] }]} />);

    hoverItem('Middle');

    const style = submenu().style;
    const left = positions.Middle.left + parseFloat(style.left) + parseFloat(style.marginLeft || '0');
    expect(style.left).not.toBe('');
    expect(left).toBeGreaterThanOrEqual(8);
    expect(left + 200).toBeLessThanOrEqual(window.innerWidth - 8);
  });

  it('вызывает действие пункта подменю и закрывает меню', () => {
    const onClick = vi.fn();
    const items: ContextItem[] = [{ label: 'Parent', submenu: [{ label: 'Action', onClick }] }];
    function Harness() {
      const [open, setOpen] = React.useState(true);
      return open ? <ContextMenu x={100} y={100} items={items} onClose={() => setOpen(false)} /> : null;
    }
    render(<Harness />);

    hoverItem('Parent');
    fireEvent.click(screen.getByText('Action', { exact: true }));

    expect(onClick).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Action', { exact: true })).toBeNull();
  });
});
