// Conversation slide-over (image copy 3): WhatsApp-mirrored thread (pattern 2).
// Patient bubbles left (surface), bot/tenant bubbles right (whatsapp tint),
// interactive option chips, system lines centered. Footer: Reassign / Reject /
// Approve & notify (approve is optimistic + closes).
//
// A header strip shows Doctor / Time (IST) / Status to match the prototype.

import { Check, UserCog, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { StatusPill } from '@/components/ui/StatusPill';
import { istSlot } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import type { Booking } from '@/lib/types';
import { useConversation } from '../../conversations/api';
import { useApproveBooking, useCancelBooking } from '../api';

const deva = (s: string) => (s.match(/[ऀ-ॿ]/) ? 'deva' : '');

export function ConversationPanel({
  booking,
  open,
  onClose,
}: {
  booking: Booking;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data: messages, isLoading, isError, refetch } = useConversation(booking.id);
  const approve = useApproveBooking();
  const cancel = useCancelBooking();

  // Await the server before success + close so a rejection surfaces, not a false success (#55).
  const onApprove = async () => {
    try {
      await approve.mutateAsync({ bookingId: booking.id, idempotencyKey: idempotencyKey() });
      toast.success(`${booking.patient} · ${t('status.confirmed')}`);
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
  };
  const onReject = async () => {
    try {
      await cancel.mutateAsync({ bookingId: booking.id, reason: 'rejected_from_conversation', idempotencyKey: idempotencyKey() });
      toast(`${booking.patient} · ${t('status.cancelled')}`);
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('panel.conversation')}
      title={`${booking.patient} · #${booking.token}`}
      headerExtra={
        <dl className="grid grid-cols-3 gap-2 text-[12px]">
          <div>
            <dt className="text-[10px] uppercase tracking-wider text-muted-2">{t('conversation.doctor')}</dt>
            <dd className="text-ink">{booking.doctorName}</dd>
          </div>
          <div>
            <dt className="text-[10px] uppercase tracking-wider text-muted-2">{t('conversation.time')}</dt>
            <dd className="mono text-ink">{istSlot(booking.time)}</dd>
          </div>
          <div>
            <dt className="text-[10px] uppercase tracking-wider text-muted-2">{t('conversation.status')}</dt>
            <dd>
              <StatusPill status={booking.status} />
            </dd>
          </div>
        </dl>
      }
      footer={
        <div className="grid w-full grid-cols-3 gap-2">
          <Button variant="ghost" size="md" onClick={onClose}>
            <UserCog size={15} aria-hidden="true" />
            {t('conversation.reassign')}
          </Button>
          <Button variant="danger" size="md" onClick={onReject} disabled={cancel.isPending}>
            <X size={15} aria-hidden="true" />
            {t('conversation.reject')}
          </Button>
          <Button variant="primary" size="md" onClick={onApprove} disabled={approve.isPending}>
            <Check size={15} aria-hidden="true" />
            {t('conversation.approveNotify')}
          </Button>
        </div>
      }
    >
      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !messages ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-12 w-2/3" />
          <Skeleton className="ml-auto h-12 w-3/4" />
          <Skeleton className="h-10 w-1/2" />
          <Skeleton className="ml-auto h-16 w-3/4" />
        </div>
      ) : messages.length === 0 ? (
        <EmptyState title={t('conversation.empty')} />
      ) : (
        <ol className="flex flex-col gap-2.5">
          {messages.map((m, i) => {
            if (m.system) {
              return (
                <li key={i} className="my-1 text-center">
                  <span className="inline-block rounded-full bg-surface-sunk px-3 py-1 text-[11px] text-muted">
                    {m.text}
                  </span>
                </li>
              );
            }
            const isPatient = m.from === 'patient';
            return (
              <li key={i} className={`flex flex-col ${isPatient ? 'items-start' : 'items-end'}`}>
                <div
                  className={[
                    'max-w-[80%] whitespace-pre-line rounded-[var(--radius)] px-3 py-2 text-[13px]',
                    isPatient ? 'bg-surface text-ink border border-line' : 'bg-whatsapp-soft text-whatsapp-ink',
                    deva(m.text),
                  ].join(' ')}
                >
                  {m.text}
                </div>
                {m.interactive ? (
                  <div className={`mt-1 flex max-w-[80%] flex-wrap gap-1.5 ${isPatient ? '' : 'justify-end'}`}>
                    {m.interactive.map((opt) => (
                      <span
                        key={opt}
                        className={`rounded-full border border-line bg-surface px-2.5 py-1 text-[12px] text-ink ${deva(opt)}`}
                      >
                        {opt}
                      </span>
                    ))}
                  </div>
                ) : null}
                <span className="mono mt-0.5 text-[10px] text-muted-2">{m.at}</span>
              </li>
            );
          })}
        </ol>
      )}
    </SlideOver>
  );
}
