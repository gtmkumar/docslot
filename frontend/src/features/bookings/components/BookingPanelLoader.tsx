// Loader wrappers for the booking detail slide-overs (manage / approve /
// conversation). The list/queue rows carry only a booking id + masked phone; the
// panels need the full Booking (phone, age, gender, note, language). These
// wrappers fetch it via useBookingDetail (live: GET /bookings/{id}; mock: the
// prototype seam) so the panel opens for a REAL booking and stays URL-restorable
// across a refresh (the id lives in ?panel=&id=). While loading we render a
// focus-trapped SlideOver skeleton; on error we surface a retry via toUserError.
//
// Keeping the inner panels (ManageAppointmentPanel / ApproveCollectPanel /
// ConversationPanel) consuming a resolved `Booking` means their bodies are
// unchanged — only the data source moved from an inline BOOKINGS.find to a fetch.

import type { ComponentType } from 'react';
import { useTranslation } from 'react-i18next';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { Button } from '@/components/ui/Button';
import { toUserError } from '@/lib/backend';
import type { Booking } from '@/lib/types';
import { useBookingDetail } from '../api';
import { ManageAppointmentPanel } from './ManageAppointmentPanel';
import { ApproveCollectPanel } from './ApproveCollectPanel';
import { ConversationPanel } from './ConversationPanel';

interface InnerPanelProps {
  booking: Booking;
  open: boolean;
  onClose: () => void;
}

/** Shared loading/error shell so all three loaders share the same UX. */
function BookingPanelShell({
  bookingId,
  eyebrowKey,
  Inner,
  open,
  onClose,
}: {
  bookingId: string;
  eyebrowKey: string;
  Inner: ComponentType<InnerPanelProps>;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data, isLoading, isError, error, refetch } = useBookingDetail(bookingId);

  if (data) return <Inner booking={data} open={open} onClose={onClose} />;

  return (
    <SlideOver open={open} onClose={onClose} eyebrow={t(eyebrowKey)} title={t('common.loading')}>
      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          description={error ? toUserError(error) : t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-4" role="status" aria-busy="true">
          <Skeleton className="h-20 w-full" />
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-10 w-full" />
          <div className="flex justify-end">
            <Button variant="ghost" size="sm" onClick={onClose}>
              {t('common.cancel')}
            </Button>
          </div>
        </div>
      ) : null}
    </SlideOver>
  );
}

export function ManageAppointmentPanelLoader({
  bookingId,
  open,
  onClose,
}: {
  bookingId: string;
  open: boolean;
  onClose: () => void;
}) {
  return (
    <BookingPanelShell bookingId={bookingId} eyebrowKey="panel.manage" Inner={ManageAppointmentPanel} open={open} onClose={onClose} />
  );
}

export function ApproveCollectPanelLoader({
  bookingId,
  open,
  onClose,
}: {
  bookingId: string;
  open: boolean;
  onClose: () => void;
}) {
  return (
    <BookingPanelShell bookingId={bookingId} eyebrowKey="panel.approve" Inner={ApproveCollectPanel} open={open} onClose={onClose} />
  );
}

export function ConversationPanelLoader({
  bookingId,
  open,
  onClose,
}: {
  bookingId: string;
  open: boolean;
  onClose: () => void;
}) {
  return (
    <BookingPanelShell bookingId={bookingId} eyebrowKey="panel.conversation" Inner={ConversationPanel} open={open} onClose={onClose} />
  );
}
