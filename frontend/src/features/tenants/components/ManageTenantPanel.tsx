// Manage-tenant slide-over (the PRIMARY CRUD modality — not a centered modal).
// URL-addressable via ?panel=manageTenant&id=<tenantId>; focus-trap + Esc/overlay
// close owned by SlideOver + SlideOverHost. Two SEPARATE concerns:
//
//  1. Lifecycle (suspend / reactivate) — a distinct, more-privileged action gated on
//     `platform.tenants.suspend`. Suspending blocks a clinic's sign-in + bookings, so it
//     is an explicit danger-confirm with a MANDATORY reason (audited server-side), NOT a
//     toggle buried in the edit form. Mirrors the People-tab account lifecycle cluster.
//  2. Edit — contact/display fields only, gated on `platform.tenants.update`. `tenantCode`
//     + `tenantType` are IMMUTABLE (they scope menus + billing) and shown READ-ONLY; the
//     lifecycle `status` is deliberately NOT editable here.
//
// Both gates are in-memory can() checks, never a role branch. The header + status render
// instantly from the shared ['tenants','list'] cache (useTenants); the EDIT FORM pre-fills
// from GET /tenants/{id} (useTenant → the full detail DTO with legalName/primaryPhone/
// state/pinCode) behind a loading skeleton, so every field re-syncs from the server.
// Mutations carry a stable Idempotency-Key; on success the list + detail caches invalidate.

