// Webhook create/edit slide-over. Create: pick client, name, URL, event-type
// multi-select, optional signing secret → on success swaps to the one-time
// secret reveal. Edit: update name/URL/events/active (no secret reveal — the
// secret isn't rotated here). POST/PATCH carry a stable Idempotency-Key.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useUI } from '@/stores/ui';
import { useApiClients, useCreateWebhook, useEventTypes, useUpdateWebhook, useWebhooks } from '../api';

const isHttps = (url: string) => /^https:\/\/.+/.test(url.trim());

export function WebhookFormPanel({
  webhookId,
  open,
  onClose,
}: {
  /** undefined → create mode; a webhook id → edit mode. */
  webhookId?: string;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const editing = Boolean(webhookId);
  const { data: webhooks } = useWebhooks();
  const existing = webhookId ? webhooks?.find((w) => w.webhookId === webhookId) : undefined;
  const { data: clients } = useApiClients();
  const { data: eventTypes, isLoading: eventsLoading } = useEventTypes();
  const create = useCreateWebhook();
  const update = useUpdateWebhook();
  const openPanel = useUI((s) => s.openPanel);

  const [clientId, setClientId] = useState(existing?.clientId ?? '');
  const [name, setName] = useState(existing?.name ?? '');
  const [url, setUrl] = useState(existing?.url ?? '');
  const [secret, setSecret] = useState('');
  const [active, setActive] = useState(existing?.isActive ?? true);
  const [selectedEvents, setSelectedEvents] = useState<Set<string>>(new Set(existing?.eventTypes ?? []));
  const [touched, setTouched] = useState(false);

  const toggleEvent = (key: string) =>
    setSelectedEvents((s) => {
      const next = new Set(s);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const nameInvalid = name.trim().length === 0;
  const urlInvalid = !isHttps(url);
  const eventsInvalid = selectedEvents.size === 0;
  const clientInvalid = !editing && !clientId;

  const onSubmit = async () => {
    setTouched(true);
    if (nameInvalid || urlInvalid || eventsInvalid || clientInvalid) return;

    // try/catch so a failed create/update surfaces an error toast instead of an unhandled
    // rejection with zero feedback (#55).
    try {
      if (editing && webhookId) {
        await update.mutateAsync({
          webhookId,
          req: { name, url, eventTypes: [...selectedEvents], isActive: active },
          idempotencyKey: idempotencyKey(),
        });
        toast.success(t('developers.webhookForm.saved'));
        onClose();
      } else {
        const result = await create.mutateAsync({
          clientId,
          tenantId: null,
          name,
          url,
          eventTypes: [...selectedEvents],
          secret: secret || null,
          maxRetries: 5,
          timeoutSeconds: 30,
          idempotencyKey: idempotencyKey(),
        });
        // One-time signing-secret reveal.
        openPanel({ type: 'clientSecret', result, kind: 'webhook', intent: 'created' });
      }
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const pending = create.isPending || update.isPending;

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('developers.webhookForm.eyebrow')}
      title={editing ? t('developers.webhookForm.titleEdit') : t('developers.webhookForm.titleCreate')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="button" disabled={pending} onClick={() => void onSubmit()}>
            {editing ? t('developers.webhookForm.save') : t('developers.webhookForm.create')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {!editing ? (
          <div>
            <label htmlFor="wf-client" className={labelClass}>
              {t('developers.webhookForm.client')}
            </label>
            <Select id="wf-client" value={clientId} onChange={(e) => setClientId(e.target.value)} aria-invalid={touched && clientInvalid}>
              <option value="">—</option>
              {clients?.map((c) => (
                <option key={c.clientId} value={c.clientId}>
                  {c.clientName}
                </option>
              ))}
            </Select>
          </div>
        ) : null}

        <FieldShell label={t('developers.webhookForm.name')} htmlFor="wf-name" error={touched && nameInvalid ? t('developers.validation.name') : undefined}>
          <TextInput id="wf-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder={t('developers.webhookForm.namePlaceholder')} aria-invalid={touched && nameInvalid} />
        </FieldShell>

        <FieldShell label={t('developers.webhookForm.url')} htmlFor="wf-url" error={touched && urlInvalid ? t('developers.validation.url') : undefined}>
          <TextInput id="wf-url" type="url" inputMode="url" className="mono" value={url} onChange={(e) => setUrl(e.target.value)} placeholder={t('developers.webhookForm.urlPlaceholder')} aria-invalid={touched && urlInvalid} />
        </FieldShell>

        {!editing ? (
          <FieldShell label={t('developers.webhookForm.signingSecret')} htmlFor="wf-secret" optional={t('developers.webhookForm.signingSecretOptional')}>
            <TextInput id="wf-secret" className="mono" value={secret} onChange={(e) => setSecret(e.target.value)} />
          </FieldShell>
        ) : null}

        <section>
          <span className={labelClass}>{t('developers.webhookForm.eventTypes')}</span>
          {eventsLoading || !eventTypes ? (
            <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
              {Array.from({ length: 5 }).map((_, i) => (
                <Skeleton key={i} className="h-9 w-full" />
              ))}
            </div>
          ) : (
            <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
              {eventTypes.map((e) => (
                <li key={e.eventType}>
                  <label className="flex cursor-pointer items-center gap-2.5 px-3 py-2 transition-colors hover:bg-surface-sunk">
                    <input
                      type="checkbox"
                      checked={selectedEvents.has(e.eventType)}
                      onChange={() => toggleEvent(e.eventType)}
                      className="h-4 w-4 accent-[var(--primary)]"
                    />
                    <span className="min-w-0 flex-1">
                      <span className="mono block truncate text-[12px] text-ink">{e.eventType}</span>
                      <span className="block truncate text-[11px] text-muted">{e.description}</span>
                    </span>
                  </label>
                </li>
              ))}
            </ul>
          )}
          {touched && eventsInvalid ? (
            <p role="alert" className="mt-1 text-[12px] text-danger">
              {t('developers.validation.events')}
            </p>
          ) : null}
        </section>

        {editing ? (
          <label className="flex items-center gap-2.5 rounded-[var(--radius-sm)] border border-line px-3 py-2.5">
            <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} className="h-4 w-4 accent-[var(--primary)]" />
            <span className="text-[13px] text-ink">{t('developers.webhookForm.active')}</span>
          </label>
        ) : null}
      </div>
    </SlideOver>
  );
}
