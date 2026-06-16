// Upload-lab-report slide-over. Test name + structured result rows + a
// critical-findings flag. Gated upstream by docslot.report.upload. POST carries a
// stable Idempotency-Key. NOT URL-addressable (clinical write context).

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Plus, X } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useUploadLabReport } from '../api';
import type { LabResultRow } from '@/lib/mock/contracts';

const emptyRow = (): LabResultRow => ({ analyte: '', value: '', unit: null, refRange: null, flag: null });

export function UploadReportPanel({ patientId, open, onClose }: { patientId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const upload = useUploadLabReport(patientId);

  const [testName, setTestName] = useState('');
  const [critical, setCritical] = useState(false);
  const [rows, setRows] = useState<LabResultRow[]>([emptyRow()]);
  const [touched, setTouched] = useState(false);

  const filled = rows.filter((r) => r.analyte.trim().length > 0);
  const testOk = testName.trim().length > 0;
  const rowsOk = filled.length > 0;

  const setRow = (i: number, patch: Partial<LabResultRow>) =>
    setRows((cur) => cur.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));

  const onSubmit = async () => {
    setTouched(true);
    if (!testOk || !rowsOk) return;
    await upload.mutateAsync({
      bookingId: crypto.randomUUID(),
      patientId,
      testName: testName.trim(),
      results: filled,
      hasCriticalFindings: critical,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('clinical.reports.uploaded'));
    onClose();
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('clinical.reports.uploadTitle')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="button" disabled={upload.isPending} onClick={() => void onSubmit()}>
            {t('clinical.reports.submit')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <FieldShell label={t('clinical.reports.testName')} htmlFor="ur-test" error={touched && !testOk ? t('clinical.validation.testName') : undefined}>
          <TextInput id="ur-test" autoFocus value={testName} onChange={(e) => setTestName(e.target.value)} aria-invalid={touched && !testOk} />
        </FieldShell>

        <section>
          <div className="mb-2 flex items-center justify-between">
            <span className={labelClass}>{t('clinical.reports.results')}</span>
            <Button variant="ghost" size="sm" onClick={() => setRows((r) => [...r, emptyRow()])}>
              <Plus size={13} aria-hidden="true" />
              {t('clinical.reports.addRow')}
            </Button>
          </div>
          <ul className="flex flex-col gap-2">
            {rows.map((r, i) => (
              <li key={i} className="rounded-[var(--radius-sm)] border border-line p-2.5">
                <div className="flex items-center gap-2">
                  <TextInput placeholder={t('clinical.reports.analyte')} value={r.analyte} onChange={(e) => setRow(i, { analyte: e.target.value })} className="flex-1" />
                  {rows.length > 1 ? (
                    <button type="button" aria-label={t('common.close')} onClick={() => setRows((cur) => cur.filter((_, idx) => idx !== i))} className="rounded p-1 text-muted hover:bg-surface-sunk hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
                      <X size={14} aria-hidden="true" />
                    </button>
                  ) : null}
                </div>
                <div className="mt-2 grid grid-cols-2 gap-2">
                  <TextInput placeholder={t('clinical.reports.value')} value={r.value} onChange={(e) => setRow(i, { value: e.target.value })} className="text-[12px]" />
                  <TextInput placeholder={t('clinical.reports.ref')} value={r.refRange ?? ''} onChange={(e) => setRow(i, { refRange: e.target.value || null })} className="mono text-[12px]" />
                </div>
              </li>
            ))}
          </ul>
          {touched && !rowsOk ? <p role="alert" className="mt-1 text-[12px] text-danger">{t('clinical.validation.result')}</p> : null}
        </section>

        <label className="flex items-center gap-2.5 rounded-[var(--radius-sm)] border border-line px-3 py-2.5">
          <input type="checkbox" checked={critical} onChange={(e) => setCritical(e.target.checked)} className="h-4 w-4 accent-[var(--danger)]" />
          <span className="text-[13px] text-ink">{t('clinical.reports.critical')}</span>
        </label>
      </div>
    </SlideOver>
  );
}
