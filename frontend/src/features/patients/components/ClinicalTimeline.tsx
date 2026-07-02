// Unified clinical timeline — the center column of the patient records screen.
// Consumes GET /patients/{id}/timeline: chips are rendered PURELY from the
// backend's categories[] (bilingual labels + counts; an "All records" chip sums
// them) — never a hardcoded category list, so a future 'imaging' category appears
// automatically. Items render as reverse-chronological cards on a vertical spine
// with per-category icon dots; clicking a card opens the EXISTING detail surface
// for its ref.type (prescription / lab report / medical-history batch).
//
// The whole read is purpose-gated (one declaration unlocks it). Writes elsewhere
// (paper-Rx import / verify / prescription finalize) invalidate this query.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  FileText,
  FlaskConical,
  Paperclip,
  Pill,
  ScanLine,
  ShieldCheck,
  Stethoscope,
  Syringe,
  TriangleAlert,
  Upload,
} from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { KebabMenu, type KebabItem } from '@/components/ui/KebabMenu';
import { Skeleton } from '@/components/ui/Skeleton';
import { relativeTime, shortDate } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { usePatientTimeline } from '../api';
import { AbdmTab } from './AbdmTab';
import type { ConsentStatus, PurposeOfUse, TimelineItem } from '@/lib/mock/contracts';

const DEVA = /[ऀ-ॿ]/;

/** Per-category icon + badge tone. Unknown keys fall back to a neutral badge so a
 *  new backend category still renders (forward-compatible). */
const CATEGORY_META: Record<string, { icon: typeof Pill; badge: string }> = {
  prescription: { icon: Pill, badge: 'bg-primary-soft text-primary' },
  lab_report: { icon: FlaskConical, badge: 'bg-accent-soft text-accent' },
  vaccination: { icon: Syringe, badge: 'bg-warn-soft text-warn' },
  document: { icon: FileText, badge: 'bg-surface-sunk text-muted' },
  medical_history: { icon: FileText, badge: 'bg-surface-sunk text-muted' },
};
const metaFor = (key: string) => CATEGORY_META[key] ?? { icon: FileText, badge: 'bg-surface-sunk text-muted' };

/** ref.type → whether the card opens a known detail surface. */
const OPENABLE = new Set(['prescription', 'lab_report', 'medical_history_batch']);

