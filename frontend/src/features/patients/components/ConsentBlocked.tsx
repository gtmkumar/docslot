// Consent-denied affordance + 403 detection (Phase-3 slice 4).
//
// A clinical read blocked by consent surfaces here instead of data: a clear "no
// active consent" message and — gated on docslot.medical_access.break_glass — a
// "Break glass (emergency access)" button that opens the contextual break-glass
// slide-over. After a successful grant the gated read re-fetches (the break-glass
// mutation invalidates the patient's clinical namespace), so the caller also gets
// a retry affordance.
//
// Detection is mode-agnostic: real mode throws ApiError(403) on a consent denial;
// mock mode throws ConsentRequiredError. Either is treated as "consent denied".

import { useTranslation } from 'react-i18next';
import { KeyRound, ShieldX } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { ApiError } from '@/lib/api-client';
import { ConsentRequiredError } from '@/lib/mock';
import { usePermissions } from '@/lib/permissions';
import { useUI, type Panel } from '@/stores/ui';
import type { BreakGlassResourceType } from '@/lib/mock/contracts';

/** True when a query error is a consent denial (real 403 OR mock ConsentRequired). */
export function isConsentDenied(error: unknown): boolean {
  if (error instanceof ConsentRequiredError) return true;
  if (error instanceof ApiError) return error.status === 403;
  return false;
}

export function ConsentBlocked({
  patientId,
  resourceType,
  resourceId,
  reopen,
  onRetry,
  inPanel,
}: {
  patientId: string;
  resourceType: BreakGlassResourceType;
  resourceId: string | null;
  /** The gated detail panel to restore after a grant (so the read re-runs in
   *  place). Omitted when this renders at screen level (e.g. the history list). */
  reopen?: Panel;
  /** Re-run the gated read after a grant (the mutation already invalidated it). */
  onRetry?: () => void;
  /** Render without the Card wrapper when already inside a slide-over body. */
  inPanel?: boolean;
}) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);

  const body = (
    <div className="flex flex-col items-center gap-3 py-2 text-center">
      <span
        aria-hidden="true"
        className="flex h-12 w-12 items-center justify-center rounded-full bg-danger-soft text-danger"
      >
        <ShieldX size={24} />
      </span>
      <p className="text-sm font-medium text-ink">{t('clinical.consentBlocked.title')}</p>
      <p className="max-w-sm text-[13px] text-muted">{t('clinical.consentBlocked.body')}</p>
      <div className="mt-1 flex flex-wrap items-center justify-center gap-2">
        {onRetry ? (
          <Button variant="ghost" size="sm" onClick={onRetry}>
            {t('common.retry')}
          </Button>
        ) : null}
        {can('docslot.medical_access.break_glass') ? (
          <Button
            variant="danger"
            size="sm"
            onClick={() => openPanel({ type: 'clinicalBreakGlass', patientId, resourceType, resourceId, reopen })}
          >
            <KeyRound size={14} aria-hidden="true" />
            {t('clinical.consentBlocked.breakGlass')}
          </Button>
        ) : null}
      </div>
    </div>
  );

  return inPanel ? body : <Card className="p-6">{body}</Card>;
}