import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { ShieldAlert } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { FieldShell, Select, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import { useTenants } from '@/features/impersonation/api';
import type { TenantDetail } from '@/lib/mock/contracts';
import { useSetTenantSuspension, useTenant, useUpdateTenant } from '../api';
import { usePincodeGeo } from '../usePincodeGeo';
import { INDIA_STATES } from '../india-geo';
import { TenantStatusChip } from './TenantStatusChip';

// Free-text escape for the city select, mirroring NewTenantPanel: a town not in the
// (district-level, non-exhaustive) reference list switches to a plain text input.
const CITY_OTHER = '__other__';

const schema = z.object({
  displayName: z.string().trim().min(1, 'displayName').max(255),
  legalName: z.string().trim().min(1, 'legalName').max(255),
  primaryEmail: z.string().trim().email('primaryEmail'),
  primaryPhone: z.string().trim().min(8, 'primaryPhone').max(20),
  state: z.string().trim().max(100).optional().default(''),
  city: z.string().trim().max(100).optional().default(''),
  cityOther: z.string().trim().max(100).optional().default(''),
  pinCode: z
    .string()
    .trim()
    .regex(/^$|^[1-9][0-9]{5}$/, 'pinCode')
    .optional()
    .default(''),
});
const schemaWithCityEscape = schema.refine((v) => v.city !== CITY_OTHER || v.cityOther.length > 0, {
  path: ['cityOther'],
  message: 'cityOther',
});
type TenantForm = z.infer<typeof schema>;

const EMPTY_FORM: TenantForm = {
  displayName: '', legalName: '', primaryEmail: '', primaryPhone: '',
  state: '', city: '', cityOther: '', pinCode: '',
};

/** Map a detail row's state + city onto the india-geo cascade values: the state Select
 *  needs a matching INDIA_STATES name and the city Select a matching option (else the
 *  CITY_OTHER free-text escape). Falls back to a city-only reverse lookup when the stored
 *  state isn't in the reference list, so an existing city is never lost. */
function geoFromDetail(state: string | null | undefined, city: string | null | undefined) {
  const st = (state ?? '').trim();
  const c = (city ?? '').trim();
  const match = INDIA_STATES.find((s) => s.name.toLowerCase() === st.toLowerCase());
  if (match) {
    const cityInList = match.cities.find((cc) => cc.toLowerCase() === c.toLowerCase());
    if (cityInList) return { state: match.name, city: cityInList, cityOther: '' };
    if (c) return { state: match.name, city: CITY_OTHER, cityOther: c };
    return { state: match.name, city: '', cityOther: '' };
  }
  // Stored state unknown/blank → recover from the city string alone.
  if (!c) return { state: '', city: '', cityOther: '' };
  const byCity = INDIA_STATES.find((s) => s.cities.some((cc) => cc.toLowerCase() === c.toLowerCase()));
  if (byCity) {
    const m = byCity.cities.find((cc) => cc.toLowerCase() === c.toLowerCase()) as string;
    return { state: byCity.name, city: m, cityOther: '' };
  }
  return { state: '', city: CITY_OTHER, cityOther: c };
}

function formFromDetail(d: TenantDetail): TenantForm {
  const g = geoFromDetail(d.state, d.city);
  return {
    displayName: d.displayName,
    legalName: d.legalName,
    primaryEmail: d.primaryEmail,
    primaryPhone: d.primaryPhone,
    state: g.state,
    city: g.city,
    cityOther: g.cityOther,
    pinCode: d.pinCode ?? '',
  };
}

export function ManageTenantPanel({
  tenantId,
  open,
  onClose,
}: {
  tenantId: string;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const canEdit = can('platform.tenants.update');
  const canSuspend = can('platform.tenants.suspend');

  // Header/status render instantly from the list cache; the edit form pre-fills from the
  // detail fetch (gated platform.tenants.read, same as reaching this screen).
  const { data: tenants } = useTenants();
  const listRow = tenants?.find((tn) => tn.tenantId === tenantId);
  const detailQuery = useTenant(tenantId, open);
  const detail = detailQuery.data;
  const view = detail ?? listRow;

  const updateTenant = useUpdateTenant();

  const { register, handleSubmit, formState, watch, setValue, getValues, reset } = useForm<TenantForm>({
    defaultValues: EMPTY_FORM,
    resolver: async (values) => {
      const parsed = schemaWithCityEscape.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  // Pre-fill (and re-sync after an edit invalidates the detail) once the detail lands.
  useEffect(() => {
    if (detail) reset(formFromDetail(detail));
  }, [detail, reset]);

  // PIN-code auto-fill + centroid capture, shared with NewTenantPanel. A new lookup
  // autofills state/city and holds the centroid; on submit the geo tag is the lookup's
  // centroid when its PIN matches, otherwise the clinic's EXISTING geo is kept (never wiped
  // by a routine contact edit).
  const { pinStatus, pinLookup, onPinChange } = usePincodeGeo(
    {
      setField: (name, value) => setValue(name, value, { shouldDirty: true }),
      getField: (name) => getValues(name),
    },
    CITY_OTHER,
  );

  // The state select drives the city options; CITY_OTHER swaps the city select for a
  // free-text input (reference list is district-level, not exhaustive). Mirrors NewTenantPanel.
  const stateSel = watch('state');
  const citySel = watch('city');
  const stateCities = INDIA_STATES.find((s) => s.name === stateSel)?.cities ?? [];

  const onSubmit = handleSubmit(async (values) => {
    // Geo: use the fresh lookup centroid when it belongs to the submitted PIN; otherwise
    // keep the clinic's existing geo (from the detail) rather than clearing it.
    const geoTag =
      pinLookup?.pinCode === values.pinCode
        ? { latitude: pinLookup.latitude, longitude: pinLookup.longitude }
        : { latitude: detail?.latitude ?? null, longitude: detail?.longitude ?? null };
    try {
      await updateTenant.mutateAsync({
        tenantId,
        request: {
          displayName: values.displayName,
          legalName: values.legalName,
          primaryEmail: values.primaryEmail,
          primaryPhone: values.primaryPhone,
          city: (values.city === CITY_OTHER ? values.cityOther : values.city) || null,
          state: values.state || null,
          pinCode: values.pinCode || null,
          latitude: geoTag.latitude,
          longitude: geoTag.longitude,
        },
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('tenants.manage.saved'));
      onClose();
    } catch (e) {
      // 403 (missing permission) / 404 / validation surface via toUserError.
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof TenantForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`tenants.manage.validation.${m}`) : undefined;
  };

  const formId = 'manage-tenant-form';
  const canSave = canEdit && detailQuery.isSuccess;

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('tenants.manage.eyebrow')}
      title={view?.displayName ?? t('tenants.manage.title')}
      description={t('tenants.manage.description')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {canEdit ? t('common.cancel') : t('common.close')}
          </Button>
          {canEdit ? (
            <Button variant="primary" size="md" type="submit" form={formId} disabled={!canSave || updateTenant.isPending}>
              {updateTenant.isPending ? t('tenants.manage.saving') : t('tenants.manage.save')}
            </Button>
          ) : null}
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {/* Lifecycle — read-first status + the gated suspend/reactivate action. OUTSIDE
            the edit form (its own buttons + confirm); never part of the contact save. */}
        <LifecycleSection
          tenantId={tenantId}
          status={view?.status}
          suspendedReason={detail?.suspendedReason ?? null}
          canSuspend={canSuspend}
        />

        <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
          {/* Immutable identity — code + type scope menus and billing; shown read-only. */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <span className={labelClass}>{t('tenants.manage.tenantCode')}</span>
              <p className="mono rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-sm text-muted">
                {view?.tenantCode ?? '—'}
              </p>
            </div>
            <div>
              <span className={labelClass}>{t('tenants.manage.tenantType')}</span>
              <p className="rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-sm text-muted">
                {view ? t(`tenants.types.${view.tenantType}`, { defaultValue: view.tenantType }) : '—'}
              </p>
            </div>
          </div>
          <p className="-mt-2 text-[11px] text-muted-2">{t('tenants.manage.immutableHint')}</p>

          {/* Edit fields — three states over the detail fetch: loading / error / ready. */}
          {detailQuery.isLoading ? (
            <div className="flex flex-col gap-4" role="status" aria-busy="true" aria-label={t('common.loading')}>
              {Array.from({ length: 5 }).map((_, i) => (
                <div key={i} className="flex flex-col gap-1.5">
                  <Skeleton className="h-3 w-24" />
                  <Skeleton className="h-9 w-full rounded-[var(--radius-sm)]" />
                </div>
              ))}
            </div>
          ) : detailQuery.isError ? (
            <div className="flex items-center justify-between gap-2 rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2.5 text-[12px] text-muted">
              <span>{t('tenants.manage.detailError')}</span>
              <button
                type="button"
                onClick={() => void detailQuery.refetch()}
                className="font-medium text-primary underline hover:no-underline focus-visible:outline-none"
              >
                {t('common.retry')}
              </button>
            </div>
          ) : (
            <>
              <FieldShell label={t('tenants.manage.displayName')} htmlFor="mt-display" error={errKey('displayName')}>
                <TextInput
                  id="mt-display"
                  autoFocus={canEdit}
                  disabled={!canEdit}
                  {...register('displayName')}
                  aria-invalid={Boolean(formState.errors.displayName)}
                />
              </FieldShell>

              <FieldShell label={t('tenants.manage.legalName')} htmlFor="mt-legal" error={errKey('legalName')}>
                <TextInput
                  id="mt-legal"
                  disabled={!canEdit}
                  placeholder={t('tenants.manage.legalNamePlaceholder')}
                  {...register('legalName')}
                  aria-invalid={Boolean(formState.errors.legalName)}
                />
              </FieldShell>

              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <FieldShell label={t('tenants.manage.primaryEmail')} htmlFor="mt-email" error={errKey('primaryEmail')}>
                  <TextInput
                    id="mt-email"
                    type="email"
                    disabled={!canEdit}
                    placeholder="ops@clinic.in"
                    {...register('primaryEmail')}
                    aria-invalid={Boolean(formState.errors.primaryEmail)}
                  />
                </FieldShell>

                <FieldShell label={t('tenants.manage.primaryPhone')} htmlFor="mt-phone" error={errKey('primaryPhone')}>
                  <TextInput
                    id="mt-phone"
                    type="tel"
                    inputMode="tel"
                    className="mono"
                    disabled={!canEdit}
                    placeholder="+91 98200 11223"
                    {...register('primaryPhone')}
                    aria-invalid={Boolean(formState.errors.primaryPhone)}
                  />
                </FieldShell>
              </div>

              {/* State → city cascade over the shared india-geo reference (same idiom as
                  NewTenantPanel). Selects are controlled (value from watch) so programmatic
                  writes land even when the target option renders in the same commit. */}
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <FieldShell label={t('tenants.manage.state')} htmlFor="mt-state">
                  <Select
                    id="mt-state"
                    value={stateSel}
                    disabled={!canEdit}
                    {...register('state', {
                      // A new state invalidates the previous city choice.
                      onChange: () => setValue('city', '', { shouldDirty: true }),
                    })}
                  >
                    <option value="">{t('tenants.new.statePlaceholder')}</option>
                    {INDIA_STATES.map((s) => (
                      <option key={s.name} value={s.name}>
                        {s.name}
                      </option>
                    ))}
                  </Select>
                </FieldShell>

                <FieldShell label={t('tenants.manage.city')} htmlFor="mt-city">
                  <Select id="mt-city" value={citySel} disabled={!canEdit || !stateSel} {...register('city')}>
                    <option value="">
                      {stateSel ? t('tenants.new.cityPlaceholder') : t('tenants.new.cityNeedsState')}
                    </option>
                    {stateCities.map((c) => (
                      <option key={c} value={c}>
                        {c}
                      </option>
                    ))}
                    {stateSel ? <option value={CITY_OTHER}>{t('tenants.new.cityOtherOption')}</option> : null}
                  </Select>
                </FieldShell>
              </div>

              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                {citySel === CITY_OTHER ? (
                  <FieldShell label={t('tenants.new.cityOther')} htmlFor="mt-city-other" error={errKey('cityOther')}>
                    <TextInput
                      id="mt-city-other"
                      disabled={!canEdit}
                      placeholder={t('tenants.new.cityOtherPlaceholder')}
                      aria-invalid={Boolean(formState.errors.cityOther)}
                      {...register('cityOther')}
                    />
                  </FieldShell>
                ) : null}
                <FieldShell label={t('tenants.manage.pinCode')} htmlFor="mt-pin" error={errKey('pinCode')}>
                  <TextInput
                    id="mt-pin"
                    inputMode="numeric"
                    maxLength={6}
                    disabled={!canEdit}
                    placeholder="854301"
                    aria-invalid={Boolean(formState.errors.pinCode)}
                    {...register('pinCode', { onChange: (e) => void onPinChange(String(e.target.value)) })}
                  />
                  {/* PIN lookup status + geo: the fresh lookup wins; otherwise the clinic's
                      stored centroid is shown so the admin sees the current geo tag. */}
                  <p className="mt-1.5 text-[12px] text-muted" aria-live="polite">
                    {pinStatus === 'looking'
                      ? t('tenants.new.pinLooking')
                      : pinStatus === 'notFound'
                        ? t('tenants.new.pinNotFound')
                        : pinStatus === 'found' && pinLookup
                          ? `${pinLookup.district}, ${pinLookup.state}` +
                            (pinLookup.latitude != null ? ` · ${t('tenants.new.pinGeoTagged')}` : '')
                          : detail?.latitude != null && detail?.longitude != null
                            ? t('tenants.manage.geoTagged', {
                                lat: detail.latitude.toFixed(5),
                                long: detail.longitude.toFixed(5),
                              })
                            : t('tenants.new.pinHint')}
                  </p>
                </FieldShell>
              </div>
            </>
          )}
        </form>
      </div>
    </SlideOver>
  );
}

// ── Lifecycle: read-only status chip + gated suspend/reactivate danger-confirm ──────
// Mirrors ManageUserPanel's AccountSection: a single inline confirm with a MANDATORY
// reason (suspend only). Reactivation is restorative — confirmed, but no reason required.
function LifecycleSection({
  tenantId,
  status,
  suspendedReason,
  canSuspend,
}: {
  tenantId: string;
  status?: string | null;
  suspendedReason?: string | null;
  canSuspend: boolean;
}) {
  const { t } = useTranslation();
  const suspension = useSetTenantSuspension();

  const isSuspended = status === 'suspended';
  const isSuspendAction = !isSuspended; // active → the available action is "suspend"

  const [confirming, setConfirming] = useState(false);
  const [reason, setReason] = useState('');
  const [reasonTouched, setReasonTouched] = useState(false);
  const reasonMissing = reason.trim().length === 0;

  const resetConfirm = () => {
    setConfirming(false);
    setReason('');
    setReasonTouched(false);
  };

  const onConfirm = () => {
    // Reason is MANDATORY when suspending; reactivation needs none.
    if (isSuspendAction) {
      setReasonTouched(true);
      if (reasonMissing) return;
    }
    // OPTIMISTIC: the hook flips the status chip + list row instantly, so the confirm
    // closes at once; on failure everything snaps back and the revert is toasted.
    suspension.mutate(
      {
        tenantId,
        // isActive:false → /suspend (reason mandatory); isActive:true → /reactivate.
        isActive: !isSuspendAction,
        reason: isSuspendAction ? reason.trim() : null,
        idempotencyKey: idempotencyKey(),
      },
      { onError: (e) => toast.error(t('common.reverted', { error: toUserError(e) })) },
    );
    toast.success(isSuspendAction ? t('tenants.manage.suspendedToast') : t('tenants.manage.reactivatedToast'));
    resetConfirm();
  };

  return (
    <section>
      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
        {t('tenants.manage.lifecycleHeading')}
      </h3>

      <div className="flex flex-col gap-2 rounded-[var(--radius-sm)] border border-line px-3 py-2.5">
        <div className="flex items-center justify-between gap-2">
          <TenantStatusChip status={status} />
          {canSuspend && !confirming ? (
            <Button
              variant={isSuspended ? 'ghost' : 'danger'}
              size="sm"
              disabled={suspension.isPending}
              onClick={() => {
                setReason('');
                setReasonTouched(false);
                setConfirming(true);
              }}
            >
              {isSuspended ? t('tenants.manage.reactivate') : t('tenants.manage.suspend')}
            </Button>
          ) : null}
        </div>

        {/* When suspended, show the recorded reason (from the detail DTO). */}
        {isSuspended && suspendedReason ? (
          <p className="text-[12px] text-muted">
            <span className="text-muted-2">{t('tenants.manage.suspendedReasonLabel')}: </span>
            {suspendedReason}
          </p>
        ) : null}

        {/* Inline danger-confirm. Suspend carries a MANDATORY reason (audited). */}
        {confirming ? (
          <div className="mt-1 flex flex-col gap-3 rounded-[var(--radius)] border border-line bg-bg-2 p-3">
            <p className="flex items-start gap-2 text-[12px] text-muted">
              {isSuspendAction ? (
                <ShieldAlert size={14} aria-hidden="true" className="mt-0.5 shrink-0 text-danger" />
              ) : null}
              {isSuspendAction ? t('tenants.manage.confirmSuspend') : t('tenants.manage.confirmReactivate')}
            </p>

            {isSuspendAction ? (
              <div>
                <label htmlFor="tenant-suspend-reason" className={labelClass}>
                  {t('tenants.manage.reasonLabel')}
                </label>
                <TextArea
                  id="tenant-suspend-reason"
                  rows={2}
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  placeholder={t('tenants.manage.reasonPlaceholder')}
                  aria-invalid={reasonTouched && reasonMissing}
                />
                {reasonTouched && reasonMissing ? (
                  <p className="mt-1 text-[11px] text-danger">{t('tenants.manage.validation.reason')}</p>
                ) : null}
              </div>
            ) : null}

            <div className="flex justify-end gap-2">
              <Button variant="ghost" size="sm" onClick={resetConfirm} disabled={suspension.isPending}>
                {t('common.cancel')}
              </Button>
              <Button
                variant={isSuspendAction ? 'danger' : 'primary'}
                size="sm"
                disabled={suspension.isPending || (isSuspendAction && reasonMissing)}
                onClick={() => void onConfirm()}
              >
                {isSuspendAction ? t('tenants.manage.suspend') : t('tenants.manage.reactivate')}
              </Button>
            </div>
          </div>
        ) : null}
      </div>
    </section>
  );
}
