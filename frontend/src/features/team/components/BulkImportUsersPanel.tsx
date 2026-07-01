// Bulk-import members slide-over (#95) — the People-tab toolbar "Bulk import" action.
// Two phases inside ONE focus-trapped slide-over (SlideOver owns the trap, Esc/overlay
// close, and focus-return per REACT_SKILL pattern 14):
//
//   1. INPUT — choose a CSV file (or paste rows), map the columns (auto-detected,
//      overridable), and preview the parsed rows with per-row validation. The batch
//      cap (500) is enforced before we POST (the server 422s an oversize batch anyway).
//   2. RESULT — the per-row outcome (created / linked / skipped / error) + a summary.
//
// The CSV is parsed CLIENT-SIDE (RFC-4180-ish: quoted fields, escaped quotes, embedded
// commas/newlines). Provisioning is gated tenant.users.create server-side (the toolbar
// also gates the button); role is conferred subject to the R3 no-escalation guard. On a
// successful import the People list refreshes (the mutation invalidates the users query).
// Tokens only, bilingual, no PHI (staff identities only).

import { useEffect, useRef, useState, type ChangeEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  ArrowLeft,
  CircleCheck,
  Link2,
  MinusCircle,
  Upload,
  X as XIcon,
} from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { Select, TextArea, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import type { BulkImportResult, BulkImportResultRow } from '@/lib/mock/contracts';
import { useBulkImportUsers } from '../api';

const MAX_BATCH = 500;
const PREVIEW_ROWS = 8;
const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

/** RFC-4180-ish CSV parse: handles quoted fields, "" escapes, and embedded commas /
 *  CR / LF inside quotes. Strips a leading BOM. Blank lines are dropped. */
function parseCsv(text: string): string[][] {
  const src = text.replace(/^﻿/, '');
  const rows: string[][] = [];
  let field = '';
  let row: string[] = [];
  let inQuotes = false;
  let i = 0;
  const pushField = () => {
    row.push(field);
    field = '';
  };
  const pushRow = () => {
    pushField();
    rows.push(row);
    row = [];
  };
  while (i < src.length) {
    const c = src[i];
    if (inQuotes) {
      if (c === '"') {
        if (src[i + 1] === '"') {
          field += '"';
          i += 2;
          continue;
        }
        inQuotes = false;
        i += 1;
        continue;
      }
      field += c;
      i += 1;
      continue;
    }
    if (c === '"') {
      inQuotes = true;
      i += 1;
      continue;
    }
    if (c === ',') {
      pushField();
      i += 1;
      continue;
    }
    if (c === '\r') {
      i += 1;
      continue;
    }
    if (c === '\n') {
      pushRow();
      i += 1;
      continue;
    }
    field += c;
    i += 1;
  }
  if (field.length > 0 || row.length > 0) pushRow();
  // Drop fully-blank rows (trailing newline, empty lines).
  return rows.filter((r) => r.some((cell) => cell.trim() !== ''));
}

/** First column index whose header matches `re`, else `fallback`. */
function pickColumn(cols: string[], re: RegExp, fallback: number): number {
  const idx = cols.findIndex((c) => re.test(c.trim().toLowerCase()));
  return idx >= 0 ? idx : fallback;
}

// Per-status pill config — token-only, icon + text (never colour alone). `linked`
// reuses the info tint (an existing account was joined), `skipped` the warn tint, and
// `errored` the danger tint; `created` the positive teal. Unknown statuses fall back
// to neutral so an additive backend status still renders.
const STATUS_STYLE: Record<string, { className: string; icon: typeof CircleCheck }> = {
  created: { className: 'bg-primary-soft text-primary', icon: CircleCheck },
  linked: { className: 'bg-info-soft text-info', icon: Link2 },
  skipped: { className: 'bg-warn-soft text-warn', icon: MinusCircle },
  errored: { className: 'bg-danger-soft text-danger', icon: XIcon },
};
const NEUTRAL_STATUS = { className: 'bg-surface-sunk text-muted', icon: MinusCircle };

function ResultStatusPill({ status }: { status: string }) {
  const { t } = useTranslation();
  const key = status.toLowerCase();
  const cfg = STATUS_STYLE[key] ?? NEUTRAL_STATUS;
  const Icon = cfg.icon;
  // Known statuses get a translated label; anything unknown shows the raw server value.
  const label = STATUS_STYLE[key] ? t(`team.import.status.${key}`) : status;
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium ${cfg.className}`}>
      <Icon size={11} aria-hidden="true" />
      {label}
    </span>
  );
}

export function BulkImportUsersPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const bulkImport = useBulkImportUsers();
  const fileRef = useRef<HTMLInputElement | null>(null);

  const [raw, setRaw] = useState('');
  const [fileName, setFileName] = useState<string | null>(null);
  const [hasHeader, setHasHeader] = useState(true);
  const [map, setMap] = useState<{ email: number; name: number; role: number }>({ email: 0, name: 1, role: -1 });
  const [result, setResult] = useState<BulkImportResult | null>(null);

  // Derived parse (pure; the React Compiler memoizes on `raw`).
  const matrix = parseCsv(raw);
  const maxCols = matrix.reduce((m, r) => Math.max(m, r.length), 0);
  const columns = hasHeader
    ? (matrix[0] ?? [])
    : Array.from({ length: maxCols }, (_, i) => `${t('team.import.column')} ${i + 1}`);
  const dataRows = hasHeader ? matrix.slice(1) : matrix;

  // Re-run column auto-detection whenever the header signature changes (a new file /
  // paste, or toggling "first row is a header"). Guarded by a signature ref so it never
  // clobbers a user's manual mapping override on unrelated re-renders.
  const sig = `${hasHeader}|${columns.join('')}`;
  const lastSig = useRef<string>('');
  useEffect(() => {
    if (sig === lastSig.current) return;
    lastSig.current = sig;
    setMap({
      email: pickColumn(columns, /mail/, 0),
      name: pickColumn(columns, /name/, columns.length > 1 ? 1 : 0),
      role: pickColumn(columns, /role/, -1),
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig]);

  const parsedRows = dataRows.map((cells, i) => {
    const email = (cells[map.email] ?? '').trim();
    const fullName = (cells[map.name] ?? '').trim();
    const roleKey = map.role >= 0 ? (cells[map.role] ?? '').trim() || null : null;
    const emailValid = EMAIL_RE.test(email);
    const nameValid = fullName.length > 0;
    return { index: i + 1, email, fullName, roleKey, emailValid, nameValid, valid: emailValid && nameValid };
  });
  const importable = parsedRows.filter((r) => r.valid);
  const invalidCount = parsedRows.length - importable.length;
  const overCap = importable.length > MAX_BATCH;
  const hasInput = matrix.length > 0;

  const onFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    try {
      const text = await file.text();
      setRaw(text);
      setFileName(file.name);
    } catch {
      toast.error(t('team.import.readFailed'));
    }
    // Reset so re-selecting the SAME file fires onChange again.
    e.target.value = '';
  };

  const reset = () => {
    setRaw('');
    setFileName(null);
    setResult(null);
    lastSig.current = '';
  };

  const onImport = async () => {
    if (!importable.length || overCap) return;
    try {
      const res = await bulkImport.mutateAsync({
        rows: importable.map((r) => ({ email: r.email, fullName: r.fullName, roleKey: r.roleKey })),
        idempotencyKey: idempotencyKey(),
      });
      setResult(res);
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const colSelect = (which: 'email' | 'name' | 'role') => (
    <Select
      value={String(map[which])}
      onChange={(e) => setMap((m) => ({ ...m, [which]: Number(e.target.value) }))}
      aria-label={t(`team.import.col${which === 'email' ? 'Email' : which === 'name' ? 'Name' : 'Role'}`)}
    >
      {which === 'role' ? <option value="-1">{t('team.import.roleNone')}</option> : null}
      {columns.map((c, i) => (
        <option key={i} value={String(i)}>
          {c || `${t('team.import.column')} ${i + 1}`}
        </option>
      ))}
    </Select>
  );

  const footer = result ? (
    <>
      <Button variant="ghost" size="md" type="button" onClick={reset}>
        <ArrowLeft size={15} aria-hidden="true" />
        {t('team.import.importMore')}
      </Button>
      <Button variant="primary" size="md" type="button" onClick={onClose}>
        {t('team.import.done')}
      </Button>
    </>
  ) : (
    <>
      <Button variant="ghost" size="md" type="button" onClick={onClose}>
        {t('common.cancel')}
      </Button>
      <Button
        variant="primary"
        size="md"
        type="button"
        onClick={onImport}
        disabled={!importable.length || overCap || bulkImport.isPending}
      >
        {t('team.import.submit', { count: importable.length })}
      </Button>
    </>
  );

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.import.eyebrow')}
      title={t('team.import.title')}
      description={t('team.import.description')}
      footer={footer}
    >
      {result ? (
        <ImportResult result={result} />
      ) : (
        <div className="flex flex-col gap-4">
          <p className="text-[13px] text-muted">{t('team.import.description')}</p>

          {/* Source: file picker + paste. Both feed the same parse. */}
          <div className="flex flex-col gap-2">
            <input
              ref={fileRef}
              type="file"
              accept=".csv,text/csv"
              onChange={(e) => void onFile(e)}
              className="sr-only"
            />
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="ghost" size="sm" type="button" onClick={() => fileRef.current?.click()}>
                <Upload size={15} aria-hidden="true" />
                {t('team.import.chooseFile')}
              </Button>
              {fileName ? (
                <span className="truncate text-[12px] text-muted">{t('team.import.fileLoaded', { name: fileName })}</span>
              ) : (
                <span className="text-[12px] text-muted-2">{t('team.import.orPaste')}</span>
              )}
            </div>
            <TextArea
              aria-label={t('team.import.pasteLabel')}
              autoFocus
              rows={5}
              value={raw}
              onChange={(e) => {
                setRaw(e.target.value);
                setFileName(null);
              }}
              placeholder={t('team.import.pastePlaceholder')}
              className="mono text-[12px]"
            />
          </div>

          {hasInput ? (
            <>
              {/* Header toggle + column mapping. */}
              <label className="flex items-center gap-2 text-[13px] text-ink">
                <input
                  type="checkbox"
                  checked={hasHeader}
                  onChange={(e) => setHasHeader(e.target.checked)}
                  className="h-4 w-4 accent-[var(--primary)]"
                />
                {t('team.import.hasHeader')}
              </label>

              <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                <div>
                  <span className={labelClass}>{t('team.import.colEmail')}</span>
                  {colSelect('email')}
                </div>
                <div>
                  <span className={labelClass}>{t('team.import.colName')}</span>
                  {colSelect('name')}
                </div>
                <div>
                  <span className={labelClass}>{t('team.import.colRole')}</span>
                  {colSelect('role')}
                </div>
              </div>

              {/* Preview + counts. */}
              <div className="flex flex-col gap-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className={labelClass}>{t('team.import.preview')}</span>
                  <span className="text-[12px] text-muted">
                    {t('team.import.previewNote', { shown: Math.min(PREVIEW_ROWS, parsedRows.length), total: parsedRows.length })}
                  </span>
                </div>
                <div className="overflow-hidden rounded-[var(--radius-sm)] border border-line">
                  <table className="w-full text-[12px]">
                    <thead className="bg-surface-sunk text-muted-2">
                      <tr>
                        <th className="px-2 py-1.5 text-left font-medium">{t('team.import.colRow')}</th>
                        <th className="px-2 py-1.5 text-left font-medium">{t('team.import.colEmail')}</th>
                        <th className="px-2 py-1.5 text-left font-medium">{t('team.import.colName')}</th>
                        <th className="px-2 py-1.5 text-left font-medium">{t('team.import.colRole')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {parsedRows.slice(0, PREVIEW_ROWS).map((r) => (
                        <tr key={r.index} className={`border-t border-line ${r.valid ? '' : 'bg-danger-soft/40'}`}>
                          <td className="px-2 py-1.5 text-muted-2">{r.index}</td>
                          <td className={`px-2 py-1.5 ${r.emailValid ? 'text-ink' : 'text-danger'}`}>
                            {r.email || t('team.import.invalidEmail')}
                          </td>
                          <td className={`px-2 py-1.5 ${r.nameValid ? 'text-ink' : 'text-danger'}`}>
                            {r.fullName || t('team.import.missingName')}
                          </td>
                          <td className="px-2 py-1.5 text-muted">{r.roleKey ?? '—'}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>

                <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px]">
                  <span className="text-primary">{t('team.import.rowsValid', { count: importable.length })}</span>
                  {invalidCount > 0 ? (
                    <>
                      <span aria-hidden="true" className="text-muted-2">·</span>
                      <span className="text-warn">{t('team.import.rowsInvalid', { count: invalidCount })}</span>
                    </>
                  ) : null}
                </div>

                {overCap ? (
                  <p className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[12px] text-danger">
                    {t('team.import.overCap', { max: MAX_BATCH })}
                  </p>
                ) : null}
              </div>
            </>
          ) : (
            // Empty state — before any file/paste.
            <div className="rounded-[var(--radius-sm)] border border-dashed border-line px-4 py-6 text-center">
              <p className="text-[13px] font-medium text-ink">{t('team.import.emptyTitle')}</p>
              <p className="mt-1 text-[12px] text-muted">{t('team.import.emptyBody')}</p>
            </div>
          )}
        </div>
      )}
    </SlideOver>
  );
}

/** Result phase — the summary chips + the per-row outcome list. */
function ImportResult({ result }: { result: BulkImportResult }) {
  const { t } = useTranslation();
  const chips: { key: string; count: number; className: string }[] = [
    { key: 'created', count: result.created, className: 'bg-primary-soft text-primary' },
    { key: 'linked', count: result.linked, className: 'bg-info-soft text-info' },
    { key: 'skipped', count: result.skipped, className: 'bg-warn-soft text-warn' },
    { key: 'errored', count: result.errored, className: 'bg-danger-soft text-danger' },
  ];
  return (
    <div className="flex flex-col gap-4">
      <div>
        <p className="text-[13px] font-medium text-ink">{t('team.import.resultTitle')}</p>
        <p className="mt-0.5 text-[12px] text-muted">{t('team.import.summaryTotal', { count: result.total })}</p>
      </div>

      <div className="flex flex-wrap gap-2">
        {chips.map((c) => (
          <span
            key={c.key}
            className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[12px] font-medium ${c.className}`}
          >
            <span className="tabular-nums">{c.count}</span>
            {t(`team.import.status.${c.key}`)}
          </span>
        ))}
      </div>

      <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
        {result.rows.map((r: BulkImportResultRow) => (
          <li key={`${r.row}-${r.email}`} className="flex items-start justify-between gap-3 px-3 py-2">
            <div className="min-w-0">
              <p className="truncate text-[13px] text-ink">{r.email}</p>
              {r.message ? <p className="truncate text-[12px] text-muted">{r.message}</p> : null}
            </div>
            <ResultStatusPill status={r.status} />
          </li>
        ))}
      </ul>
    </div>
  );
}
