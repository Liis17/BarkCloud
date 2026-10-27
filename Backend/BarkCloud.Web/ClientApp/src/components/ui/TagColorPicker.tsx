import React from 'react';
import { TextField } from './TextField';

export const TAG_COLOR_PRESETS = [
  '#6750A4', '#006A6B', '#006D3C', '#8B5000', '#B3261E', '#984061',
  '#345DA8', '#4E6351', '#626200', '#715573', '#777777', '#000000',
] as const;

export function normalizeHex(value: string): string | null {
  const match = /^#([\da-f]{6})$/i.exec(value.trim());
  return match ? `#${match[1].toUpperCase()}` : null;
}

export function isValidTagColor(value: string): boolean {
  return normalizeHex(value) !== null;
}

export function rgbToHsv(hex: string, hueWhenGray = 0): { h: number; s: number; v: number } {
  const normalized = normalizeHex(hex);
  if (!normalized) return { h: hueWhenGray, s: 0, v: 0 };
  const rgb = [1, 3, 5].map((index) => parseInt(normalized.slice(index, index + 2), 16) / 255);
  const [r, g, b] = rgb;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const delta = max - min;
  if (delta === 0) return { h: hueWhenGray, s: 0, v: max };
  let h = 0;
  if (max === r) h = 60 * (((g - b) / delta) % 6);
  else if (max === g) h = 60 * ((b - r) / delta + 2);
  else h = 60 * ((r - g) / delta + 4);
  if (h < 0) h += 360;
  return { h, s: max === 0 ? 0 : delta / max, v: max };
}

export function hsvToHex(hue: number, saturation: number, value: number): string {
  const h = ((hue % 360) + 360) % 360;
  const s = Math.max(0, Math.min(1, saturation));
  const v = Math.max(0, Math.min(1, value));
  const chroma = v * s;
  const x = chroma * (1 - Math.abs(((h / 60) % 2) - 1));
  const offset = v - chroma;
  const sector = Math.floor(h / 60);
  const channels = sector === 0 ? [chroma, x, 0]
    : sector === 1 ? [x, chroma, 0]
      : sector === 2 ? [0, chroma, x]
        : sector === 3 ? [0, x, chroma]
          : sector === 4 ? [x, 0, chroma]
            : [chroma, 0, x];
  return `#${channels.map((channel) => Math.round((channel + offset) * 255).toString(16).padStart(2, '0')).join('').toUpperCase()}`;
}

interface TagColorPickerProps {
  value: string;
  disabled?: boolean;
  onChange: (value: string) => void;
  onValidityChange: (valid: boolean) => void;
}

