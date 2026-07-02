// Live Rx preview — a letterhead-styled prescription that mirrors what the patient
// receives. Marked `.rx-print` so Print / Save PDF (window.print) isolate just this
// card. Tokens only; Hindi content renders with the Devanagari font.

import { useTranslation } from 'react-i18next';
import { formatMedicationLine } from '@/lib/mock/contracts';
import type { ConsultForm } from '../model';

const DEVA = /[ऀ-ॿ]/;
const deva = (s: string) => (DEVA.test(s) ? 'deva' : '');

export function RxPreview({
  doctorName,
  doctorQual,
  clinicName,
  clinicLocation,
  patientName,
  patientMeta,
  dateLabel,
  form,
}: {
  doctorName: string;
  doctorQual: string | null;
  clinicName: string;
  clinicLocation: string | null;
  patientName: string;
  patientMeta: string;
  dateLabel: string;
  form: ConsultForm;
}) {
  const { t } = useTranslation();

  const vitalsBits: string[] = [];
  if (form.vitals.bp.trim()) vitalsBits.push(`${t('consult.vitals.bp')} ${form.vitals.bp.trim()}`);
  if (form.vitals.pulse.trim()) vitalsBits.push(`${t('consult.vitals.pulse')} ${form.vitals.pulse.trim()}`);
  if (form.vitals.temp.trim()) vitalsBits.push(`${t('consult.vitals.temp')} ${form.vitals.temp.trim()}°F`);
  if (form.vitals.spo2.trim()) vitalsBits.push(`${t('consult.vitals.spo2')} ${form.vitals.spo2.trim()}%`);
  if (form.vitals.weight.trim()) vitalsBits.push(`${t('consult.vitals.weight')} ${form.vitals.weight.trim()}kg`);

  const adviceLines = [...form.adviceChips, form.adviceText.trim()].map((s) => s.trim()).filter(Boolean);
  const diagnosis = form.diagnoses.join(', ');
  const hasContent = diagnosis.length > 0 || form.medications.length > 0;

  return (
    <div className="rx-print flex flex-col rounded-[var(--radius)] border border-line bg-surface p-5 shadow-[var(--shadow-sm)]">
      {/* Letterhead */}
      <header className="flex items-start justify-between gap-3 border-b-2 border-primary pb-3">
        <div className="min-w-0">
          <p className="text-base font-semibold text-primary">{doctorName}</p>
          {doctorQual ? <p className="text-[12px] text-muted">{doctorQual}</p> : null}
          <p className="text-[12px] text-muted">
            {clinicName}
            {clinicLocation ? ` · ${clinicLocation}` : ''}
          </p>
        </div>
        <div className="flex flex-col items-end gap-1">
          <span className="flex h-9 w-9 items-center justify-center rounded-[var(--radius-sm)] bg-ink text-[15px] font-semibold text-bg" aria-hidden="true">
            ℞
          </span>
          <span className="mono text-[11px] text-muted-2">{dateLabel}</span>
        </div>
      </header>

      {/* Patient line + vitals */}
      <div className="border-b border-line py-2.5">
        <p className={`text-[13px] font-semibold text-ink ${deva(patientName)}`}>
          {patientName} <span className="font-normal text-muted">· {patientMeta}</span>
        </p>
        {vitalsBits.length > 0 ? <p className="mono mt-1 text-[11px] text-muted">{vitalsBits.join(' · ')}</p> : null}
      </div>

      {!hasContent ? (
        <div className="flex flex-1 flex-col items-center justify-center gap-2 py-16 text-center">
          <span aria-hidden="true" className="text-2xl text-muted-2">℞</span>
          <p className="max-w-xs text-[13px] text-muted-2">{t('consult.preview.placeholder')}</p>
        </div>
      ) : (
        <div className="flex flex-col gap-4 py-3">
          {diagnosis ? (
            <section>
              <h4 className="text-[10px] font-semibold uppercase tracking-wider text-muted-2">{t('consult.preview.diagnosis')}</h4>
              <p className={`mt-0.5 text-[14px] font-medium text-ink ${deva(diagnosis)}`}>{diagnosis}</p>
            </section>
          ) : null}

          {form.medications.length > 0 ? (
            <section>
              <span className="text-lg font-semibold text-primary" aria-hidden="true">℞</span>
              <ol className="mt-1 flex flex-col gap-2">
                {form.medications.map((m, i) => (
                  <li key={i} className="flex gap-2">
                    <span className="mono text-[12px] text-muted-2">{i + 1}.</span>
                    <div className="min-w-0">
                      <p className={`text-[13px] text-ink ${deva(m.name)}`}>
                        <span className="font-semibold">{m.name}</span>
                        {m.strength ? <span className="text-muted"> ({m.strength}{m.form ? ` ${m.form}` : ''})</span> : null}
                      </p>
                      <p className="mono text-[12px] text-muted">{formatMedicationLine(m, t)}</p>
                    </div>
                  </li>
                ))}
              </ol>
            </section>
          ) : null}

          {form.investigations.length > 0 ? (
            <section>
              <h4 className="text-[10px] font-semibold uppercase tracking-wider text-muted-2">{t('consult.preview.investigations')}</h4>
              <ul className="mt-1 flex flex-wrap gap-1.5">
                {form.investigations.map((inv) => (
                  <li key={inv} className={`inline-flex items-center rounded-full bg-info-soft px-2 py-0.5 text-[12px] text-info ${deva(inv)}`}>
                    {inv}
                  </li>
                ))}
              </ul>
            </section>
          ) : null}

          {adviceLines.length > 0 ? (
            <section>
              <h4 className="text-[10px] font-semibold uppercase tracking-wider text-muted-2">{t('consult.preview.advice')}</h4>
              <ul className="mt-1 flex list-disc flex-col gap-0.5 pl-4">
                {adviceLines.map((a) => (
                  <li key={a} className={`text-[13px] text-ink ${deva(a)}`}>
                    {a}
                  </li>
                ))}
              </ul>
            </section>
          ) : null}

          {form.followUpInDays != null ? (
            <div className="inline-flex w-fit items-center gap-1.5 rounded-full bg-accent-soft px-3 py-1 text-[12px] font-medium text-accent">
              {t('consult.preview.followUp')}:{' '}
              {form.followUpInDays === 0 ? t('consult.followUp.sos') : t('consult.durationValue', { count: form.followUpInDays })}
            </div>
          ) : null}
        </div>
      )}

      {/* Signature block */}
      <footer className="mt-auto flex items-end justify-between gap-3 border-t border-line pt-3">
        <p className="max-w-[60%] text-[10px] text-muted-2">{t('consult.preview.digitallySigned')}</p>
        <div className="text-right">
          <p className={`text-[15px] italic text-ink ${deva(doctorName)}`}>{doctorName}</p>
          <p className="mt-0.5 border-t border-line-strong pt-0.5 text-[10px] text-muted-2">{t('consult.preview.signature')}</p>
        </div>
      </footer>
    </div>
  );
}