export function ClinicalTimeline({
  patientId,
  purpose,
  abdmConsent,
  onNewPrescription,
}: {
  patientId: string;
  purpose: PurposeOfUse;
  abdmConsent: ConsentStatus | undefined;
  onNewPrescription: () => void;
}) {
  const { t, i18n } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const [filter, setFilter] = useState<string>('all');
  // ABDM is a CONSENT-SPECIAL surface, not a timeline category: its consent model
  // differs from the aggregate (active consent OR break-glass), so it's a distinct
  // chip that delegates to AbdmTab rather than a backend category. Gated on read.
  const canAbdm = can('docslot.abdm.records.read');

  const { data, isLoading, isError, refetch } = usePatientTimeline(patientId, purpose);
  const isHi = i18n.language?.startsWith('hi');

  const categories = data?.categories ?? [];
  const items = data?.items ?? [];
  const totalCount = categories.reduce((sum, c) => sum + c.count, 0);
  // Label lookup for the per-card category badge (backend-driven, bilingual).
  const labelFor = (key: string) => {
    const c = categories.find((x) => x.key === key);
    return c ? (isHi ? c.labelHi : c.labelEn) : key;
  };

  const filtered = (filter === 'all' ? items : items.filter((i) => i.category === filter))
    .slice()
    .sort((a, b) => (a.occurredAt < b.occurredAt ? 1 : a.occurredAt > b.occurredAt ? -1 : 0));

  // Upload-record dropdown — permission-filtered.
  const uploadItems: KebabItem[] = [
    ...(can('docslot.medical_history.intake') || can('docslot.medical_history.create')
      ? [{ key: 'paper-rx', label: t('clinical.history.import.action'), icon: <ScanLine size={15} />, onSelect: () => openPanel({ type: 'importHistory', patientId, purpose }) }]
      : []),
    ...(can('docslot.report.upload')
      ? [{ key: 'upload-report', label: t('clinical.reports.upload'), icon: <Upload size={15} />, onSelect: () => openPanel({ type: 'uploadReport', patientId }) }]
      : []),
    ...(can('docslot.medical_history.create')
      ? [{ key: 'add-record', label: t('clinical.history.add'), icon: <FileText size={15} />, onSelect: () => openPanel({ type: 'createHistory', patientId, purpose }) }]
      : []),
  ];

  const openRef = (item: TimelineItem) => {
    if (item.ref.type === 'prescription') openPanel({ type: 'prescriptionDetail', prescriptionId: item.ref.id, patientId, purpose });
    else if (item.ref.type === 'lab_report') openPanel({ type: 'labReportDetail', reportId: item.ref.id, patientId, purpose });
    else if (item.ref.type === 'medical_history_batch') openPanel({ type: 'historyBatch', batchId: item.ref.id, patientId, purpose });
  };

  return (
    <div className="flex min-w-0 flex-col gap-4">
      {/* Toolbar: backend-driven category chips + record actions. */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap gap-1.5" role="tablist" aria-label={t('clinical.title')}>
          <Chip label={t('clinical.timeline.allRecords')} count={totalCount} active={filter === 'all'} onClick={() => setFilter('all')} />
          {categories.map((c) => (
            <Chip
              key={c.key}
              label={isHi ? c.labelHi : c.labelEn}
              deva={isHi}
              count={c.count}
              active={filter === c.key}
              onClick={() => setFilter(c.key)}
            />
          ))}
          {/* ABDM — consent-special chip, visually distinct (shield + accent). */}
          {canAbdm ? (
            <button
              type="button"
              role="tab"
              aria-selected={filter === 'abdm'}
              onClick={() => setFilter('abdm')}
              className={[
                'inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-[12px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary',
                filter === 'abdm' ? 'border-accent bg-accent text-bg' : 'border-accent/40 bg-accent-soft text-accent hover:bg-accent hover:text-bg',
              ].join(' ')}
            >
              <ShieldCheck size={12} aria-hidden="true" />
              {t('clinical.tabAbdm')}
            </button>
          ) : null}
        </div>
        <div className="flex items-center gap-2">
          {uploadItems.length > 0 ? (
            <KebabMenu variant="labeled" align="right" label={t('clinical.timeline.uploadRecord')} triggerIcon={<Upload size={14} />} items={uploadItems} />
          ) : null}
          {can('docslot.prescription.create') ? (
            <Button variant="primary" size="sm" onClick={onNewPrescription}>
              <Stethoscope size={14} aria-hidden="true" />
              {t('clinical.rx.new')}
            </Button>
          ) : null}
        </div>
      </div>

      {filter === 'abdm' ? (
        // ABDM owns its own consent gate + lazy fetch (active consent OR break-glass).
        <AbdmTab patientId={patientId} purpose={purpose} abdmConsent={abdmConsent} />
      ) : isError ? (
        <Card>
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        </Card>
      ) : isLoading || !data ? (
        <TimelineSkeleton />
      ) : filtered.length === 0 ? (
        <Card>
          <EmptyState
            title={t('clinical.timeline.empty')}
            description={t('clinical.timeline.emptyBody')}
            actionLabel={uploadItems.length > 0 ? t('clinical.timeline.uploadRecord') : undefined}
            onAction={uploadItems.length > 0 ? uploadItems[0].onSelect : undefined}
          />
        </Card>
      ) : (
        <ol className="flex flex-col">
          {filtered.map((item, idx) => (
            <TimelineRow
              key={item.itemId}
              item={item}
              categoryLabel={labelFor(item.category)}
              last={idx === filtered.length - 1}
              openable={OPENABLE.has(item.ref.type)}
              onOpen={() => openRef(item)}
            />
          ))}
        </ol>
      )}
    </div>
  );
}

function Chip({ label, count, active, onClick, deva }: { label: string; count?: number; active: boolean; onClick: () => void; deva?: boolean }) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={[
        'inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-[12px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary',
        active ? 'border-primary bg-primary text-bg' : 'border-line bg-surface text-muted hover:bg-surface-sunk hover:text-ink',
        deva ? 'deva' : '',
      ].join(' ')}
    >
      {label}
      {typeof count === 'number' ? (
        <span className={`rounded-full px-1.5 text-[10px] ${active ? 'bg-bg/20 text-bg' : 'bg-surface-sunk text-muted-2'}`}>{count}</span>
      ) : null}
    </button>
  );
}

