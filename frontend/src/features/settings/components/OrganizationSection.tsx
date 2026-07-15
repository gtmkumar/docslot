// Organization section: a read-only facility identity block (type, specialty focus,
// HFR linkage) plus an editable weekly Business-hours grid. Editing gates on
// tenant.settings.update — without it the grid renders disabled and the Save bar is
// hidden. Save PATCHes { businessHours } with all 7 canonical keys (mon..sun); a
// day marked Closed sends { open:null, close:null, closed:true }. Client-validates
// open < close for open days. Zero hex — tokens only.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Building2, Info } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Toggle } from '@/components/ui/Toggle';
import { TextInput } from '@/components/ui/Field';
import { toUserError } from '@/lib/backend';
import type { BusinessHours, Settings } from '@/lib/mock/contracts';
import { useUpdateSettings } from '../api';
import { SectionCard } from './SectionCard';

const DAY_ORDER = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const;
type Day = (typeof DAY_ORDER)[number];

// A fixed reference week (2024-01-01 is a Monday) → localized weekday names via Intl,
// so we don't hand-maintain 7×2 day-name strings and Hindi comes for free.
const DAY_REF: Record<Day, string> = {
  mon: '2024-01-01',
  tue: '2024-01-02',
  wed: '2024-01-03',
  thu: '2024-01-04',
  fri: '2024-01-05',
  sat: '2024-01-06',
  sun: '2024-01-07',
};
function dayLabel(day: Day, lang: string): string {
  return new Intl.DateTimeFormat(lang.startsWith('hi') ? 'hi-IN' : 'en-IN', { weekday: 'long' }).format(
    new Date(DAY_REF[day]),
  );
}

interface DayDraft {
  open: string;
  close: string;
  closed: boolean;
}
type HoursDraft = Record<Day, DayDraft>;

/** Normalize the wire map (a possibly-partial subset of mon..sun) into a full 7-day
 *  draft. An absent day is treated as closed. */
function toDraft(hours: BusinessHours): HoursDraft {
  return DAY_ORDER.reduce((acc, day) => {
    const d = hours[day];
    acc[day] = { open: d?.open ?? '', close: d?.close ?? '', closed: d?.closed ?? !d };
    return acc;
  }, {} as HoursDraft);
}

/** Build the full canonical payload (all 7 keys) from the draft. */
function toPayload(draft: HoursDraft): BusinessHours {
  return DAY_ORDER.reduce((acc, day) => {
    const d = draft[day];
    acc[day] = d.closed ? { open: null, close: null, closed: true } : { open: d.open, close: d.close, closed: false };
    return acc;
  }, {} as BusinessHours);
}

/** Per-day validity: an open day needs both times, and open must be before close. */
function dayError(d: DayDraft): 'incomplete' | 'range' | null {
  if (d.closed) return null;
  if (!d.open || !d.close) return 'incomplete';
  if (d.open >= d.close) return 'range';
  return null;
}

