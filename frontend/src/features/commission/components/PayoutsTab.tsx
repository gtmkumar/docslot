// Payouts — batch settlement with the gross → TDS 5% (194H) → GST → net
// breakdown. CRITICAL: approve and execute are TWO DISTINCT, independently-gated
// actions (often different users):
//   - 'pending'  → Approve (commission.payouts.approve)
//   - 'approved' → shows "awaiting execution"; Execute (commission.payouts.execute)
// The UI never collapses them into one button, and gates each on its own key.
// Money actions carry a stable Idempotency-Key.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ExternalLink, FileText } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { inr, shortDate } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { openForm16ADocument } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import { useApprovePayout, useExecutePayout, useIssueForm16A, usePayouts } from '../api';
import { CommissionBadge } from './CommissionBadge';
import type { Form16ACertificate, Payout } from '@/lib/mock/contracts';

export function PayoutsTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = usePayouts();

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[13px] text-muted">{t('commission.payouts.sub')}</p>
      {isError ? (
        <Card>
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        </Card>
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-2" role="status" aria-busy="true">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-24 w-full" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <Card>
          <EmptyState title={t('commission.payouts.empty')} />
        </Card>
      ) : (
        <ul className="flex flex-col gap-3">
          {data.map((p) => (
            <PayoutCard key={p.payoutId} payout={p} />
          ))}
        </ul>
      )}
    </div>
  );
}

