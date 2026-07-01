// Audit log timeline (#86) — the Team console "Audit log" tab. A left CATEGORY
// facet rail (with counts), a SEVERITY filter, a free-text search, a date-range
// control (default: last 30 days), a CSV Export, and day-grouped rows (time,
// actor, an action pill, the target, and the raw ip — NO city yet, that's #94).
//
// Wired through the data seam (real GET /security/audit/logs + /export, mock
// otherwise). Gated on tenant.audit.read by the parent tab. States: loading
// skeleton, error, empty (widen the range), and populated. NO PHI: actors are
// staff identities (same directory as People); resourceLabel is server-humanized.

import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Download, FileClock, Search, ShieldAlert, TriangleAlert } from 'lucide-react';
import { toast } from 'sonner';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { TextInput } from '@/components/ui/Field';
import { shortDate } from '@/lib/format';
import { downloadTextFile } from '@/lib/download';
import { toUserError } from '@/lib/backend';
import { useAuditLog, useExportAuditLog } from '../api';
import type { AuditFacetCount, AuditLogFilter, AuditLogRow } from '@/lib/mock/contracts';

const PAGE_SIZE = 25;
const DEFAULT_DAYS = 30;
const NEUTRAL = 'bg-surface-sunk text-muted';

// Severity tone (token-only, never colour alone — text + a dot). Informational is
// neutral; Warning is amber; Critical is terracotta/danger.
const SEV_TONE: Record<string, string> = {
  Informational: NEUTRAL,
  Warning: 'bg-warn-soft text-warn',
  Critical: 'bg-danger-soft text-danger',
};
const SEV_DOT: Record<string, string> = {
  Informational: 'bg-muted-2',
  Warning: 'bg-warn',
  Critical: 'bg-danger',
};

const SEVERITIES = ['Informational', 'Warning', 'Critical'] as const;

// ── Date helpers (all IST-anchored) ──────────────────────────────────────────
function defaultRange(days: number): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to.getTime() - days * 86_400_000);
  return { from: from.toISOString(), to: to.toISOString() };
}
/** ISO → YYYY-MM-DD in Asia/Kolkata (for a <input type="date"> value). */
function istDateInput(iso: string): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Kolkata',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(new Date(iso));
}
/** IST HH:mm for a row. */
function istTime(iso: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Kolkata',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(iso));
}

function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const id = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(id);
  }, [value, delayMs]);
  return debounced;
}

