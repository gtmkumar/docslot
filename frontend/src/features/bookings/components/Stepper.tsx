// Stepper header for the Create-appointment slide-over (Patient → Slot → Confirm).
// Pure presentation; the active index is owned by the panel body. Color comes
// from tokens — active = teal, done = teal-soft, upcoming = muted.

import { Check } from 'lucide-react';

interface Step {
  label: string;
}

export function Stepper({ steps, active }: { steps: Step[]; active: number }) {
  return (
    <ol className="flex items-center gap-2">
      {steps.map((step, i) => {
        const isDone = i < active;
        const isActive = i === active;
        return (
          <li key={step.label} className="flex flex-1 items-center gap-2">
            <span
              className={[
                'flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-[12px] font-semibold',
                isActive
                  ? 'bg-primary text-bg'
                  : isDone
                    ? 'bg-primary-soft text-primary'
                    : 'bg-surface-sunk text-muted-2',
              ].join(' ')}
            >
              {isDone ? <Check size={13} aria-hidden="true" /> : i + 1}
            </span>
            <span
              className={[
                'truncate text-[13px]',
                isActive ? 'font-medium text-ink' : 'text-muted',
              ].join(' ')}
            >
              {step.label}
            </span>
            {i < steps.length - 1 ? <span className="h-px flex-1 bg-line" aria-hidden="true" /> : null}
          </li>
        );
      })}
    </ol>
  );
}
