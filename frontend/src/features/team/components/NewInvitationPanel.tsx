// New invitation slide-over (#89) — token-based tenant onboarding, a NEW capability
// ALONGSIDE the direct-add invite (InviteUserPanel). Collects an email + an OPTIONAL
// role, POSTs to mint a single-use invitation, and on success hands the ONE-TIME
// token to the reveal panel (invitationToken) so the admin can copy the link
// (automated email/WhatsApp delivery lands in #93).
//
// react-hook-form + zod (dependency-free resolver). POST carries a stable
// Idempotency-Key. Gated upstream by tenant.users.create. The role is optional —
// the invitee gets no tenant role until accept when omitted; only tenant-scoped
// roles are offered (a platform role is a cross-tenant, super-only concern the DB
// would reject, and the actor may only confer a role they hold — R3, enforced server-side).

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useUI } from '@/stores/ui';
import { useCreateInvitation, useRoles } from '../api';

const schema = z.object({
  email: z.string().trim().email('email'),
  // Optional — an invite with no role links no tenant membership until accept.
  roleId: z.string().optional().default(''),
});
type InvitationForm = z.infer<typeof schema>;

export function NewInvitationPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: roles } = useRoles();
  const createInvitation = useCreateInvitation();
  const openPanel = useUI((s) => s.openPanel);

  // Only tenant-scoped roles are conferrable from this tenant-admin surface.
  const assignableRoles = roles?.filter((r) => r.scope === 'tenant') ?? [];

  const { register, handleSubmit, formState } = useForm<InvitationForm>({
    defaultValues: { email: '', roleId: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const result = await createInvitation.mutateAsync({
        email: values.email,
        roleId: values.roleId || null,
        idempotencyKey: idempotencyKey(),
      });
      // Hand the ONE-TIME token to the reveal panel (replaces this panel). The token
      // is never toasted, cached, or URL-encoded — only the reveal panel carries it.
      openPanel({ type: 'invitationToken', result, email: values.email });
    } catch (e) {
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof InvitationForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`team.validation.${m}`) : undefined;
  };

  const formId = 'new-invitation-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.invites.newInvite.eyebrow')}
      title={t('team.invites.newInvite.title')}
      description={t('team.invites.newInvite.description')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="primary"
            size="md"
            type="submit"
            form={formId}
            disabled={createInvitation.isPending}
          >
            {t('team.invites.newInvite.create')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <p className="text-[13px] text-muted">{t('team.invites.newInvite.description')}</p>

        <FieldShell label={t('team.invites.newInvite.email')} htmlFor="ni-email" error={errKey('email')}>
          <TextInput
            id="ni-email"
            type="email"
            autoFocus
            placeholder={t('team.invites.newInvite.emailPlaceholder')}
            {...register('email')}
            aria-invalid={Boolean(formState.errors.email)}
          />
        </FieldShell>

        <div>
          <label htmlFor="ni-role" className={labelClass}>
            {t('team.invites.newInvite.role')}{' '}
            <span className="font-normal text-muted-2">({t('team.invites.newInvite.roleOptional')})</span>
          </label>
          <Select id="ni-role" {...register('roleId')}>
            <option value="">{t('team.invites.newInvite.noRole')}</option>
            {assignableRoles.map((r) => (
              <option key={r.roleId} value={r.roleId}>
                {r.name}
              </option>
            ))}
          </Select>
        </div>

        <p className="text-[12px] text-muted">{t('team.invites.newInvite.firstLoginNote')}</p>
      </form>
    </SlideOver>
  );
}
