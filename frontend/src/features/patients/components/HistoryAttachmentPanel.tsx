// Scanned paper-Rx viewer. Fetches the record's attachment authenticated (Bearer
// + tenant + the declared purpose-of-use) into an object/data URL and shows it in
// the slide-over. The bytes are PHI: the query is not cached (gcTime 0) and the
// object URL is REVOKED on unmount so nothing lingers. NOT URL-addressable.

import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { useMedicalHistoryAttachment } from '../api';
import type { PurposeOfUse } from '@/lib/mock/contracts';

export function HistoryAttachmentPanel({
  patientId,
  historyId,
  purpose,
  fileName,
  open,
  onClose,
}: {
  patientId: string;
  historyId: string;
  purpose: PurposeOfUse;
  fileName: string;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data: url, isLoading, isError, refetch } = useMedicalHistoryAttachment(patientId, historyId, purpose);

  // Revoke the object URL when it changes / the viewer unmounts. Revoking a data:
  // URL (mock) is a harmless no-op; revoking a blob object URL (real) frees the
  // PHI bytes so they don't linger in memory.
  useEffect(() => {
    if (!url) return;
    return () => URL.revokeObjectURL(url);
  }, [url]);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={fileName || t('clinical.history.attachment.title')}
      description={t('clinical.history.attachment.title')}
    >
      {isError ? (
        <EmptyState
          title={t('clinical.history.attachment.error')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !url ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-72 w-full rounded-[var(--radius)]" />
        </div>
      ) : (
        <div className="flex flex-col gap-3">
          {/* select-none: the scan is PHI, discourage casual copy/drag. */}
          <img
            src={url}
            alt={t('clinical.history.attachment.imageAlt')}
            className="w-full select-none rounded-[var(--radius)] border border-line bg-surface-sunk object-contain"
          />
        </div>
      )}
    </SlideOver>
  );
}
