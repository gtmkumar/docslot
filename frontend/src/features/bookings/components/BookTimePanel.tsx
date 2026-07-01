// Book time / Reserve a slot slide-over (image copy 9/10). Practitioner select →
// morning/afternoon IST slot grids → reserve. A functional shell: selecting a
// slot enables Confirm; confirm reserves via the payment-link-less path (toast).
//
// The practitioner list is sourced from the live practitioners query (real ids),
// NOT a hardcoded mock id — feeding a mock id ('d1') into useSlots hit the .NET
// {doctorId:guid} route as a 404 and the panel spun on the skeleton forever
// (#60). Both the practitioner and slot reads now surface isError with a retry.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { Select, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { istSlot } from '@/lib/format';
import { usePractitioners, useSlots } from '../api';
import type { Slot } from '@/lib/mock/contracts';

export function BookTimePanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  // No preselected doctor id: fall back to the first loaded practitioner so the
  // slots query always fires with a real id (never a mock 'd1' → 404).
  const [doctorId, setDoctorId] = useState<string | undefined>(undefined);
  const [slot, setSlot] = useState<string | null>(null);
  const {
    data: practitioners,
    isLoading: pLoading,
    isError: pError,
    refetch: pRefetch,
  } = usePractitioners();
  const activeDoctorId = doctorId ?? practitioners?.[0]?.id;
  const { data: slots, isLoading: sLoading, isError: sError, refetch: sRefetch } = useSlots(activeDoctorId);

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
          {pError ? (
            <EmptyState
              title={t('error.genericTitle')}
              description={t('error.genericBody')}
              actionLabel={t('common.retry')}
              onAction={() => void pRefetch()}
            />
          ) : pLoading || !practitioners ? (
            <Skeleton className="h-10 w-full" />
          ) : practitioners.length === 0 ? (
            <p className="text-[12px] text-muted">{t('bookTime.noPractitioners')}</p>
          ) : (
            <Select
              id="bt-doctor"
              value={activeDoctorId ?? ''}
              onChange={(e) => {
                setDoctorId(e.target.value);
                setSlot(null);
              }}
            >
              {practitioners.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name} · {d.spec}
                </option>
              ))}
            </Select>
          )}
        </div>

        {!activeDoctorId ? null : sError ? (
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void sRefetch()}
          />
        ) : sLoading || !slots ? (
          <div className="grid grid-cols-4 gap-2" role="status" aria-busy="true">
            {Array.from({ length: 12 }).map((_, i) => (
              <Skeleton key={i} className="h-12 w-full" />
            ))}
          </div>
        ) : slots.length === 0 ? (
          <p className="text-[12px] text-muted">{t('bookTime.noSlots')}</p>
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
