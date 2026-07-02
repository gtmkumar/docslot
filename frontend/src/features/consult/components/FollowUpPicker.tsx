// Follow-up quick chips → followUpInDays. Single-select; clicking the active chip
// clears it. Sentinels: null = not set, 0 = SOS only, >0 = after N days.

import { useTranslation } from 'react-i18next';
import { FOLLOW_UPS } from '../constants';

export function FollowUpPicker({
  value,
  onChange,
  disabled = false,
}: {
  value: number | null;
  onChange: (next: number | null) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  return (
    <ul className="flex flex-wrap gap-1.5">
      {FOLLOW_UPS.map((f) => {
        const active = value === f.days;
        return (
          <li key={f.key}>
            <button
              type="button"
              aria-pressed={active}
              disabled={disabled}
              onClick={() => onChange(active ? null : f.days)}
              className={`inline-flex h-8 items-center rounded-full border px-3 text-[12px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:opacity-50 ${
                active ? 'border-primary bg-primary-soft text-primary' : 'border-line bg-surface text-muted hover:text-ink'
              }`}
            >
              {t(f.labelKey)}
            </button>
          </li>
        );
      })}
    </ul>
  );
}