function PayoutCard({ payout }: { payout: Payout }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const approve = useApprovePayout();
  const execute = useExecutePayout();
  const issueTds = useIssueForm16A();
  const [open, setOpen] = useState(false);
  // The issued certificate carries PAN LAST 4 only (safe to hold/show). The FULL
  // PAN lives solely on the rendered document opened via openForm16ADocument — it
  // is never placed in this (or any) state, cached, or logged.
  const [cert, setCert] = useState<Form16ACertificate | null>(null);
  const [opening, setOpening] = useState(false);

  const onApprove = () =>
    approve.mutate({ payoutId: payout.payoutId, idempotencyKey: idempotencyKey() }, { onSuccess: () => toast.success(t('commission.payouts.approved')) });
  const onExecute = () =>
    execute.mutate({ payoutId: payout.payoutId, idempotencyKey: idempotencyKey() }, { onSuccess: () => toast.success(t('commission.payouts.executed')) });

  const onIssueTds = () =>
    issueTds.mutate(
      { payoutId: payout.payoutId, idempotencyKey: idempotencyKey() },
      {
        onSuccess: (result) => {
          setCert(result);
          toast.success(t('commission.payouts.form16a.issued'));
        },
        onError: () => toast.error(t('error.genericTitle')),
      },
    );

  const onViewCert = async () => {
    setOpening(true);
    try {
      // Opens the document (full PAN) in a NEW TAB via a transient blob — never
      // logged/cached/in app state. The server logs the access on its side.
      await openForm16ADocument(payout.payoutId);
    } catch {
      toast.error(t('commission.payouts.form16a.openFailed'));
    } finally {
      setOpening(false);
    }
  };

  const statusInfo = STATUS[payout.status];

  return (
    <li>
      <Card className="p-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="min-w-0">
            <p className="text-sm font-medium text-ink">{payout.brokerName}</p>
            <p className="text-[11px] text-muted">
              {shortDate(payout.periodStart)} – {shortDate(payout.periodEnd)} · {t('commission.payouts.attributions', { count: payout.attributionCount })}
            </p>
          </div>
          <div className="flex items-center gap-3">
            <div className="text-right">
              <p className="mono text-base font-semibold text-ink">{inr(payout.netAmountInr)}</p>
              <p className="text-[10px] uppercase tracking-wider text-muted-2">{t('commission.payouts.net')}</p>
            </div>
            <CommissionBadge tone={statusInfo.tone} label={t(`commission.payouts.${statusInfo.labelKey}`)} />
          </div>
        </div>

        {/* Tax breakdown (expand). */}
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          className="mt-2 inline-flex items-center gap-1 text-[12px] text-primary hover:underline focus-visible:outline-none"
        >
          <ChevronDown size={13} className={open ? 'rotate-180 transition-transform' : 'transition-transform'} aria-hidden="true" />
          {t('commission.payouts.gross')} → {t('commission.payouts.net')}
        </button>
        {open ? (
          <dl className="mt-2 grid grid-cols-2 gap-x-4 gap-y-1.5 rounded-[var(--radius-sm)] border border-line p-3 text-[12px] sm:grid-cols-4">
            <Money label={t('commission.payouts.gross')} value={inr(payout.grossAmountInr)} />
            <Money label={`${t('commission.payouts.tds')}`} value={`− ${inr(payout.tdsAmountInr)}`} />
            <Money label={t('commission.payouts.gst')} value={payout.gstRate != null ? `+ ${inr(payout.gstAmountInr)}` : '—'} />
            <Money label={t('commission.payouts.net')} value={inr(payout.netAmountInr)} strong />
          </dl>
        ) : null}

        {/* TWO-STEP actions — approve and execute are distinct + independently gated. */}
        <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-line pt-3">
          {payout.status === 'pending' && can('commission.payouts.approve') ? (
            <Button variant="primary" size="sm" onClick={onApprove} disabled={approve.isPending}>
              {t('commission.payouts.approveStep')}
            </Button>
          ) : null}

          {payout.status === 'approved' ? (
            <>
              <span className="text-[12px] text-muted">{t('commission.payouts.awaitingExecution')}</span>
              {can('commission.payouts.execute') ? (
                <Button variant="primary" size="sm" onClick={onExecute} disabled={execute.isPending}>
                  {t('commission.payouts.executeStep')}
                </Button>
              ) : null}
            </>
          ) : null}

          {/* Form 16A — only for a PAID payout, gated on commission.tds.issue.
              Issue mints the certificate; View opens the document (full PAN) in a
              new tab. The certificate is PROVISIONAL until filed on TRACES. */}
          {payout.status === 'paid' && can('commission.tds.issue') ? (
            cert ? (
              <Button variant="ghost" size="sm" onClick={() => void onViewCert()} disabled={opening}>
                <ExternalLink size={14} aria-hidden="true" />
                {t('commission.payouts.form16a.view')}
              </Button>
            ) : (
              <Button variant="ghost" size="sm" onClick={onIssueTds} disabled={issueTds.isPending}>
                <FileText size={14} aria-hidden="true" />
                {t('commission.payouts.form16a.issue')}
              </Button>
            )
          ) : null}

          {payout.paymentReference ? (
            <span className="mono ml-auto text-[11px] text-muted-2">{t('commission.payouts.ref', { ref: payout.paymentReference })}</span>
          ) : null}
        </div>

        {/* Issued certificate summary — invoice number + the PROVISIONAL-until-TRACES
            note. PAN shown as last 4 only (never the full PAN). */}
        {cert ? (
          <div className="mt-2 flex flex-col gap-1 rounded-[var(--radius-sm)] border border-line bg-bg-2 p-3 text-[11px]">
            <div className="flex flex-wrap items-center gap-2">
              <CommissionBadge tone="pending" label={t('commission.payouts.form16a.provisional')} />
              {cert.invoiceNumber ? (
                <span className="mono text-muted">{t('commission.payouts.form16a.invoice', { ref: cert.invoiceNumber })}</span>
              ) : null}
            </div>
            <p className="text-muted-2">
              {t('commission.payouts.form16a.tds', { section: cert.section, rate: cert.tdsRate, amount: inr(cert.tdsAmountInr) })}
              {cert.deducteePanLast4 ? ` · ${t('commission.payouts.form16a.panLast4', { last4: cert.deducteePanLast4 })}` : ''}
            </p>
            <p className="text-muted-2">{t('commission.payouts.form16a.provisionalNote')}</p>
          </div>
        ) : null}

        {payout.status === 'pending' || payout.status === 'approved' ? (
          <p className="mt-2 text-[11px] text-muted-2">{t('commission.payouts.twoStepNote')}</p>
        ) : null}
      </Card>
    </li>
  );
}

const STATUS: Record<Payout['status'], { tone: string; labelKey: string }> = {
  pending: { tone: 'pending', labelKey: 'awaitingApproval' },
  approved: { tone: 'approved', labelKey: 'awaitingExecution' },
  processing: { tone: 'processing', labelKey: 'processing' },
  paid: { tone: 'paid', labelKey: 'paid' },
  on_hold: { tone: 'on_hold', labelKey: 'onHold' },
  failed: { tone: 'failed', labelKey: 'failed' },
  reversed: { tone: 'reversed', labelKey: 'reversed' },
};

function Money({ label, value, strong }: { label: string; value: string; strong?: boolean }) {
  return (
    <div>
      <dt className="text-[10px] uppercase tracking-wider text-muted-2">{label}</dt>
      <dd className={`mono ${strong ? 'font-semibold text-ink' : 'text-muted'}`}>{value}</dd>
    </div>
  );
}