export function OrganizationSection({ settings, canUpdate }: { settings: Settings; canUpdate: boolean }) {
  const { t, i18n } = useTranslation();
  const update = useUpdateSettings();
  const [draft, setDraft] = useState<HoursDraft>(() => toDraft(settings.businessHours));

  const baseline = toDraft(settings.businessHours);
  const dirty = JSON.stringify(draft) !== JSON.stringify(baseline);
  const errors = DAY_ORDER.map((day) => dayError(draft[day]));
  const hasError = errors.some((e) => e !== null);
  const saveDisabled = !canUpdate || !dirty || hasError || update.isPending;

  const setDay = (day: Day, patch: Partial<DayDraft>) =>
    setDraft((prev) => ({ ...prev, [day]: { ...prev[day], ...patch } }));

  const onSave = async () => {
    if (saveDisabled) return;
    try {
      const saved = await update.mutateAsync({ businessHours: toPayload(draft) });
      // Re-seed the draft from the server's normalized hours (e.g. a closed day drops any
      // retained times) so the dirty check clears and the Save bar hides.
      setDraft(toDraft(saved.businessHours));
      toast.success(t('settings.organization.saved'));
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const hfrLinked = Boolean(settings.hfr.id);

  return (
    <SectionCard
      anchorId="organization"
      icon={<Building2 size={16} aria-hidden="true" />}
      title={t('settings.organization.title')}
      caption={t('settings.organization.caption')}
    >
      {/* Read-only identity */}
      <dl className="grid gap-4 sm:grid-cols-3">
        <IdentityItem label={t('settings.organization.facilityType')} value={settings.facilityType} />
        <IdentityItem
          label={t('settings.organization.specialtyFocus')}
          value={settings.specialtyFocus ?? t('settings.organization.notSet')}
        />
        <div>
          <dt className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('settings.organization.hfr')}
          </dt>
          <dd>
            {hfrLinked ? (
              <span className="inline-flex items-center gap-1.5">
                <span className="mono text-[13px] text-ink">{settings.hfr.id}</span>
                {settings.hfr.status ? (
                  <span className="inline-flex items-center rounded-full bg-primary-soft px-2 py-0.5 text-[11px] font-medium text-primary">
                    {settings.hfr.status}
                  </span>
                ) : null}
              </span>
            ) : (
              <span className="text-[13px] text-muted">{t('settings.organization.notSet')}</span>
            )}
          </dd>
        </div>
      </dl>

      {/* Business hours grid */}
      <div className="mt-5 border-t border-line pt-4">
        <h3 className="text-[13px] font-semibold text-ink">{t('settings.organization.businessHours')}</h3>
        <p className="mt-0.5 text-[12px] text-muted">{t('settings.organization.businessHoursCaption')}</p>

        <ul className="mt-3 flex flex-col divide-y divide-line">
          {DAY_ORDER.map((day, i) => {
            const d = draft[day];
            const err = errors[i];
            return (
              <li key={day} className="py-2.5">
                <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
                  <span className="w-24 shrink-0 text-[13px] font-medium text-ink">{dayLabel(day, i18n.language)}</span>

                  {d.closed ? (
                    <span className="flex-1 text-[13px] text-muted-2">{t('settings.organization.closed')}</span>
                  ) : (
                    <div className="flex flex-1 items-center gap-2">
                      <label className="sr-only" htmlFor={`hours-${day}-open`}>
                        {t('settings.organization.open')}
                      </label>
                      <TextInput
                        id={`hours-${day}-open`}
                        type="time"
                        className="mono w-28"
                        disabled={!canUpdate}
                        value={d.open}
                        onChange={(e) => setDay(day, { open: e.target.value })}
                        aria-invalid={err !== null}
                      />
                      <span aria-hidden="true" className="text-muted-2">
                        –
                      </span>
                      <label className="sr-only" htmlFor={`hours-${day}-close`}>
                        {t('settings.organization.close')}
                      </label>
                      <TextInput
                        id={`hours-${day}-close`}
                        type="time"
                        className="mono w-28"
                        disabled={!canUpdate}
                        value={d.close}
                        onChange={(e) => setDay(day, { close: e.target.value })}
                        aria-invalid={err !== null}
                      />
                    </div>
                  )}

                  <div className="flex items-center gap-2">
                    <Toggle
                      id={`hours-${day}-closed`}
                      checked={d.closed}
                      disabled={!canUpdate}
                      onChange={(v) => setDay(day, { closed: v })}
                      label={t('settings.organization.closed')}
                    />
                    <label htmlFor={`hours-${day}-closed`} className="text-[12px] text-muted">
                      {t('settings.organization.closed')}
                    </label>
                  </div>
                </div>
                {err ? (
                  <p role="alert" className="mt-1 pl-24 text-[12px] text-danger">
                    {err === 'range'
                      ? t('settings.organization.invalidRange')
                      : t('settings.organization.incompleteRange')}
                  </p>
                ) : null}
              </li>
            );
          })}
        </ul>
      </div>

      {/* Save bar — only when the caller can edit and has pending changes. */}
      {canUpdate && dirty ? (
        <div className="mt-4 flex items-center justify-between gap-3 border-t border-line pt-4">
          <span className="flex items-center gap-1.5 text-[12px] text-muted">
            <Info size={13} aria-hidden="true" />
            {hasError ? t('settings.organization.invalidRange') : t('team.security.unsaved')}
          </span>
          <div className="flex shrink-0 gap-2">
            <Button
              variant="ghost"
              size="sm"
              disabled={update.isPending}
              onClick={() => setDraft(toDraft(settings.businessHours))}
            >
              {t('settings.discard')}
            </Button>
            <Button variant="primary" size="sm" disabled={saveDisabled} onClick={() => void onSave()}>
              {t('settings.organization.save')}
            </Button>
          </div>
        </div>
      ) : null}
    </SectionCard>
  );
}

function IdentityItem({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{label}</dt>
      <dd className="text-[13px] text-ink">{value}</dd>
    </div>
  );
}
