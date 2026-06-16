// Approve & collect slide-over (image copy 4). Payment method (UPI link / Cash /
// Skip) → amount (presets + custom) → link expiry → live WhatsApp message
// preview with the UPI link → Send link ₹{amount}.
//
// The send goes through useSendPaymentLink which (via the api-client seam)
// attaches an Idempotency-Key — money-moving POSTs must never double-fire on
// retry. Cash/Skip approve the booking directly (no link).

import { useActionState, useState } from 'react';
import { Banknote, Link2, SkipForward } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { TextInput } from '@/components/ui/Field';
import { inr, istSlot } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import type { Booking } from '@/lib/types';
import { useApproveBooking, useSendPaymentLink } from '../api';
import { PatientChip } from './PatientChip';

type Method = 'upi' | 'cash' | 'skip';
type Expiry = 30 | 120 | 0; // 0 = at appointment
const PRESETS = [500, 700, 900, 1200];

export function ApproveCollectPanel({
  booking,
  open,
  onClose,
}: {
  booking: Booking;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const sendLink = useSendPaymentLink();
  const approve = useApproveBooking();

  const [method, setMethod] = useState<Method>('upi');
  const [amount, setAmount] = useState<number>(900);
  const [expiry, setExpiry] = useState<Expiry>(30);

  // useActionState wraps the submit so the footer button reflects pending state.
  // The Idempotency-Key is generated ONCE per submit invocation (action start),
  // so a retried send maps to the same key — the server never double-charges.
  const [, submit, isPending] = useActionState(async () => {
    const key = idempotencyKey();
    if (method === 'upi') {
      await sendLink.mutateAsync({ bookingId: booking.id, amount, expiresInMins: expiry, idempotencyKey: key });
      toast.success(t('approve.sendLink', { amount: inr(amount) }));
    } else {
      approve.mutate({ bookingId: booking.id, idempotencyKey: key });
      toast.success(`${booking.patient} · ${t('status.confirmed')}`);
    }
    onClose();
    return null;
  }, null);

  const formId = 'approve-collect-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('panel.approve')}
      title={`${booking.patient} · ${booking.id}`}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={isPending}>
            {method === 'upi' ? t('approve.sendLink', { amount: inr(amount) }) : t('manage.approveShort')}
          </Button>
        </>
      }
    >
      <form id={formId} action={submit} className="flex flex-col gap-5">
        <PatientChip booking={booking} slotTime={istSlot(booking.time)} />

        {/* Payment method */}
        <section>
          <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('approve.paymentMethod')}
          </h3>
          <div role="radiogroup" aria-label={t('approve.paymentMethod')} className="grid grid-cols-3 gap-2">
            <MethodOption
              checked={method === 'upi'}
              onSelect={() => setMethod('upi')}
              icon={<Link2 size={16} aria-hidden="true" />}
              title={t('approve.upiLink')}
              hint={t('approve.upiHint')}
            />
            <MethodOption
              checked={method === 'cash'}
              onSelect={() => setMethod('cash')}
              icon={<Banknote size={16} aria-hidden="true" />}
              title={t('approve.cash')}
              hint={t('approve.cashHint')}
            />
            <MethodOption
              checked={method === 'skip'}
              onSelect={() => setMethod('skip')}
              icon={<SkipForward size={16} aria-hidden="true" />}
              title={t('approve.skip')}
              hint={t('approve.skipHint')}
            />
          </div>
        </section>

        {method === 'upi' ? (
          <>
            {/* Amount */}
            <section>
              <div className="mb-2 flex items-center justify-between">
                <h3 className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">
                  {t('approve.amount')}
                </h3>
                <span className="text-[11px] text-muted-2">{t('approve.defaultConsultFee')}</span>
              </div>
              <div className="flex flex-wrap gap-2">
                {PRESETS.map((p) => (
                  <button
                    key={p}
                    type="button"
                    onClick={() => setAmount(p)}
                    aria-pressed={amount === p}
                    className={[
                      'mono rounded-[var(--radius-sm)] border px-3 py-1.5 text-[13px] transition-colors',
                      amount === p
                        ? 'border-primary bg-primary text-bg'
                        : 'border-line bg-surface text-ink hover:bg-surface-sunk',
                    ].join(' ')}
                  >
                    {inr(p)}
                  </button>
                ))}
                <TextInput
                  type="number"
                  inputMode="numeric"
                  aria-label={t('approve.custom')}
                  value={amount}
                  min={0}
                  onChange={(e) => setAmount(Number(e.target.value) || 0)}
                  className="mono w-24"
                />
              </div>
            </section>

            {/* Expiry */}
            <section>
              <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
                {t('approve.linkExpires')}
              </h3>
              <div role="radiogroup" aria-label={t('approve.linkExpires')} className="grid grid-cols-3 gap-2">
                <ExpiryOption checked={expiry === 30} onSelect={() => setExpiry(30)} label={t('approve.expiry30')} />
                <ExpiryOption checked={expiry === 120} onSelect={() => setExpiry(120)} label={t('approve.expiry120')} />
                <ExpiryOption checked={expiry === 0} onSelect={() => setExpiry(0)} label={t('approve.expiryAppt')} />
              </div>
            </section>

            {/* WhatsApp preview */}
            <section>
              <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
                {t('approve.messagePreview')}
              </h3>
              <div className="flex flex-col gap-2 rounded-[var(--radius)] bg-whatsapp-soft p-3 text-[13px] text-whatsapp-ink">
                <p>{t('approve.previewBody', { name: booking.patient, doctor: booking.doctorName, time: istSlot(booking.time) })}</p>
                <p className="font-medium">{t('approve.previewPay', { amount: inr(amount) })}</p>
                <code className="mono block overflow-x-auto rounded bg-surface px-2 py-1.5 text-[11px] text-ink">
                  upi://pay?pa=apollocare@hdfcbank&am={amount}&tn={booking.id}
                </code>
                <p className="text-[11px] opacity-80">{t('approve.previewFooter')}</p>
              </div>
            </section>
          </>
        ) : null}

        <p className="text-[12px] text-muted">{t('approve.autoConfirm')}</p>
      </form>
    </SlideOver>
  );
}

function MethodOption({
  checked,
  onSelect,
  icon,
  title,
  hint,
}: {
  checked: boolean;
  onSelect: () => void;
  icon: React.ReactNode;
  title: string;
  hint: string;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={checked}
      onClick={onSelect}
      className={[
        'flex flex-col items-center gap-1 rounded-[var(--radius-sm)] border px-2 py-3 text-center transition-colors',
        checked ? 'border-primary bg-primary-soft text-primary' : 'border-line bg-surface text-ink hover:bg-surface-sunk',
      ].join(' ')}
    >
      {icon}
      <span className="text-[13px] font-medium">{title}</span>
      <span className="text-[10px] text-muted">{hint}</span>
    </button>
  );
}

function ExpiryOption({ checked, onSelect, label }: { checked: boolean; onSelect: () => void; label: string }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={checked}
      onClick={onSelect}
      className={[
        'rounded-[var(--radius-sm)] border px-2 py-2 text-[13px] transition-colors',
        checked ? 'border-primary bg-primary text-bg' : 'border-line bg-surface text-ink hover:bg-surface-sunk',
      ].join(' ')}
    >
      {label}
    </button>
  );
}
