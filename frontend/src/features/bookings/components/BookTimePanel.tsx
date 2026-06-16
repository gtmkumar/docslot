// Book time / Reserve a slot slide-over (image copy 9/10). Practitioner select →
// morning/afternoon IST slot grids → reserve. A functional shell: selecting a
// slot enables Confirm; confirm reserves via the payment-link-less path (toast).

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { Select, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { istSlot } from '@/lib/format';
import { DOCTORS } from '@/lib/data';
import { useSlots } from '../api';
import type { Slot } from '@/lib/mock/contracts';

export function BookTimePanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const [doctorId, setDoctorId] = useState(DOCTORS[0].id);
  const [slot, setSlot] = useState<string | null>(null);
  const { data: slots, isLoading } = useSlots(doctorId);

  const isAfternoon = (time: string) => Number(time.split(':')[0]) >= 13;
  const morning = (slots ?? []).filter((s) => !isAfternoon(s.time));
  const afternoon = (slots ?? []).filter((s) => isAfternoon(s.time));

  const onConfirm = () => {
    if (!slot) return;
    toast.success(t('bookTime.reserved'));
    onClose();
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('bookTime.eyebrow')}
      title={t('bookTime.title')}
      footer={
        <>
          <Button variant="ghost" size="md" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" disabled={!slot} onClick={onConfirm}>
            {t('bookTime.confirmSlot')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        <div>
          <label htmlFor="bt-doctor" className={labelClass}>
            {t('bookTime.practitioner')}
          </label>
          <Select
            id="bt-doctor"
            value={doctorId}
            onChange={(e) => {
              setDoctorId(e.target.value);
              setSlot(null);
            }}
          >
            {DOCTORS.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name} · {d.spec}
              </option>
            ))}
          </Select>
        </div>

        {isLoading || !slots ? (
          <div className="grid grid-cols-4 gap-2" role="status" aria-busy="true">
            {Array.from({ length: 12 }).map((_, i) => (
              <Skeleton key={i} className="h-12 w-full" />
            ))}
          </div>
        ) : (
          <>
            <SlotSection title={t('bookTime.morning')} slots={morning} selected={slot} onSelect={setSlot} />
            <SlotSection title={t('bookTime.afternoon')} slots={afternoon} selected={slot} onSelect={setSlot} />
          </>
        )}

        {slot ? (
          <p className="mono rounded-[var(--radius-sm)] bg-primary-soft px-3 py-2 text-[13px] text-primary">
            {t('bookTime.reserving')} · {istSlot(slot)}
          </p>
        ) : (
          <p className="text-[12px] text-muted">{t('bookTime.pickHint')}</p>
        )}
      </div>
    </SlideOver>
  );
}

function SlotSection({
  title,
  slots,
  selected,
  onSelect,
}: {
  title: string;
  slots: Slot[];
  selected: string | null;
  onSelect: (s: string) => void;
}) {
  if (slots.length === 0) return null;
  return (
    <section>
      <span className={labelClass}>{title}</span>
      <div className="grid grid-cols-4 gap-2">
        {slots.map((s) => {
          const disabled = s.state === 'full' || s.state === 'blocked';
          const active = selected === s.time;
          return (
            <button
              key={s.time}
              type="button"
              disabled={disabled}
              onClick={() => onSelect(s.time)}
              aria-pressed={active}
              className={[
                'mono rounded-[var(--radius-sm)] border px-1 py-2 text-[12px] transition-colors',
                active
                  ? 'border-primary bg-primary text-bg'
                  : disabled
                    ? 'cursor-not-allowed border-line bg-surface-sunk text-muted-2 line-through'
                    : 'border-line bg-surface text-ink hover:bg-surface-sunk',
              ].join(' ')}
            >
              {s.time}
            </button>
          );
        })}
      </div>
    </section>
  );
}
