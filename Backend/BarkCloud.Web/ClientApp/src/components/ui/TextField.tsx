import React from 'react';

interface FieldTextProps {
  label: React.ReactNode;
  hint?: React.ReactNode;
  error?: React.ReactNode;
  errorRole?: 'alert' | 'status';
  trailing?: React.ReactNode;
  containerClassName?: string;
  className?: string;
}

export type TextFieldProps = FieldTextProps & React.InputHTMLAttributes<HTMLInputElement>;

export const TextField = React.forwardRef<HTMLInputElement, TextFieldProps>(function TextField(
  { label, hint, error, errorRole, trailing, containerClassName, className, id, placeholder, 'aria-describedby': describedBy, 'aria-invalid': invalid, ...props },
  ref,
) {
  const generatedId = React.useId();
  const fieldId = id ?? generatedId;
  const hintId = `${fieldId}-hint`;
  const errorId = `${fieldId}-error`;
  const hasError = error !== undefined && error !== null && error !== false;
  const ids = [describedBy, hint ? hintId : null, hasError ? errorId : null].filter(Boolean).join(' ') || undefined;

  return (
    <div className={`md-text-field${trailing ? ' has-trailing' : ''}${containerClassName ? ` ${containerClassName}` : ''}`}>
      <div className="md-text-field__box">
        <fieldset className="md-text-field__outline" aria-hidden="true"><legend><span>{label}</span></legend></fieldset>
        <input
          {...props}
          ref={ref}
          id={fieldId}
          className={`md-text-field__control${className ? ` ${className}` : ''}`}
          placeholder={placeholder ?? ' '}
          aria-describedby={ids}
          aria-invalid={hasError ? true : invalid}
        />
        <label className="md-text-field__label" htmlFor={fieldId}>{label}</label>
        {trailing && <span className="md-text-field__trailing">{trailing}</span>}
      </div>
      {hint && <span className="md-text-field__hint" id={hintId}>{hint}</span>}
      {hasError && <span className="md-text-field__error" id={errorId} role={errorRole}>{error}</span>}
    </div>
  );
});

export type TextAreaFieldProps = FieldTextProps & React.TextareaHTMLAttributes<HTMLTextAreaElement>;

export const TextAreaField = React.forwardRef<HTMLTextAreaElement, TextAreaFieldProps>(function TextAreaField(
  { label, hint, error, errorRole, containerClassName, className, id, placeholder, 'aria-describedby': describedBy, 'aria-invalid': invalid, ...props },
  ref,
) {
  const generatedId = React.useId();
  const fieldId = id ?? generatedId;
  const hintId = `${fieldId}-hint`;
  const errorId = `${fieldId}-error`;
  const hasError = error !== undefined && error !== null && error !== false;
  const ids = [describedBy, hint ? hintId : null, hasError ? errorId : null].filter(Boolean).join(' ') || undefined;

  return (
    <div className={`md-text-field md-text-field--multiline${containerClassName ? ` ${containerClassName}` : ''}`}>
      <div className="md-text-field__box">
        <fieldset className="md-text-field__outline" aria-hidden="true"><legend><span>{label}</span></legend></fieldset>
        <textarea
          {...props}
          ref={ref}
          id={fieldId}
          className={`md-text-field__control${className ? ` ${className}` : ''}`}
          placeholder={placeholder ?? ' '}
          aria-describedby={ids}
          aria-invalid={hasError ? true : invalid}
        />
        <label className="md-text-field__label" htmlFor={fieldId}>{label}</label>
      </div>
      {hint && <span className="md-text-field__hint" id={hintId}>{hint}</span>}
      {hasError && <span className="md-text-field__error" id={errorId} role={errorRole}>{error}</span>}
    </div>
  );
});

export interface SearchFieldProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'> {
  label: string;
  leading?: React.ReactNode;
  trailing?: React.ReactNode;
  containerClassName?: string;
}

export const SearchField = React.forwardRef<HTMLInputElement, SearchFieldProps>(function SearchField(
  { label, leading, trailing, className, containerClassName, ...props },
  ref,
) {
  return (
    <div className={`md-search-field${containerClassName ? ` ${containerClassName}` : ''}`}>
      {leading && <span className="md-search-field__leading" aria-hidden="true">{leading}</span>}
      <input {...props} ref={ref} type="search" className={`md-search-field__control${className ? ` ${className}` : ''}`} aria-label={props['aria-label'] || label} />
      {trailing && <span className="md-search-field__trailing">{trailing}</span>}
    </div>
  );
});
