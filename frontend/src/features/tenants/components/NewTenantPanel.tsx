// New-clinic (tenant onboarding) slide-over — platform console, gated upstream by
// platform.tenants.create. Collects the clinic identity + the initial owner's email,
// POSTs /api/v1/tenants (clinic + one-time tenant_owner invitation in ONE server
// transaction), and on success hands the invite link to the reveal panel
// (tenantCreated). No password ever crosses this surface — the owner sets their own
// on the public /accept-invite page.
//
// react-hook-form + zod (dependency-free resolver); POST carries a stable
// Idempotency-Key. The tenantCode auto-slugs from the display name but stays editable.

import { useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { lookupPincode, toUserError } from '@/lib/backend';
import type { PincodeLookup } from '@/lib/mock/contracts';
import { useUI } from '@/stores/ui';
import { useCreateTenant } from '../api';
import { COUNTRIES, INDIA_STATES } from '../india-geo';

// Mirrors CreateTenantValidator.TenantTypes (the tenant_type vocabulary that drives
// menu filtering server-side). Labels are bilingual via i18n keys below.
const TENANT_TYPES = ['hospital', 'individual_doctor', 'pathology_lab', 'mobile_lab_operator', 'pharmacy'] as const;

// Sentinel for the city select's free-text escape: a town not in the reference list
// (INDIA_STATES is district-level, not exhaustive) switches to a plain text input.
const CITY_OTHER = '__other__';

const schema = z.object({
  displayName: z.string().trim().min(1, 'displayName').max(255),
  legalName: z.string().trim().min(1, 'legalName').max(255),
  tenantCode: z
    .string()
    .trim()
    .regex(/^[a-z0-9][a-z0-9-]{2,49}$/, 'tenantCode'),
  tenantType: z.enum(TENANT_TYPES),
  primaryEmail: z.string().trim().email('primaryEmail'),
  primaryPhone: z.string().trim().min(8, 'primaryPhone').max(20),
  pinCode: z
    .string()
    .trim()
    .regex(/^$|^[1-9][0-9]{5}$/, 'pinCode'),
  state: z.string().trim().min(1, 'state').max(100),
  city: z.string().trim().min(1, 'city').max(100),
  cityOther: z.string().trim().max(100).optional().default(''),
  adminEmail: z.string().trim().email('adminEmail'),
});
const schemaWithCityEscape = schema.refine((v) => v.city !== CITY_OTHER || v.cityOther.length > 0, {
  path: ['cityOther'],
  message: 'cityOther',
});
type TenantForm = z.infer<typeof schema>;

/** "Apollo Care · Andheri West" → "apollo-care-andheri-west" (best-effort; editable). */
function slugify(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 50);
}

