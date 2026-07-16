// Shared PIN-code → geo hook for the tenant panels (onboarding NewTenantPanel + edit
// ManageTenantPanel), so the auto-fill + centroid-capture logic lives in ONE place.
//
// On a valid 6-digit PIN it resolves the postal directory (lookupPincode), auto-fills
// the state + city fields (via the caller's setField, using the CITY_OTHER free-text
// escape when the district isn't in the reference list), and holds the lookup — whose
// centroid (latitude/longitude) geo-tags the clinic on submit. Clearing/invalidating the
// PIN reverts EXACTLY the fields the last lookup wrote (never a state/city the admin
// picked themselves). Stale responses for a since-changed PIN are ignored.
//
// The hook is form-agnostic: the caller passes narrow setField/getField adapters bound to
// its react-hook-form instance, plus its CITY_OTHER sentinel. The centroid lives in the
// returned `pinLookup`; each panel maps it onto its own submit payload (they differ: new
// clinic falls back to null, edit falls back to the clinic's existing geo).

import { useRef, useState } from 'react';
import { lookupPincode } from '@/lib/backend';
import type { PincodeLookup } from '@/lib/mock/contracts';
import { INDIA_STATES } from './india-geo';

export type PinStatus = 'idle' | 'looking' | 'found' | 'notFound';

/** Narrow form adapters the hook drives — the caller binds these to its react-hook-form
 *  setValue/getValues (baking in `shouldDirty`). */
export interface PincodeGeoForm {
  setField: (name: 'state' | 'city' | 'cityOther', value: string) => void;
  getField: (name: 'state' | 'city' | 'cityOther' | 'pinCode') => string;
}

export function usePincodeGeo(form: PincodeGeoForm, cityOtherSentinel: string) {
  const [pinStatus, setPinStatus] = useState<PinStatus>('idle');
  const [pinLookup, setPinLookup] = useState<PincodeLookup | null>(null);
  // What the last lookup wrote — so removing/shortening the PIN un-fills EXACTLY those
  // values and never wipes a state/city the admin chose or edited themselves.
  const appliedByLookup = useRef<{ state: string; city: string; cityOther: string } | null>(null);

  const applyLookup = (r: PincodeLookup) => {
    const match = INDIA_STATES.find((s) => s.name.toLowerCase() === r.state.trim().toLowerCase());
    if (!match) return;
    form.setField('state', match.name);
    const cityInList = match.cities.find((c) => c.toLowerCase() === r.district.trim().toLowerCase());
    if (cityInList) {
      form.setField('city', cityInList);
      appliedByLookup.current = { state: match.name, city: cityInList, cityOther: '' };
    } else {
      form.setField('city', cityOtherSentinel);
      form.setField('cityOther', r.district.trim());
      appliedByLookup.current = { state: match.name, city: cityOtherSentinel, cityOther: r.district.trim() };
    }
  };

  /** PIN no longer valid → revert the lookup's own writes (and only those). */
  const unapplyLookup = () => {
    const applied = appliedByLookup.current;
    appliedByLookup.current = null;
    if (!applied) return;
    // Field-by-field: anything the admin overrode since the lookup stays untouched.
    if (form.getField('city') === applied.city) form.setField('city', '');
    if (applied.city === cityOtherSentinel && form.getField('cityOther') === applied.cityOther)
      form.setField('cityOther', '');
    if (form.getField('state') === applied.state) form.setField('state', '');
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
      if (form.getField('pinCode').trim() !== pin) return;
      setPinLookup(r);
      setPinStatus('found');
      applyLookup(r);
    } catch {
      if (form.getField('pinCode').trim() === pin) setPinStatus('notFound');
    }
  };

  return { pinStatus, pinLookup, onPinChange };
}
