// Form field primitives shared by the slide-over forms. Token-driven styles,
// accessible label association, and an error slot wired for react-hook-form /
// zod messages. Zero hex.

import type { InputHTMLAttributes, ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from 'react';

export const fieldClass =
  'w-full rounded-[var(--radius-sm)] border border-line bg-surface px-3 py-2 text-sm text-ink ' +
  'placeholder:text-muted-2 outline-none transition-colors focus:border-primary focus:ring-2 focus:ring-primary-soft ' +
  'disabled:opacity-50 aria-[invalid=true]:border-danger aria-[invalid=true]:ring-danger-soft';

export const labelClass = 'mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2';

export function FieldShell({
  label,
  htmlFor,
  optional,
  error,
  children,
}: {
  label: string;
  htmlFor?: string;
  optional?: string;
  error?: string;
  children: ReactNode;
}) {
  return (
    <div>
      <label htmlFor={htmlFor} className={labelClass}>
        {label}
        {optional ? (
          <span className="font-normal lowercase tracking-normal text-muted-2"> ({optional})</span>
        ) : null}
      </label>
      {children}
      {error ? (
        <p role="alert" className="mt-1 text-[12px] text-danger">
          {error}
        </p>
      ) : null}
    </div>
  );
}

export function TextInput(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={`${fieldClass} ${props.className ?? ''}`} />;
}

export function TextArea(props: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea {...props} className={`${fieldClass} resize-none ${props.className ?? ''}`} />;
}

export function Select(props: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select {...props} className={`${fieldClass} ${props.className ?? ''}`} />;
}