export function NewTenantPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const createTenant = useCreateTenant();
  const openPanel = useUI((s) => s.openPanel);

  const { register, handleSubmit, formState, setValue, getValues, watch } = useForm<TenantForm>({
    defaultValues: {
      displayName: '',
      legalName: '',
      tenantCode: '',
      tenantType: 'hospital',
      primaryEmail: '',
      primaryPhone: '',
      pinCode: '',
      state: '',
      city: '',
      cityOther: '',
      adminEmail: '',
    },
    resolver: async (values) => {
      const parsed = schemaWithCityEscape.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  // The state select drives the city options; the CITY_OTHER sentinel swaps the city
  // select for a free-text input (reference list is district-level, not exhaustive).
  const stateSel = watch('state');
  const citySel = watch('city');
  const stateCities = INDIA_STATES.find((s) => s.name === stateSel)?.cities ?? [];

  // PIN-code auto-fill: at 6 valid digits, resolve via the postal directory and apply
  // state + city; the centroid (when known) geo-tags the clinic on submit. The lookup
  // result is kept alongside its PIN so an edited PIN never ships stale coordinates.
  const [pinStatus, setPinStatus] = useState<'idle' | 'looking' | 'found' | 'notFound'>('idle');
  const [pinLookup, setPinLookup] = useState<PincodeLookup | null>(null);
  // What the last lookup wrote — so removing/shortening the PIN un-fills EXACTLY those
  // values and never wipes a state/city the admin chose or edited themselves.
  const appliedByLookup = useRef<{ state: string; city: string; cityOther: string } | null>(null);

  const applyLookup = (r: PincodeLookup) => {
    const match = INDIA_STATES.find((s) => s.name.toLowerCase() === r.state.trim().toLowerCase());
    if (!match) return;
    setValue('state', match.name, { shouldDirty: true });
    const cityInList = match.cities.find((c) => c.toLowerCase() === r.district.trim().toLowerCase());
    if (cityInList) {
      setValue('city', cityInList, { shouldDirty: true });
      appliedByLookup.current = { state: match.name, city: cityInList, cityOther: '' };
    } else {
      setValue('city', CITY_OTHER, { shouldDirty: true });
      setValue('cityOther', r.district.trim(), { shouldDirty: true });
      appliedByLookup.current = { state: match.name, city: CITY_OTHER, cityOther: r.district.trim() };
    }
  };

  /** PIN no longer valid → revert the lookup's own writes (and only those). */
  const unapplyLookup = () => {
    const applied = appliedByLookup.current;
    appliedByLookup.current = null;
    if (!applied) return;
    const current = getValues();
    // Field-by-field: anything the admin overrode since the lookup stays untouched.
    if (current.city === applied.city) setValue('city', '', { shouldDirty: true });
    if (applied.city === CITY_OTHER && current.cityOther === applied.cityOther)
      setValue('cityOther', '', { shouldDirty: true });
    if (current.state === applied.state) setValue('state', '', { shouldDirty: true });
  };

  const onPinChange = async (raw: string) => {
    const pin = raw.trim();
    setPinLookup(null);
    if (!/^[1-9][0-9]{5}$/.test(pin)) {
      setPinStatus('idle');
      unapplyLookup();
      return;
    }
    setPinStatus('looking');
    try {
      const r = await lookupPincode(pin);
      // Ignore a slow response for a PIN the admin has already changed again.
      if (getValues('pinCode').trim() !== pin) return;
      setPinLookup(r);
      setPinStatus('found');
      applyLookup(r);
    } catch {
      if (getValues('pinCode').trim() === pin) setPinStatus('notFound');
    }
  };

  const onSubmit = handleSubmit(async (values) => {
    try {
      const result = await createTenant.mutateAsync({
        request: {
          tenantCode: values.tenantCode,
          legalName: values.legalName,
          displayName: values.displayName,
          tenantType: values.tenantType,
          primaryEmail: values.primaryEmail,
          primaryPhone: values.primaryPhone,
          city: (values.city === CITY_OTHER ? values.cityOther : values.city) || null,
          state: values.state || null,
          pinCode: values.pinCode || null,
          // Geo tag only when the coordinates belong to the PIN actually submitted.
          latitude: pinLookup?.pinCode === values.pinCode ? pinLookup.latitude : null,
          longitude: pinLookup?.pinCode === values.pinCode ? pinLookup.longitude : null,
          adminEmail: values.adminEmail,
        },
        idempotencyKey: idempotencyKey(),
      });
      // Hand the ONE-TIME invite link to the reveal panel (replaces this panel). The
      // token is never toasted, cached, or URL-encoded — only the reveal panel carries it.
      openPanel({ type: 'tenantCreated', result });
    } catch (e) {
      // 409 (code taken) / 403 / validation surface via toUserError.
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof TenantForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`tenants.new.validation.${m}`) : undefined;
  };

  const formId = 'new-tenant-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('tenants.new.eyebrow')}
      title={t('tenants.new.title')}
      description={t('tenants.new.description')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={createTenant.isPending}>
            {createTenant.isPending ? t('tenants.new.creating') : t('tenants.new.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('tenants.new.displayName')} htmlFor="nt-display" error={errKey('displayName')}>
          <TextInput
            id="nt-display"
            autoFocus
            placeholder={t('tenants.new.displayNamePlaceholder')}
            {...register('displayName', {
              onChange: (e) => {
                // Auto-slug the code from the name until the admin edits the code manually.
                if (!formState.dirtyFields.tenantCode)
                  setValue('tenantCode', slugify(String(e.target.value)), { shouldDirty: false });
              },
            })}
            aria-invalid={Boolean(formState.errors.displayName)}
          />
        </FieldShell>

        <FieldShell label={t('tenants.new.legalName')} htmlFor="nt-legal" error={errKey('legalName')}>
          <TextInput
            id="nt-legal"
            placeholder={t('tenants.new.legalNamePlaceholder')}
            {...register('legalName')}
            aria-invalid={Boolean(formState.errors.legalName)}
          />
        </FieldShell>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <FieldShell label={t('tenants.new.tenantCode')} htmlFor="nt-code" error={errKey('tenantCode')}>
            <TextInput
              id="nt-code"
              placeholder="apollo-care"
              {...register('tenantCode')}
              aria-invalid={Boolean(formState.errors.tenantCode)}
            />
          </FieldShell>

          <FieldShell label={t('tenants.new.tenantType')} htmlFor="nt-type" error={errKey('tenantType')}>
            <Select id="nt-type" {...register('tenantType')}>
              {TENANT_TYPES.map((type) => (
                <option key={type} value={type}>
                  {t(`tenants.types.${type}`)}
                </option>
              ))}
            </Select>
          </FieldShell>
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <FieldShell label={t('tenants.new.primaryEmail')} htmlFor="nt-email" error={errKey('primaryEmail')}>
            <TextInput
              id="nt-email"
              type="email"
              placeholder="ops@clinic.in"
              {...register('primaryEmail')}
              aria-invalid={Boolean(formState.errors.primaryEmail)}
            />
          </FieldShell>

          <FieldShell label={t('tenants.new.primaryPhone')} htmlFor="nt-phone" error={errKey('primaryPhone')}>
            <TextInput
              id="nt-phone"
              type="tel"
              placeholder="+91 98200 11223"
              {...register('primaryPhone')}
              aria-invalid={Boolean(formState.errors.primaryPhone)}
            />
          </FieldShell>
        </div>

        <FieldShell label={t('tenants.new.pinCode')} htmlFor="nt-pin" error={errKey('pinCode')}>
          <TextInput
            id="nt-pin"
            inputMode="numeric"
            maxLength={6}
            placeholder={t('tenants.new.pinPlaceholder')}
            aria-invalid={Boolean(formState.errors.pinCode)}
            {...register('pinCode', { onChange: (e) => void onPinChange(String(e.target.value)) })}
          />
          <p className="mt-1.5 text-[12px] text-muted" aria-live="polite">
            {pinStatus === 'looking'
              ? t('tenants.new.pinLooking')
              : pinStatus === 'notFound'
                ? t('tenants.new.pinNotFound')
                : pinStatus === 'found' && pinLookup
                  ? `${pinLookup.district}, ${pinLookup.state}` +
                    (pinLookup.latitude != null ? ` · ${t('tenants.new.pinGeoTagged')}` : '')
                  : t('tenants.new.pinHint')}
          </p>
        </FieldShell>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <FieldShell label={t('tenants.new.country')} htmlFor="nt-country">
            {/* Single-option until the platform onboards outside India (tenants.country defaults 'IN'). */}
            <Select id="nt-country" defaultValue={COUNTRIES[0]}>
              {COUNTRIES.map((c) => (
                <option key={c} value={c}>
                  {c}
                </option>
              ))}
            </Select>
          </FieldShell>

          <FieldShell label={t('tenants.new.state')} htmlFor="nt-state" error={errKey('state')}>
            {/* Controlled (value from watch): programmatic writes from the PIN lookup must land
                even when the target option is rendered in the same commit — an uncontrolled
                select silently drops a value its DOM doesn't have yet. */}
            <Select
              id="nt-state"
              value={stateSel}
              aria-invalid={Boolean(formState.errors.state)}
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
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <FieldShell label={t('tenants.new.city')} htmlFor="nt-city" error={errKey('city')}>
            <Select
              id="nt-city"
              value={citySel}
              disabled={!stateSel}
              aria-invalid={Boolean(formState.errors.city)}
              {...register('city')}
            >
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

          {citySel === CITY_OTHER ? (
            <FieldShell label={t('tenants.new.cityOther')} htmlFor="nt-city-other" error={errKey('cityOther')}>
              <TextInput
                id="nt-city-other"
                placeholder={t('tenants.new.cityOtherPlaceholder')}
                aria-invalid={Boolean(formState.errors.cityOther)}
                {...register('cityOther')}
              />
            </FieldShell>
          ) : null}
        </div>

        {/* The initial owner — receives the one-time invite link and becomes tenant_owner on accept. */}
        <div className="rounded-[var(--radius)] border border-line bg-surface-sunk px-3 py-3">
          <p className="mb-3 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('tenants.new.ownerSection')}
          </p>
          <FieldShell label={t('tenants.new.adminEmail')} htmlFor="nt-admin" error={errKey('adminEmail')}>
            <TextInput
              id="nt-admin"
              type="email"
              placeholder="admin@clinic.in"
              {...register('adminEmail')}
              aria-invalid={Boolean(formState.errors.adminEmail)}
            />
          </FieldShell>
          <p className="mt-2 text-[12px] text-muted">{t('tenants.new.ownerHint')}</p>
        </div>
      </form>
    </SlideOver>
  );
}
