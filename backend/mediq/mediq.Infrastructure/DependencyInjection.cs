using mediq.Application.Abstractions;
using mediq.Infrastructure.Audit;
using mediq.Infrastructure.Idempotency;
using mediq.Infrastructure.Persistence;
using mediq.Infrastructure.Persistence.Repositories;
using mediq.Infrastructure.PlatformApi;
using mediq.Infrastructure.Rbac;
using mediq.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace mediq.Infrastructure;

public static class InfrastructureRegistration
{
    /// <summary>
    /// Wires EF Core (database-first, mapped to the canonical <c>platform</c> schema), the UoW, the
    /// repositories (only where they earn their place), and the security/RBAC/audit services. The
    /// connection string is sourced from configuration/Aspire ("platform-db" or "DefaultConnection").
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString =
            config.GetConnectionString("platform-db")
            ?? config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("No 'platform-db' or 'DefaultConnection' connection string configured.");

        services.AddDbContext<PlatformDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform")));

        // UnitOfWork over the context (the schema owns the data; we only track drift).
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Dedicated connection for write-then-throw security records (audit, login attempts, lockout,
        // chain-revoke) — they must survive a command-transaction rollback.
        services.AddSingleton<IDedicatedConnectionFactory, DedicatedConnectionFactory>();

        // Repositories — write-side aggregates with non-trivial logic.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IImpersonationRepository, ImpersonationRepository>();

        // Read-side projections + provisioning.
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IUserProvisioning, UserProvisioning>();
        services.AddScoped<IUserLifecycle, Persistence.Repositories.UserLifecycle>();

        // Security.
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<ISessionStore, SessionStore>();
        services.AddScoped<ISessionAdminService, Security.SessionAdminService>();
        services.AddScoped<ILoginAttemptService, LoginAttemptService>();

        // RBAC + audit + DURABLE idempotency (slice 03 — table-backed, survives restart/scale-out).
        services.AddScoped<IRbacQueryService, RbacQueryService>();
        services.AddScoped<IIamReadService, IamReadService>();
        services.AddScoped<IAuditTrailWriter, AuditTrailWriter>();
        services.AddScoped<IIdempotencyStore, Idempotency.DurableIdempotencyStore>();

        // platform_api (slice 02): OAuth clients, scopes/tokens, request log, webhooks.
        services.AddScoped<IApiClientRepository, ApiClientRepository>();
        services.AddScoped<IApiScopeRepository, ApiScopeRepository>();
        services.AddScoped<IApiTokenStore, ApiTokenStore>();
        services.AddScoped<IApiRequestLogWriter, ApiRequestLogWriter>();
        services.AddScoped<IApiRequestLogReader, ApiRequestLogReader>();
        services.AddScoped<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();
        services.AddScoped<IEventTypeRepository, EventTypeRepository>();
        services.AddScoped<IWebhookDeliveryStore, WebhookDeliveryStore>();
        // Durable async webhook delivery: publish enqueues, the WebhookDeliveryWorker drains this store out-of-band.
        services.Configure<mediq.Application.Options.WebhookDeliveryOptions>(
            config.GetSection(mediq.Application.Options.WebhookDeliveryOptions.SectionName));
        services.AddScoped<IWebhookDeliveryDrainStore, WebhookDeliveryDrainStore>();
        // Developer-portal forensics: deliveries list + manual retry (tenant-scoped via the subscription join).
        services.AddScoped<IWebhookDeliveryAdminStore, WebhookDeliveryAdminStore>();
        services.AddSingleton<IWebhookSigner, WebhookSigner>();
        services.AddHttpClient("webhooks");
        services.AddScoped<IWebhookHttpDispatcher, WebhookHttpDispatcher>();
        services.AddScoped<IWebhookPublisher, WebhookPublisher>();

        // Durable transactional INTEGRATION-EVENT OUTBOX (phase-4 seam). WebhookPublisher captures EVERY event
        // into platform_api.integration_event_outbox atomically with the business write (closing the lost-event
        // gap: an event with no matching webhook subscription was previously discarded). The drain worker
        // (IntegrationEventDrainWorker, registered in Program behind Messaging:DrainWorkerEnabled) publishes due
        // rows via IIntegrationEventBus. Provider switch mirrors the AiService/Abdm honest-stub seam:
        //   Messaging:Provider=none (dev/test DEFAULT) → NullIntegrationEventBus (no I/O, no broker needed)
        //   Messaging:Provider=rabbitmq               → RabbitMqIntegrationEventBus (needs the Aspire IConnection;
        //                                               Program registers it via AddRabbitMQClient under the same flag)
        services.Configure<mediq.Application.Options.MessagingOptions>(
            config.GetSection(mediq.Application.Options.MessagingOptions.SectionName));
        if (string.Equals(config[$"{mediq.Application.Options.MessagingOptions.SectionName}:Provider"],
                "rabbitmq", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IIntegrationEventBus, Messaging.RabbitMqIntegrationEventBus>();
        else
            services.AddSingleton<IIntegrationEventBus, Messaging.NullIntegrationEventBus>();
        services.AddScoped<IIntegrationOutboxStore, Messaging.IntegrationOutboxStore>();
        services.AddScoped<IIntegrationEventOutboxDrainStore, Messaging.IntegrationEventOutboxDrainStore>();

        // Retention pruner (phase-4): a config-gated SWEEP worker (RetentionPruneWorker, registered in Program
        // behind Retention:PrunerEnabled) physically deletes AGED, terminal status='success' rows from the two
        // append-only platform_api operational tables (integration_event_outbox / webhook_deliveries), closing the
        // unbounded-growth ops hazard. DEFAULT-OFF; only success rows past the retention window are removed.
        services.Configure<mediq.Application.Options.RetentionPruneOptions>(
            config.GetSection(mediq.Application.Options.RetentionPruneOptions.SectionName));
        services.AddScoped<IRetentionPruneStore, Messaging.RetentionPruneStore>();

        // docslot (slice 03 — booking core). Repositories, read services, slot holds, OPD tokens,
        // purpose-of-use, booking-event publisher. Clinical PHI services deferred to 03b/05.
        services.AddScoped<IBookingRepository, Docslot.BookingRepository>();
        services.AddScoped<IBookingReadService, Docslot.BookingReadService>();
        services.AddScoped<IDoctorReadService, Docslot.DoctorReadService>();
        services.AddScoped<IDoctorRepository, Docslot.DoctorRepository>();
        services.AddScoped<IAnalyticsReadService, Docslot.AnalyticsReadService>();
        services.AddScoped<IBadgeReadService, Docslot.BadgeReadService>();
        services.AddScoped<IPatientReadService, Docslot.PatientReadService>();
        services.AddScoped<IPatientRepository, Docslot.PatientRepository>();
        services.AddScoped<ISettingsReadService, Docslot.SettingsReadService>();
        services.AddScoped<ISettingsRepository, Docslot.SettingsRepository>();
        services.AddScoped<ISlotHoldService, Docslot.SlotHoldService>();
        services.AddScoped<ISlotGenerationService, Docslot.SlotGenerationService>();
        services.AddScoped<Application.Features.Docslot.Bookings.IBookingCreationService, Application.Features.Docslot.Bookings.BookingCreationService>();
        services.AddScoped<IOpdTokenService, Docslot.OpdTokenService>();
        services.AddScoped<IPurposeOfUseWriter, Docslot.PurposeOfUseWriter>();
        services.AddScoped<IBookingEventPublisher, Docslot.BookingEventPublisher>();
        // NOTE: slot_holds + idempotency_keys are now CANONICAL tables (slice 05 — 03_docslot.sql /
        // 01_platform_core.sql). The app no longer issues DDL at startup.

        // Security hardening (slice 05). Field encryption (Layer 3) + DPDP rights + audit-verify + break-glass.
        services.AddScoped<IKeyManagementService, Security.LocalEnvelopeKeyManagementService>();
        services.AddScoped<IFieldEncryptionService, Security.FieldEncryptionService>();
        services.AddScoped<IDataExportService, Security.DataExportService>();
        services.AddScoped<ICryptoErasureService, Security.CryptoErasureService>();
        services.AddScoped<IBreachReportingService, Security.BreachReportingService>();
        services.AddScoped<IConsentEventLogger, Security.ConsentEventLogger>();
        services.AddScoped<IAuditChainService, Security.AuditChainService>();
        services.AddScoped<IBreakGlassService, Security.BreakGlassService>();
        services.AddScoped<ISecurityReadService, Security.SecurityReadService>();

        // WhatsApp inbound conversational booking (inbound only; outbound send is stubbed via the outbox).
        services.AddSingleton<IWhatsAppSignatureVerifier, Docslot.WhatsApp.WhatsAppSignatureVerifier>();
        services.AddScoped<IProcessedMessageStore, Docslot.WhatsApp.ProcessedMessageStore>();
        services.AddScoped<IWaMessageLogWriter, Docslot.WhatsApp.WaMessageLogWriter>();
        services.AddScoped<IOutboxMessageEnqueuer, Docslot.WhatsApp.OutboxMessageEnqueuer>();
        services.AddScoped<IWaContactProfileRepository, Docslot.WhatsApp.WaContactProfileRepository>();
        services.AddScoped<IConversationRepository, Docslot.WhatsApp.ConversationRepository>();
        services.AddScoped<IWhatsAppCatalogReadService, Docslot.WhatsApp.WhatsAppCatalogReadService>();

        // Behalf-booking patient OTP consent (DPDP). Store = infra; service = application orchestration.
        services.AddScoped<IConsentOtpStore, Docslot.WhatsApp.ConsentOtpStore>();
        services.AddScoped<IPartnerNudgeStore, Docslot.WhatsApp.PartnerNudgeStore>();
        services.AddScoped<IPatientConsentService, Application.Features.Docslot.WhatsApp.PatientConsentService>();

        // WhatsApp OUTBOUND drain: the store claims/transitions docslot.outbox_messages; the sender delivers.
        services.AddScoped<IOutboxDrainStore, Docslot.WhatsApp.OutboxDrainStore>();
        AddWhatsAppSender(services, config);

        // Clinical PHI (slice 03b) — encrypted-at-rest, RLS-protected, consent-gated.
        services.AddScoped<IClinicalRepository, Docslot.ClinicalRepository>();
        services.AddScoped<IAbdmConsentService, Docslot.AbdmConsentService>();
        services.AddScoped<IAccessPolicyService, Docslot.AccessPolicyService>();

        // Drug-safety screening — generates docslot.drug_alerts at prescription issue/amend by screening the
        // prescribed meds against recorded allergies + current meds. Dev = a curated, well-known ruleset; prod
        // swaps a licensed interaction database (FDB/Medi-Span) by config; 'none' disables (fail-SAFE no-op).
        services.Configure<mediq.Application.Options.DrugInteractionOptions>(
            config.GetSection(mediq.Application.Options.DrugInteractionOptions.SectionName));
        if (string.Equals(config[$"{mediq.Application.Options.DrugInteractionOptions.SectionName}:Provider"],
                "none", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IDrugInteractionSource, Clinical.NullDrugInteractionSource>();
        else
            services.AddSingleton<IDrugInteractionSource, Clinical.CuratedDrugInteractionSource>();
        services.AddScoped<IDrugSafetyScreeningService, Application.Features.Docslot.Clinical.DrugSafetyScreeningService>();

        // ABDM (NHA) gateway — publishes stored health records as care contexts to the national network. Dev =
        // a deterministic sandbox (no network); prod swaps the real 'nha' adapter by config; 'none' disables
        // linking (honest refusal, never a fake success). Singleton (stateless). Same seam as the payout rail.
        services.Configure<mediq.Application.Options.AbdmGatewayOptions>(
            config.GetSection(mediq.Application.Options.AbdmGatewayOptions.SectionName));
        if (string.Equals(config[$"{mediq.Application.Options.AbdmGatewayOptions.SectionName}:Provider"],
                "none", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IAbdmGateway, Abdm.DisabledAbdmGateway>();
        else
            services.AddSingleton<IAbdmGateway, Abdm.SandboxAbdmGateway>();

        // AI sibling-service no-show risk client. Dev/test default = a deterministic stub (the AI service need
        // not be running); prod sets AiService:Provider=http (a typed HttpClient to the AI service, forwarding
        // the caller's JWT). Advisory only — never on the booking critical path. Same honest-stub seam.
        services.Configure<mediq.Application.Options.AiServiceOptions>(
            config.GetSection(mediq.Application.Options.AiServiceOptions.SectionName));
        if (string.Equals(config[$"{mediq.Application.Options.AiServiceOptions.SectionName}:Provider"],
                "http", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpContextAccessor();   // the HTTP adapters forward the caller's bearer JWT to the AI service
            services.AddHttpClient<IAiNoShowClient, Ai.HttpAiNoShowClient>(ConfigureAiHttp);
            services.AddHttpClient<IAiTriageClient, Ai.HttpAiTriageClient>(ConfigureAiHttp);
            // Slice-11 PHI proxies (OCR lab-report extraction + RAG ask). Same seam; the adapters forward the
            // caller JWT + X-Purpose-Of-Use and propagate the AI's 4xx so a gate decision is never masked.
            services.AddHttpClient<IAiOcrClient, Ai.HttpAiOcrClient>(ConfigureAiHttp);
            services.AddHttpClient<IAiRagClient, Ai.HttpAiRagClient>(ConfigureAiHttp);
        }
        else
        {
            services.AddScoped<IAiNoShowClient, Ai.StubAiNoShowClient>();
            services.AddScoped<IAiTriageClient, Ai.StubAiTriageClient>();
            services.AddScoped<IAiOcrClient, Ai.StubAiOcrClient>();
            services.AddScoped<IAiRagClient, Ai.StubAiRagClient>();
        }

        static void ConfigureAiHttp(IServiceProvider sp, HttpClient http)
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<mediq.Application.Options.AiServiceOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds));
        }

        // Proactive no-show prediction backfill (slice 16): a config-gated worker (NoShowPredictionWorker,
        // registered in Program behind NoShowBackfill:Enabled) scans upcoming, not-yet-scored bookings via two
        // SECURITY DEFINER functions (rls-cross-tenant-worker pattern; NON-PHI features only), mints a short-TTL
        // per-tenant SERVICE token, and asks the AI sibling to score each — marking it so it is never re-predicted.
        // DEFAULT-OFF. The store + runner are scoped so they are resolvable even when the worker isn't registered.
        services.Configure<mediq.Application.Options.NoShowBackfillOptions>(
            config.GetSection(mediq.Application.Options.NoShowBackfillOptions.SectionName));
        services.AddScoped<INoShowBackfillStore, Docslot.NoShowBackfillStore>();
        services.AddScoped<INoShowBackfillRunner, Application.Features.Docslot.NoShow.NoShowBackfillRunner>();

        // Lab-report blob storage (PHI artifacts). Bytes are envelope-ENCRYPTED by the app BEFORE storage, so
        // the adapter only ever holds ciphertext. Dev = local filesystem (default) or in-memory (tests); prod
        // swaps in an object store (S3/GCS/Azure + provider SSE/KMS) by config (same pattern as the payout
        // rail). Tenant-namespaced keys + a cross-tenant read guard. Singleton (stateless / process store).
        services.Configure<mediq.Application.Options.BlobStorageOptions>(
            config.GetSection(mediq.Application.Options.BlobStorageOptions.SectionName));
        if (string.Equals(config[$"{mediq.Application.Options.BlobStorageOptions.SectionName}:Provider"],
                "in_memory", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IBlobStorage, Storage.InMemoryBlobStorage>();
        else
            services.AddSingleton<IBlobStorage, Storage.LocalFileSystemBlobStorage>();

        // Commission / broker economy (slice 07) — PAN-encrypted KYC, attribution engine, payouts, disputes.
        services.AddScoped<IBrokerRepository, Commission.BrokerRepository>();
        services.AddScoped<IAttributionRepository, Commission.AttributionRepository>();
        services.AddScoped<ICommissionRuleRepository, Commission.CommissionRuleRepository>();
        services.AddScoped<IPayoutRepository, Commission.PayoutRepository>();
        services.AddScoped<IBrokerWalletRepository, Commission.BrokerWalletRepository>();
        services.AddScoped<ICommissionAdminRepository, Commission.CommissionAdminRepository>();
        services.AddScoped<IReferralLinkRepository, Commission.ReferralLinkRepository>();
        services.AddScoped<IFraudScorer, Commission.FraudScorer>();
        services.AddScoped<IBrokerEventPublisher, Commission.BrokerEventPublisher>();
        services.AddScoped<IBrokerIdentityResolver, Commission.BrokerIdentityResolver>();
        services.AddScoped<ICommissionLifecycleService, Application.Features.Commission.CommissionLifecycleService>();
        services.AddScoped<IDirectDiscountService, Application.Features.Commission.DirectDiscountService>();
        // Post-hoc attribution claim (patient OTP confirm/deny) — store + orchestrator.
        services.AddScoped<IAttributionClaimOtpStore, Commission.AttributionClaimOtpStore>();
        services.AddScoped<IPostHocClaimService, Application.Features.Commission.PostHocClaimService>();
        // Payout rail: dev = honest dry-run stub. A real adapter (RazorpayX/Cashfree) is selected by config
        // when credentials are present (same pattern as the WhatsApp sender) — not wired until then.
        services.AddScoped<IPayoutGateway, Commission.StubPayoutGateway>();

        // Form 16A (TDS) certificate persistence + the dev HTML renderer (a PDF/TRACES adapter swaps in for prod).
        services.AddScoped<ITdsCertificateRepository, Commission.TdsCertificateRepository>();
        services.AddSingleton<IForm16ADocumentRenderer, Commission.HtmlForm16ADocumentRenderer>();

        return services;
    }

    /// <summary>
    /// Selects the outbound WhatsApp transport from config: the real <see cref="Docslot.WhatsApp.MetaWhatsAppSender"/>
    /// (typed HttpClient to graph.facebook.com) when BOTH <c>WhatsApp:AccessToken</c> and
    /// <c>WhatsApp:GraphBaseUrl</c> are present; otherwise the dev <see cref="Docslot.WhatsApp.StubWhatsAppSender"/>
    /// (logs + synthetic wamid, never calls Meta). Default-safe: with no credentials configured (dev/test) the
    /// stub is wired, so the outbound path runs end-to-end without a Meta secret.
    /// </summary>
    private static void AddWhatsAppSender(IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(Application.Options.WhatsAppOptions.SectionName);
        var accessToken = section["AccessToken"];
        var graphBaseUrl = section["GraphBaseUrl"];

        if (!string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(graphBaseUrl))
        {
            services.AddHttpClient<IWhatsAppSender, Docslot.WhatsApp.MetaWhatsAppSender>();
        }
        else
        {
            services.AddScoped<IWhatsAppSender, Docslot.WhatsApp.StubWhatsAppSender>();
        }
    }
}
