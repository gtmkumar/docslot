---
name: developer-portal
description: Slice 02 frontend — Developer / API Platform admin portal (/developers): API clients, scopes, webhooks+deliveries, request logs, and the secret-shown-once UX.
metadata:
  type: project
---

Slice 02 (platform_api) frontend shipped the developer portal at `/developers`. Mirrors the real backend DTOs in `backend/mediq/mediq.SharedDataModel/Docslot/PlatformApi/` (ApiClientDtos, OAuthDtos, WebhookDtos) — those EXIST and are authoritative; contracts match them 1:1 (camelCase).

## Screens (`features/developers/`, route `/developers`)
- `DevelopersScreen.tsx` — Radix Tabs: API clients | Scopes | Webhooks | Request logs. `api.ts` has all hooks.
- Tabs in `components/`: `ClientsTab`, `ScopesTab` (read-only catalog), `WebhooksTab` (+ Deliveries action), `LogsTab` (paginated, per-client filter). Shared `StatusBadge` (token tones, dot+text).
- Panels: `RegisterClientPanel`, `ManageClientPanel` (details+approve/suspend/reactivate, rotate-secret, rate limits, scopes), `SecretRevealPanel`, `WebhookFormPanel` (create+edit), `DeliveriesPanel`.

## Panel union (stores/ui.ts) + URL addressing
Added: `registerClient`, `manageClient{clientId}`, `createWebhook`, `webhookForm{webhookId}`, `webhookDeliveries{webhookId}` (all URL-restorable, added to router enum + SlideOverHost panelToSearch/searchToPanel), and **`clientSecret{result, kind:'client'|'webhook'}`** which is DELIBERATELY NOT URL-addressable (excluded from the router enum; panelToSearch returns `{}` for it). The `ui` store is NOT persisted, so the secret payload vanishes on refresh — exactly the desired behavior.

## SECRET-SHOWN-ONCE UX (the key security pattern — don't break it)
The plaintext client secret / webhook signing secret exists ONLY on the create/rotate RESULT (`ApiClientSecretResult.clientSecret` / `CreateWebhookResult.signingSecret`). Flow: RegisterClientPanel/ManageClientPanel(rotate)/WebhookFormPanel(create) call `mutateAsync`, then `openPanel({type:'clientSecret', result, kind})` handing the result straight into the in-store panel payload. `SecretRevealPanel` renders it once with a copy button + warning. It is NEVER written to React Query (`useRegisterClient`/`useRotateSecret`/`useCreateWebhook` invalidate the list but don't cache the secret), NEVER in the URL, NEVER in the persisted session. Audit greps: no `setQueryData(...secret)`, no `persist(...secret)`.

## Gating + permission-key flags
- **Only `platform.api_clients.manage` exists** in the SQL seed for this domain. ALL portal management actions (register/approve/suspend/rotate/rate-limits/scopes/create-webhook/edit-webhook/retry-delivery) gate on it. Read tabs (scopes/logs/deliveries) are reachable via the menu (server permission-filtered).
- **FLAGS to orchestrator (missing finer-grained keys):** no `platform.webhooks.manage`, no `platform.api.logs.read`, no separate `platform.api_clients.read` vs `.manage`. Gated everything on `platform.api_clients.manage`; backend may want finer keys.
- **MENU SEEDING TODO (flagged):** `08_rbac_navigation.sql` has NO navigation row for the developer portal. I mocked a `developers` menu node (icon key `code`, route `/developers`) in `lib/mock/index.ts` so backend-driven nav renders it — backend needs a real `navigation_menus` row + menu→`platform.api_clients.manage` map. Added `platform.api_clients.manage` to the `SIGNED_IN_PERMISSIONS` demo seed.

## Contracts (lib/mock/contracts.ts) + mock (lib/mock/developers.ts, re-exported from index)
`ApiClient(+derived status pending|approved|suspended from isActive/isVerified), RegisterApiClientRequest, ApiClientSecretResult, SetClientStatus/RateLimits/ScopesRequest, Scope, WebhookSubscription, CreateWebhookRequest/Result, UpdateWebhookRequest, WebhookDelivery, EventType, ApiRequestLog(+Page)`. `ApiRequestLogDto` is the ONLY one with no backend DTO yet — built to spec from `platform_api.api_requests` (reconciliation pass will confirm field names). All mutations take a stable caller-generated `idempotencyKey` (de-duped via idemCache); secrets generated at call time in the mock, never seeded.

## Misc
- `lib/format.ts` already had `shortDate`/`dateTime` (IST) from Slice 01 — reused.
- Added `common.edit` i18n key; new `developers.*` namespace (en+hi, compiler-enforced parity).
- Build ~852kB single chunk (code-splitting still deferred).