export function TagColorPicker({ value, disabled = false, onChange, onValidityChange }: TagColorPickerProps) {
  const initial = rgbToHsv(value);
  const [hue, setHue] = React.useState(initial.h);
  const [saturation, setSaturation] = React.useState(initial.s);
  const [brightness, setBrightness] = React.useState(initial.v);
  const [hexInput, setHexInput] = React.useState(normalizeHex(value) || value);
  const [hexValid, setHexValid] = React.useState(isValidTagColor(value));
  const [hexTouched, setHexTouched] = React.useState(false);
  const dragPointer = React.useRef<number | null>(null);

  React.useEffect(() => {
    const normalized = normalizeHex(value);
    if (!normalized) {
      setHexInput(value);
      setHexValid(false);
      onValidityChange(false);
      return;
    }
    const hsv = rgbToHsv(normalized, hue);
    setHexInput(normalized);
    setSaturation(hsv.s);
    setBrightness(hsv.v);
    if (hsv.s > 0) setHue(hsv.h);
    setHexValid(true);
    onValidityChange(true);
  }, [value, hue, onValidityChange]);

  function updateHsv(nextHue: number, nextSaturation: number, nextBrightness: number) {
    const color = hsvToHex(nextHue, nextSaturation, nextBrightness);
    setHue(nextHue);
    setSaturation(nextSaturation);
    setBrightness(nextBrightness);
    setHexInput(color);
    setHexValid(true);
    setHexTouched(false);
    onValidityChange(true);
    onChange(color);
  }

  function selectPreset(color: string) {
    const hsv = rgbToHsv(color, hue);
    updateHsv(hsv.s > 0 ? hsv.h : hue, hsv.s, hsv.v);
  }

  function updateFromPointer(event: React.PointerEvent<HTMLDivElement>) {
    const rect = event.currentTarget.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    const nextSaturation = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
    const nextBrightness = 1 - Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height));
    updateHsv(hue, nextSaturation, nextBrightness);
  }

  function onPointerDown(event: React.PointerEvent<HTMLDivElement>) {
    if (disabled) return;
    event.preventDefault();
    dragPointer.current = event.pointerId;
    event.currentTarget.setPointerCapture?.(event.pointerId);
    updateFromPointer(event);
  }

  function onPointerMove(event: React.PointerEvent<HTMLDivElement>) {
    if (dragPointer.current === event.pointerId) updateFromPointer(event);
  }

  function finishPointer(event: React.PointerEvent<HTMLDivElement>) {
    if (dragPointer.current !== event.pointerId) return;
    dragPointer.current = null;
    event.currentTarget.releasePointerCapture?.(event.pointerId);
  }

  function changeHex(raw: string) {
    setHexInput(raw);
    setHexTouched(true);
    const normalized = normalizeHex(raw);
    setHexValid(normalized !== null);
    onValidityChange(normalized !== null);
    if (normalized) onChange(normalized);
  }

  return (
    <div className="tag-color-picker" role="group" aria-label="Выбор цвета">
      <div className="tag-color-presets" aria-label="Готовые цвета">
        {TAG_COLOR_PRESETS.map((color) => <button
          key={color}
          type="button"
          className="tag-color-preset"
          style={{ backgroundColor: color }}
          aria-label={`Выбрать цвет ${color}`}
          aria-pressed={normalizeHex(value) === color}
          disabled={disabled}
          onClick={() => selectPreset(color)}
        />)}
      </div>
      <div className="tag-color-editor">
        <div
          className="tag-color-sv"
          style={{ backgroundImage: `linear-gradient(0deg, #000, transparent), linear-gradient(90deg, #fff, hsl(${hue} 100% 50%))` }}
          aria-hidden="true"
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={finishPointer}
          onPointerCancel={finishPointer}
        >
          <span className="tag-color-sv-thumb" style={{ left: `${saturation * 100}%`, top: `${(1 - brightness) * 100}%` }} />
        </div>
        <div className="tag-color-controls">
          <label className="tag-color-slider-label" htmlFor="tag-color-hue">Оттенок</label>
          <input id="tag-color-hue" className="tag-color-hue" type="range" min="0" max="359" step="1" value={Math.round(hue)} disabled={disabled} aria-label="Оттенок" onChange={(event) => updateHsv(Number(event.currentTarget.value), saturation, brightness)} />
          <label className="tag-color-slider-label" htmlFor="tag-color-saturation">Насыщенность</label>
          <input id="tag-color-saturation" type="range" min="0" max="100" step="1" value={Math.round(saturation * 100)} disabled={disabled} aria-label="Насыщенность" onChange={(event) => updateHsv(hue, Number(event.currentTarget.value) / 100, brightness)} />
          <label className="tag-color-slider-label" htmlFor="tag-color-brightness">Яркость</label>
          <input id="tag-color-brightness" type="range" min="0" max="100" step="1" value={Math.round(brightness * 100)} disabled={disabled} aria-label="Яркость" onChange={(event) => updateHsv(hue, saturation, Number(event.currentTarget.value) / 100)} />
        </div>
      </div>
      <div className="tag-color-hex-row">
        <TextField label="HEX" aria-label="HEX цвета тега" value={hexInput} maxLength={7} disabled={disabled} spellCheck={false} autoCapitalize="characters"
          aria-invalid={!hexValid} error={hexTouched && !hexValid ? 'Введите цвет в формате #RRGGBB.' : undefined}
          onChange={(event) => changeHex(event.currentTarget.value)} />
        <span className="tag-color-preview" role="img" style={{ backgroundColor: normalizeHex(value) || '#000000' }} aria-label={`Предпросмотр цвета ${normalizeHex(value) || '#000000'}`} />
      </div>
    </div>
  );
}
