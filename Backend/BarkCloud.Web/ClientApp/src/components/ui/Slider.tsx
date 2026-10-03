import React from 'react';

interface SliderProps {
  value: number;
  min?: number;
  max?: number;
  step?: number;
  onChange: (value: number) => void;
  'aria-label': string;
  className?: string;
}

/** M3 Expressive slider поверх input[type=range]: заливка активной части считается
 *  из доли --f (0..1), вокруг ручки-планки остаётся зазор. */
export function Slider({ value, min = 0, max = 1, step, onChange, className, ...rest }: SliderProps) {
  const f = max > min ? (Math.min(max, Math.max(min, value)) - min) / (max - min) : 0;
  return (
    <input
      type="range"
      className={'m3-slider' + (className ? ' ' + className : '')}
      min={min}
      max={max}
      step={step ?? 'any'}
      value={value}
      onChange={(e) => onChange(Number(e.currentTarget.value))}
      style={{ '--f': f } as React.CSSProperties}
      aria-label={rest['aria-label']}
    />
  );
}
