import { forwardRef, useEffect, useState } from 'react';

interface OtpInputProps {
  value: string;
  disabled: boolean;
  error?: string;
  onChange: (value: string) => void;
  onInvalid: (message?: string) => void;
}

function normalizeOtp(raw: string): string | undefined {
  if (/[^0-9\s-]/.test(raw)) return undefined;
  const digits = raw.replace(/[\s-]/g, '');
  if (digits.length > 6) return undefined;
  return digits;
}

function normalizedOffset(raw: string, offset: number): number {
  return (normalizeOtp(raw.slice(0, offset)) ?? '').length;
}

function resetInputScroll(input: HTMLInputElement) {
  input.scrollLeft = 0;
}

function restoreSelection(input: HTMLInputElement, start: number, end: number) {
  const apply = () => {
    input.setSelectionRange(start, end);
    resetInputScroll(input);
  };
  resetInputScroll(input);
  if (typeof requestAnimationFrame === 'function') requestAnimationFrame(apply);
  else window.setTimeout(apply, 0);
}

export const OtpInput = forwardRef<HTMLInputElement, OtpInputProps>(function OtpInput(
  { value, disabled, error, onChange, onInvalid },
  forwardedRef,
) {
  const [selection, setSelection] = useState({ start: 0, end: 0 });

  useEffect(() => {
    if (value === '') setSelection({ start: 0, end: 0 });
  }, [value]);

  function updateSelection(input: HTMLInputElement) {
    setSelection({ start: input.selectionStart ?? value.length, end: input.selectionEnd ?? value.length });
    resetInputScroll(input);
  }

  function handleChange(input: HTMLInputElement) {
    const raw = input.value;
    const normalized = normalizeOtp(raw);
    if (normalized === undefined) {
      input.value = value;
      const start = Math.min(selection.start, value.length);
      const end = Math.min(selection.end, value.length);
      restoreSelection(input, start, end);
      onInvalid('Введите только цифры. Можно вставить код с пробелом или дефисом.');
      return;
    }

    const rawStart = input.selectionStart ?? raw.length;
    const rawEnd = input.selectionEnd ?? rawStart;
    const nextSelection = {
      start: normalizedOffset(raw, rawStart),
      end: normalizedOffset(raw, rawEnd),
    };
    onChange(normalized);
    onInvalid(undefined);
    setSelection(nextSelection);
    restoreSelection(input, nextSelection.start, nextSelection.end);
  }

  const cells = Array.from({ length: 6 }, (_, index) => {
    const selected = selection.start !== selection.end && index >= selection.start && index < selection.end;
    const caret = selection.start === selection.end && index === selection.start;
    const caretAfter = selection.start === 6 && selection.end === 6 && index === 5;
    return (
      <span className={'auth-otp-cell' + (selected ? ' is-selected' : '')} key={index}>
        {value[index] ?? ''}
        {(caret || caretAfter) && <span className={'auth-otp-caret' + (caretAfter ? ' auth-otp-caret-after' : '')} />}
      </span>
    );
  });

  return (
    <div className={'auth-otp' + (error ? ' has-error' : '')}>
      <div className="auth-otp-cells" aria-hidden="true">{cells}</div>
      <input
        ref={forwardedRef}
        id="auth-otp"
        type="text"
        inputMode="numeric"
        autoComplete="one-time-code"
        name="otp"
        aria-label="Шестизначный код"
        aria-invalid={error ? 'true' : undefined}
        aria-describedby={error ? 'auth-otp-error' : undefined}
        value={value}
        disabled={disabled}
        spellCheck={false}
        onChange={(event) => handleChange(event.currentTarget)}
        onPointerDown={(event) => resetInputScroll(event.currentTarget)}
        onFocus={(event) => resetInputScroll(event.currentTarget)}
        onSelect={(event) => updateSelection(event.currentTarget)}
        onClick={(event) => updateSelection(event.currentTarget)}
        onKeyUp={(event) => updateSelection(event.currentTarget)}
      />
    </div>
  );
});
