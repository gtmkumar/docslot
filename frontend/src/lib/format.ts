// Shared formatting helpers. Centralised so PHI masking + IST slot rules are
// applied consistently and are easy to audit.

/**
 * Time-of-day greeting key (Asia/Kolkata). Returns an i18n key under `greeting.*`
 * so the displayed string stays bilingual. We compute the hour in IST regardless
 * of the browser timezone — slot/clinic context is always India.
 */
export function greetingKey(now: Date = new Date()): 'morning' | 'afternoon' | 'evening' {
  const istHour = Number(
    new Intl.DateTimeFormat('en-US', {
      timeZone: 'Asia/Kolkata',
      hour: 'numeric',
      hour12: false,
    }).format(now),
  );
  if (istHour < 12) return 'morning';
  if (istHour < 17) return 'afternoon';
  return 'evening';
}

/**
 * Render a clock time as an explicit IST slot label, e.g. "11:30 IST".
 * The prototype slot times are already 24h Asia/Kolkata strings ("11:30"); we
 * never display a slot without the IST marker (REACT_SKILL timezone rule).
 */
export function istSlot(time: string): string {
  return `${time} IST`;
}

/**
 * Mask a phone number for at-a-glance views, keeping the country code and the
 * last 2 digits: "+91 98203 14572" → "+91 ····· ··572". Full numbers are only
 * revealed inside an opened detail panel where the staff action requires it.
 */
export function maskPhone(phone: string): string {
  const visibleTail = 2;
  return phone.replace(/\d/g, (digit, index, full) => {
    // Keep leading country-code digits (first 2 after the +) and the last N.
    const digitsOnly = full.replace(/\D/g, '');
    const pos = full.slice(0, index).replace(/\D/g, '').length;
    const keepHead = pos < 2;
    const keepTail = pos >= digitsOnly.length - visibleTail;
    return keepHead || keepTail ? digit : '·';
  });
}

/** Initials for an avatar fallback ("Riya Kapoor" → "RK"). */
export function initials(name: string): string {
  return name
    .split(/\s+/)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('');
}

/** Indian rupee, no decimals, grouped: 38400 → "₹38,400". */
export function inr(amount: number): string {
  return `₹${amount.toLocaleString('en-IN')}`;
}

/** Short date in IST, e.g. "14 Jun 2026". Null/invalid → em dash. */
export function shortDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return new Intl.DateTimeFormat('en-IN', {
    timeZone: 'Asia/Kolkata',
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  }).format(d);
}

/** Relative-ish day+time in IST for "last login", e.g. "14 Jun, 09:12". */
export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return new Intl.DateTimeFormat('en-IN', {
    timeZone: 'Asia/Kolkata',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(d);
}

/** Compact relative duration from now: past → "2h ago", future → "in 3d".
 *  Used for the audit last-verified line and the breach 72h clock. */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return '—';
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '—';
  const diff = t - now;
  const past = diff < 0;
  const abs = Math.abs(diff);
  const mins = Math.round(abs / 60_000);
  const hours = Math.round(abs / 3_600_000);
  const days = Math.round(abs / 86_400_000);
  let body: string;
  if (mins < 60) body = `${Math.max(1, mins)}m`;
  else if (hours < 24) body = `${hours}h`;
  else body = `${days}d`;
  return past ? `${body} ago` : `in ${body}`;
}