/** One card on the vertical spine: a dot column (icon + connector) + the card. */
function TimelineRow({
  item,
  categoryLabel,
  last,
  openable,
  onOpen,
}: {
  item: TimelineItem;
  categoryLabel: string;
  last: boolean;
  openable: boolean;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  const meta = metaFor(item.category);
  const Icon = meta.icon;

  const body = (
    <Card className="w-full p-3 text-left">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className={`inline-flex items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wider ${meta.badge}`}>{categoryLabel}</span>
            {item.unverified ? (
              <span className="inline-flex items-center gap-1 rounded-full bg-warn-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase text-warn">
                <TriangleAlert size={10} aria-hidden="true" />
                {t('clinical.timeline.unverified')}
              </span>
            ) : null}
            {item.hasAttachment ? (
              <Paperclip size={12} className="text-muted-2" aria-label={t('clinical.timeline.hasAttachment')} />
            ) : null}
          </div>
          <p className={`mt-1 truncate text-[13px] font-medium text-ink ${DEVA.test(item.title) ? 'deva' : ''}`}>{item.title}</p>
          {item.subtitle ? <p className={`text-[12px] text-muted ${DEVA.test(item.subtitle) ? 'deva' : ''}`}>{item.subtitle}</p> : null}
          {item.summary ? <p className={`mt-0.5 line-clamp-1 text-[12px] text-muted-2 ${DEVA.test(item.summary) ? 'deva' : ''}`}>{item.summary}</p> : null}
          {item.tags.length > 0 ? (
            <div className="mt-1.5 flex flex-wrap gap-1">
              {item.tags.map((tag, i) => (
                <span key={i} className={`rounded bg-surface-sunk px-1.5 py-0.5 text-[10px] capitalize text-muted ${DEVA.test(tag) ? 'deva' : ''}`}>{tag}</span>
              ))}
            </div>
          ) : null}
        </div>
        <div className="flex shrink-0 flex-col items-end text-right">
          <span className="mono text-[11px] text-muted-2">{shortDate(item.occurredAt)}</span>
          <span className="text-[11px] text-muted-2">{relativeTime(item.occurredAt)}</span>
        </div>
      </div>
    </Card>
  );

  return (
    <li className="flex gap-3">
      {/* Spine: icon dot + a connector that reaches the next dot. */}
      <div className="flex flex-col items-center" aria-hidden="true">
        <span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full ${meta.badge}`}>
          <Icon size={15} />
        </span>
        {!last ? <span className="w-px flex-1 bg-line" /> : null}
      </div>
      <div className="min-w-0 flex-1 pb-4">
        {openable ? (
          <button
            type="button"
            onClick={onOpen}
            className="block w-full rounded-[var(--radius)] text-left transition-colors hover:brightness-[0.99] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
          >
            {body}
          </button>
        ) : (
          body
        )}
      </div>
    </li>
  );
}

/** Skeleton: the spine + three ghost cards. */
function TimelineSkeleton() {
  return (
    <ol className="flex flex-col" role="status" aria-busy="true">
      {Array.from({ length: 3 }).map((_, i) => (
        <li key={i} className="flex gap-3">
          <div className="flex flex-col items-center" aria-hidden="true">
            <Skeleton className="h-8 w-8 rounded-full" />
            {i < 2 ? <span className="w-px flex-1 bg-line" /> : null}
          </div>
          <div className="flex-1 pb-4">
            <Skeleton className="h-20 w-full rounded-[var(--radius)]" />
          </div>
        </li>
      ))}
    </ol>
  );
}
