import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { hsvToHex, isValidTagColor, normalizeHex, rgbToHsv, TAG_COLOR_PRESETS, TagColorPicker } from './TagColorPicker';

describe('выбор цвета тега', () => {
  it('проверяет HEX #RRGGBB и предлагает двенадцать пресетов', () => {
    expect(normalizeHex('#aBc123')).toBe('#ABC123');
    expect(normalizeHex('ABC123')).toBeNull();
    expect(normalizeHex('#123')).toBeNull();
    expect(normalizeHex('#11223344')).toBeNull();
    expect(isValidTagColor('#abcdef')).toBe(true);
    expect(isValidTagColor('#abcdef80')).toBe(false);
    expect(TAG_COLOR_PRESETS).toHaveLength(12);
  });

  it('round-trips RGB colors and clamps HSV boundaries', () => {
    for (const color of ['#FF0000', '#7F00FF', '#123456', '#FFFFFF', '#000000']) {
      const { h, s, v } = rgbToHsv(color);
      expect(hsvToHex(h, s, v)).toBe(color);
    }
    expect(hsvToHex(360, 2, 2)).toBe('#FF0000');
    expect(hsvToHex(-120, -1, 1)).toBe('#FFFFFF');
    expect(rgbToHsv('#FFFFFF', 240).h).toBe(240);
  });

  it('selects presets immediately and exposes keyboard saturation/value controls', () => {
    const onChange = vi.fn();
    render(<TagColorPicker value="#6750A4" onChange={onChange} onValidityChange={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: 'Выбрать цвет #006A6B' }));
    expect(onChange).toHaveBeenLastCalledWith('#006A6B');
    expect(screen.getByRole('slider', { name: 'Оттенок' })).toBeTruthy();
    expect(screen.getByRole('slider', { name: 'Насыщенность' })).toBeTruthy();
    expect(screen.getByRole('slider', { name: 'Яркость' })).toBeTruthy();
    fireEvent.change(screen.getByRole('slider', { name: 'Насыщенность' }), { target: { value: '100' } });
    expect(onChange).toHaveBeenCalled();
  });

  it('captures pointer input and clamps saturation/value at the edges', () => {
    const onChange = vi.fn();
    const { container } = render(<TagColorPicker value="#FF0000" onChange={onChange} onValidityChange={vi.fn()} />);
    const area = container.querySelector('.tag-color-sv')!;
    vi.spyOn(area, 'getBoundingClientRect').mockReturnValue({
      x: 100, y: 50, left: 100, top: 50, right: 200, bottom: 150, width: 100, height: 100, toJSON: () => ({}),
    } as DOMRect);
    const capture = vi.fn();
    const release = vi.fn();
    Object.defineProperty(area, 'setPointerCapture', { configurable: true, value: capture });
    Object.defineProperty(area, 'releasePointerCapture', { configurable: true, value: release });
    const pointer = (type: string, pointerId: number, clientX: number, clientY: number) => {
      const event = new Event(type, { bubbles: true });
      Object.assign(event, { pointerId, pointerType: 'touch', clientX, clientY });
      fireEvent(area, event);
    };

    pointer('pointerdown', 7, 150, 75);
    expect(capture).toHaveBeenCalledWith(7);
    expect(onChange).toHaveBeenLastCalledWith('#BF6060');
    pointer('pointermove', 8, 200, 150);
    expect(onChange).toHaveBeenLastCalledWith('#BF6060');
    pointer('pointermove', 7, 200, 200);
    expect(onChange).toHaveBeenLastCalledWith('#000000');
    pointer('pointerup', 7, 200, 200);
    expect(release).toHaveBeenCalledWith(7);
  });

  it('сохраняет оттенок при редактировании серого цвета', () => {
    const onChange = vi.fn();
    render(<TagColorPicker value="#777777" onChange={onChange} onValidityChange={vi.fn()} />);

    fireEvent.change(screen.getByRole('slider', { name: 'Оттенок' }), { target: { value: '240' } });
    fireEvent.change(screen.getByRole('slider', { name: 'Насыщенность' }), { target: { value: '100' } });
    expect(onChange).toHaveBeenLastCalledWith('#000077');
  });

  it('оставляет частичный HEX локальным и синхронизирует только допустимое значение', () => {
    const onChange = vi.fn();
    const onValidityChange = vi.fn();
    render(<TagColorPicker value="#123456" onChange={onChange} onValidityChange={onValidityChange} />);
    const hex = screen.getByRole('textbox', { name: 'HEX цвета тега' });

    fireEvent.change(hex, { target: { value: '#12' } });
    expect((hex as HTMLInputElement).value).toBe('#12');
    expect(onChange).not.toHaveBeenCalled();
    expect(onValidityChange).toHaveBeenLastCalledWith(false);
    fireEvent.change(hex, { target: { value: '#abcdef' } });
    expect(onChange).toHaveBeenLastCalledWith('#ABCDEF');
    expect(onValidityChange).toHaveBeenLastCalledWith(true);
  });
});
