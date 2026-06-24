// Global support-impersonation banner (issue #3). Renders ONLY while the current
// access token carries the signed `impersonated_tenant` claim — its presence is
// the single source of truth, so a stale/cleared token hides the banner without
// any extra state. Sticky at the top of the content column, unmistakable, and
// token-driven (warn palette: acting outside your normal scope is a WARNING, not
// info). Bilingual via react-i18next; no hex literals.

import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { LogOut, ShieldAlert } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { readImpersonatedTenant } from '@/lib/jwt';
import { useSession } from '@/stores/session';
import { useEndImpersonation } from '../api';

/** Shorten a UUID for display when no human label is available (e.g. `1f5e…a0c4`). */
function shortenId(id: string): string {
  return id.length > 12 ? `${id.slice(0, 4)}…${id.slice(-4)}` : id;
}

export function ImpersonationBanner() {
  const { t } = useTranslation();
  // Read the claim off the LIVE token so the banner appears/disappears in lockstep
  // with begin/end (both swap the token through the session store).
  const accessToken = useSession((s) => s.accessToken);
  const tenants = useSession((s) => s.user?.tenants);
  const impersonationId = useSession((s) => s.impersonationId);
  const endMutation = useEndImpersonation();

  const claimTenantId = readImpersonatedTenant(accessToken);
  if (!claimTenantId) return null;

  // Prefer a human tenant name if the signed-in profile happens to list it; else
  // fall back to a shortened id (the claim is just a UUID).
  const displayName =
    tenants?.find((tenant) => tenant.tenantId === claimTenantId)?.displayName ??
    t('impersonation.actingAsTenant', { id: shortenId(claimTenantId) });

  const handleExit = () => {
    endMutation.mutate(undefined, {
      onSuccess: () => toast.success(t('impersonation.exited')),
      onError: () => toast.error(t('impersonation.exitError')),
    });
  };

  return (
    <div
      role="status"
      aria-live="polite"
      className="sticky top-0 z-30 flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-warn/40 bg-warn-soft px-4 py-2.5 text-warn shadow-[var(--shadow-sm)] sm:px-6"
    >
      <ShieldAlert size={18} aria-hidden="true" className="shrink-0" />
      <div className="min-w-0 flex-1">
        <p className="text-[13px] font-semibold leading-tight">
          {t('impersonation.actingAs', { tenant: displayName })}
        </p>
        <p className="text-[12px] leading-tight opacity-80">{t('impersonation.body')}</p>
      </div>
      <Button
        variant="ghost"
        size="sm"
        onClick={handleExit}
        // The exit needs the server-issued impersonationId; if it's missing (e.g.
        // a refresh that kept the claim but not the linkage) the action is unsafe.
        disabled={endMutation.isPending || !impersonationId}
        className="border-warn/50 text-warn hover:bg-warn/10"
      >
        <LogOut size={15} aria-hidden="true" />
        {endMutation.isPending ? t('impersonation.exiting') : t('impersonation.exit')}
      </Button>
    </div>
  );
}