export function AuditLogTab() {
  const { t } = useTranslation();

  const [category, setCategory] = useState<string | null>(null);
  const [severity, setSeverity] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [preset, setPreset] = useState<number | 'custom'>(DEFAULT_DAYS);
  const [range, setRange] = useState(() => defaultRange(DEFAULT_DAYS));
  const [page, setPage] = useState(1);

  const debouncedSearch = useDebounced(search, 250);

  // Any filter change resets to page 1 so the user never lands on an out-of-range page.
  useEffect(() => {
    setPage(1);
  }, [category, severity, debouncedSearch, range.from, range.to]);

  const filter: AuditLogFilter = {
    page,
    pageSize: PAGE_SIZE,
    from: range.from,
    to: range.to,
    category,
    severity,
    search: debouncedSearch.trim() || null,
  };

  const { data, isLoading, isError, isFetching, refetch } = useAuditLog(filter);
  const exportCsv = useExportAuditLog();

  const total = data?.total ?? 0;
  const items = data?.items ?? [];
  const categoryFacets: AuditFacetCount[] = data?.categoryFacets ?? [];
  const severityFacets: AuditFacetCount[] = data?.severityFacets ?? [];
  const allCount = categoryFacets.reduce((sum, f) => sum + f.count, 0);
  const sevCount = (key: string) => severityFacets.find((f) => f.key === key)?.count ?? 0;

  const onPreset = (days: number) => {
    setPreset(days);
    setRange(defaultRange(days));
  };
  const onFromDate = (value: string) => {
    if (!value) return;
    setPreset('custom');
    setRange((r) => ({ ...r, from: `${value}T00:00:00+05:30` }));
  };
  const onToDate = (value: string) => {
    if (!value) return;
    setPreset('custom');
    setRange((r) => ({ ...r, to: `${value}T23:59:59+05:30` }));
  };

  const onExport = async () => {
    try {
      const result = await exportCsv.mutateAsync(filter);
      downloadTextFile(result.fileName, result.content);
      toast.success(t('team.audit.exportDone'));
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const start = total === 0 ? 0 : (page - 1) * PAGE_SIZE + 1;
  const end = (page - 1) * PAGE_SIZE + items.length;
  const hasNext = end < total;

  return (
    <div className="flex flex-col gap-4">
      {/* Toolbar: search · severity · date range · export */}
      <div className="flex flex-col gap-3">
        <div className="flex flex-col gap-2 lg:flex-row lg:items-center lg:justify-between">
          <div className="relative lg:max-w-xs lg:flex-1">
            <Search
              size={15}
              aria-hidden="true"
              className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-2"
            />
            <TextInput
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('team.audit.searchPlaceholder')}
              aria-label={t('team.audit.searchPlaceholder')}
              className="pl-9"
            />
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <DateRange
              from={istDateInput(range.from)}
              to={istDateInput(range.to)}
              preset={preset}
              onPreset={onPreset}
              onFromDate={onFromDate}
              onToDate={onToDate}
            />
            <Button
              variant="ghost"
              size="sm"
              onClick={() => void onExport()}
              disabled={exportCsv.isPending || total === 0}
            >
              <Download size={15} aria-hidden="true" />
              {t('team.audit.export')}
            </Button>
          </div>
        </div>

        {/* Severity filter — segmented, each with its facet count. */}
        <div role="radiogroup" aria-label={t('team.audit.severityLabel')} className="flex flex-wrap gap-1.5">
          <SeverityChip
            active={severity === null}
            onClick={() => setSeverity(null)}
            label={t('team.audit.allSeverities')}
          />
          {SEVERITIES.map((s) => (
            <SeverityChip
              key={s}
              active={severity === s}
              onClick={() => setSeverity(severity === s ? null : s)}
              label={t(`team.audit.severity.${s}`, { defaultValue: s })}
              count={sevCount(s)}
              dot={SEV_DOT[s]}
            />
          ))}
        </div>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-[13rem_1fr]">
        {/* CATEGORY facet rail */}
        <CategoryRail
          facets={categoryFacets}
          allCount={allCount}
          active={category}
          loading={isLoading}
          onSelect={setCategory}
        />

        {/* Timeline */}
        <div className="min-w-0">
          {isError && !data ? (
            <Card>
              <EmptyState
                icon={<TriangleAlert size={28} aria-hidden="true" />}
                title={t('error.genericTitle')}
                description={t('error.genericBody')}
                actionLabel={t('common.retry')}
                onAction={() => void refetch()}
              />
            </Card>
          ) : isLoading ? (
            <TimelineSkeleton />
          ) : total === 0 ? (
            <Card>
              <EmptyState
                icon={<FileClock size={28} aria-hidden="true" />}
                title={t('team.audit.emptyTitle')}
                description={t('team.audit.emptyBody')}
              />
            </Card>
          ) : (
            <div className={`flex flex-col gap-4 transition-opacity ${isFetching ? 'opacity-60' : ''}`}>
              <Timeline items={items} />
              <Pagination
                start={start}
                end={end}
                total={total}
                canPrev={page > 1}
                canNext={hasNext}
                busy={isFetching}
                onPrev={() => setPage((p) => Math.max(1, p - 1))}
                onNext={() => setPage((p) => p + 1)}
              />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// ── Date-range control: quick presets + explicit from/to ─────────────────────
function DateRange({
  from,
  to,
  preset,
  onPreset,
  onFromDate,
  onToDate,
}: {
  from: string;
  to: string;
  preset: number | 'custom';
  onPreset: (days: number) => void;
  onFromDate: (v: string) => void;
  onToDate: (v: string) => void;
}) {
  const { t } = useTranslation();
  const dateInput =
    'rounded-[var(--radius-sm)] border border-line bg-surface px-2 py-1.5 text-[12px] text-ink outline-none ' +
    'focus:border-primary focus:ring-2 focus:ring-primary-soft';
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <div role="radiogroup" aria-label={t('team.audit.rangeLabel')} className="flex gap-1.5">
        {[7, 30, 90].map((d) => (
          <button
            key={d}
            type="button"
            role="radio"
            aria-checked={preset === d}
            onClick={() => onPreset(d)}
            className={[
              'rounded-[var(--radius-sm)] border px-2.5 py-1.5 text-[12px] transition-colors',
              preset === d ? 'border-primary bg-primary text-bg' : 'border-line text-ink hover:bg-surface-sunk',
            ].join(' ')}
          >
            {t('team.audit.lastDays', { count: d })}
          </button>
        ))}
      </div>
      <input
        type="date"
        value={from}
        max={to}
        onChange={(e) => onFromDate(e.target.value)}
        aria-label={t('team.audit.fromDate')}
        className={dateInput}
      />
      <span aria-hidden="true" className="text-[12px] text-muted-2">
        –
      </span>
      <input
        type="date"
        value={to}
        min={from}
        onChange={(e) => onToDate(e.target.value)}
        aria-label={t('team.audit.toDate')}
        className={dateInput}
      />
    </div>
  );
}

function SeverityChip({
  active,
  onClick,
  label,
  count,
  dot,
}: {
  active: boolean;
  onClick: () => void;
  label: string;
  count?: number;
  dot?: string;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      onClick={onClick}
      className={[
        'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px] transition-colors',
        active ? 'border-primary bg-primary-soft text-primary' : 'border-line text-muted hover:bg-surface-sunk',
      ].join(' ')}
    >
      {dot ? <span className={`h-1.5 w-1.5 rounded-full ${dot}`} aria-hidden="true" /> : null}
      {label}
      {count !== undefined ? <span className="text-muted-2">{count}</span> : null}
    </button>
  );
}

function CategoryRail({
  facets,
  allCount,
  active,
  loading,
  onSelect,
}: {
  facets: { key: string; count: number }[];
  allCount: number;
  active: string | null;
  loading: boolean;
  onSelect: (c: string | null) => void;
}) {
  const { t } = useTranslation();
  return (
    <Card className="h-max overflow-hidden">
      <div className="border-b border-line px-3 py-2.5">
        <h3 className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          {t('team.audit.categoriesHeading')}
        </h3>
      </div>
      {loading ? (
        <ul className="flex flex-col p-1.5" role="status" aria-busy="true">
          {Array.from({ length: 5 }).map((_, i) => (
            <li key={i} className="px-2 py-2">
              <Skeleton className="h-3 w-full" />
            </li>
          ))}
        </ul>
      ) : (
        <ul className="flex flex-col p-1.5">
          <CategoryRow label={t('team.audit.allCategories')} count={allCount} active={active === null} onClick={() => onSelect(null)} />
          {facets.map((f) => (
            <CategoryRow
              key={f.key}
              label={t(`team.audit.category.${f.key}`, { defaultValue: f.key })}
              count={f.count}
              active={active === f.key}
              onClick={() => onSelect(active === f.key ? null : f.key)}
            />
          ))}
        </ul>
      )}
    </Card>
  );
}

function CategoryRow({
  label,
  count,
  active,
  onClick,
}: {
  label: string;
  count: number;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <li>
      <button
        type="button"
        aria-pressed={active}
        onClick={onClick}
        className={[
          'flex w-full items-center justify-between gap-2 rounded-[var(--radius-sm)] px-2.5 py-2 text-left text-[13px] transition-colors',
          active ? 'bg-primary-soft font-medium text-primary' : 'text-ink hover:bg-surface-sunk',
        ].join(' ')}
      >
        <span className="min-w-0 truncate">{label}</span>
        <span className={`shrink-0 text-[11px] ${active ? 'text-primary' : 'text-muted-2'}`}>{count}</span>
      </button>
    </li>
  );
}

// ── Timeline (day-grouped) ───────────────────────────────────────────────────
function Timeline({ items }: { items: AuditLogRow[] }) {
  const { t } = useTranslation();

  // Group by IST calendar day, preserving the server's newest-first order.
  const groups: { day: string; rows: AuditLogRow[] }[] = [];
  for (const row of items) {
    const day = istDateInput(row.occurredAt);
    const last = groups[groups.length - 1];
    if (last && last.day === day) last.rows.push(row);
    else groups.push({ day, rows: [row] });
  }

  const todayKey = istDateInput(new Date().toISOString());
  const yesterdayKey = istDateInput(new Date(Date.now() - 86_400_000).toISOString());
  const dayLabel = (day: string, isoOfRow: string) =>
    day === todayKey ? t('team.audit.today') : day === yesterdayKey ? t('team.audit.yesterday') : shortDate(isoOfRow);

  return (
    <div className="flex flex-col gap-4">
      {groups.map((g) => (
        <section key={g.day} aria-label={dayLabel(g.day, g.rows[0].occurredAt)}>
          <h3 className="mb-2 px-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {dayLabel(g.day, g.rows[0].occurredAt)}
          </h3>
          <Card className="overflow-hidden">
            <ul className="flex flex-col">
              {g.rows.map((row) => (
                <AuditRow key={row.auditId} row={row} />
              ))}
            </ul>
          </Card>
        </section>
      ))}
    </div>
  );
}

function AuditRow({ row }: { row: AuditLogRow }) {
  const { t } = useTranslation();
  const tone = SEV_TONE[row.severity] ?? NEUTRAL;
  const dot = SEV_DOT[row.severity] ?? 'bg-muted-2';

  return (
    <li className="flex items-start gap-3 border-b border-line px-4 py-3 last:border-0">
      <span className="mono mt-0.5 w-12 shrink-0 text-[11px] text-muted-2">{istTime(row.occurredAt)}</span>

      <div className="min-w-0 flex-1">
        {/* Actor + action */}
        <div className="flex flex-wrap items-center gap-2">
          {row.actorName ? (
            <span className="inline-flex items-center gap-1.5">
              <Avatar name={row.actorName} size="sm" />
              <span className="text-[13px] font-medium text-ink">{row.actorName}</span>
            </span>
          ) : (
            <span className="text-[13px] font-medium text-muted">{t('team.audit.systemActor')}</span>
          )}

          <span className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-medium ${tone}`}>
            <span className={`h-1.5 w-1.5 rounded-full ${dot}`} aria-hidden="true" />
            {row.action}
          </span>

          {!row.success ? (
            <span className="inline-flex items-center gap-1 rounded-full bg-danger-soft px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider text-danger">
              <ShieldAlert size={11} aria-hidden="true" />
              {row.errorCode ? t('team.audit.failedWith', { code: row.errorCode }) : t('team.audit.failed')}
            </span>
          ) : null}

          {row.impersonatorName ? (
            <span className="inline-flex items-center gap-1 rounded-full bg-accent-soft px-2 py-0.5 text-[10px] font-medium text-accent">
              {t('team.audit.impersonatedBy', { name: row.impersonatorName })}
            </span>
          ) : null}
        </div>

        {/* Target + raw verb */}
        <p className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px] text-muted">
          <span className="truncate">{row.resourceLabel ?? row.resourceType}</span>
          <span aria-hidden="true" className="text-muted-2">
            ·
          </span>
          <span className="mono text-[11px] text-muted-2">{row.rawAction}</span>
        </p>
      </div>

      {/* Raw IP (no city — #94). */}
      {row.ipAddress ? (
        <span className="mono hidden shrink-0 self-center text-[11px] text-muted-2 sm:inline" title={t('team.audit.ipAddress')}>
          {row.ipAddress}
        </span>
      ) : null}
    </li>
  );
}

function Pagination({
  start,
  end,
  total,
  canPrev,
  canNext,
  busy,
  onPrev,
  onNext,
}: {
  start: number;
  end: number;
  total: number;
  canPrev: boolean;
  canNext: boolean;
  busy: boolean;
  onPrev: () => void;
  onNext: () => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="flex items-center justify-between gap-2">
      <p className="text-[12px] text-muted-2" aria-live="polite">
        {t('team.audit.rangeCount', { start, end, total })}
      </p>
      <div className="flex items-center gap-1.5">
        <Button variant="ghost" size="sm" onClick={onPrev} disabled={!canPrev || busy}>
          {t('team.audit.prev')}
        </Button>
        <Button variant="ghost" size="sm" onClick={onNext} disabled={!canNext || busy}>
          {t('team.audit.next')}
        </Button>
      </div>
    </div>
  );
}

function TimelineSkeleton() {
  return (
    <div className="flex flex-col gap-4" role="status" aria-busy="true">
      {Array.from({ length: 2 }).map((_, g) => (
        <div key={g}>
          <Skeleton className="mb-2 h-3 w-20" />
          <Card className="overflow-hidden">
            <ul className="flex flex-col">
              {Array.from({ length: 4 }).map((_, i) => (
                <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                  <Skeleton className="h-3 w-10" />
                  <Skeleton className="h-8 w-8 rounded-full" />
                  <div className="flex flex-1 flex-col gap-2">
                    <Skeleton className="h-3 w-48" />
                    <Skeleton className="h-3 w-32" />
                  </div>
                  <Skeleton className="h-3 w-24" />
                </li>
              ))}
            </ul>
          </Card>
        </div>
      ))}
    </div>
  );
}
