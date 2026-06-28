// SlideOverHost — single mount point for every slide-over. Two jobs:
//
//  1. URL ↔ store sync. The router search param `?panel=…&id=…` is the durable
//     source of truth so a refresh restores an open panel. On mount / param
//     change we hydrate the store from the URL; when the store opens/closes we
//     write the URL back. This keeps the slide-over URL-addressable (pattern 1)
//     without every panel re-implementing it.
//
//  2. Render the body for the active Panel type. THIS WAVE only `newBooking` has
//     a real (partial) body; the rest render a titled placeholder panel.

import { lazy, Suspense, useEffect, useRef } from 'react';
import { useNavigate, useSearch } from '@tanstack/react-router';

// Panel bodies are code-split: each feature's CRUD slide-over loads only when
// first opened, so none of the bookings/team/developers panel code sits in the
// main chunk. Named exports are mapped to default for React.lazy. A panel opens
// instantly after its chunk is cached; the brief first-open load is covered by a
// transparent Suspense fallback (the trigger already gave visual feedback).
const NewBookingPanel = lazy(() => import('@/features/bookings/components/NewBookingPanel').then((m) => ({ default: m.NewBookingPanel })));
// The manage / approve / conversation panels are opened by booking id; the loader
// wrappers fetch the full booking (GET /bookings/{id} live / prototype seam mock)
// before rendering the panel body, so they open for a REAL booking.
const ManageAppointmentPanel = lazy(() => import('@/features/bookings/components/BookingPanelLoader').then((m) => ({ default: m.ManageAppointmentPanelLoader })));
const ConversationPanel = lazy(() => import('@/features/bookings/components/BookingPanelLoader').then((m) => ({ default: m.ConversationPanelLoader })));
const ApproveCollectPanel = lazy(() => import('@/features/bookings/components/BookingPanelLoader').then((m) => ({ default: m.ApproveCollectPanelLoader })));
const BookTimePanel = lazy(() => import('@/features/bookings/components/BookTimePanel').then((m) => ({ default: m.BookTimePanel })));
const AddDoctorPanel = lazy(() => import('@/features/doctors/components/AddDoctorPanel').then((m) => ({ default: m.AddDoctorPanel })));
const AddPatientPanel = lazy(() => import('@/features/patients/components/AddPatientPanel').then((m) => ({ default: m.AddPatientPanel })));
const InviteUserPanel = lazy(() => import('@/features/team/components/InviteUserPanel').then((m) => ({ default: m.InviteUserPanel })));
const ManageUserPanel = lazy(() => import('@/features/team/components/ManageUserPanel').then((m) => ({ default: m.ManageUserPanel })));
const EditUserPanel = lazy(() => import('@/features/team/components/EditUserPanel').then((m) => ({ default: m.EditUserPanel })));
const RoleViewPanel = lazy(() => import('@/features/team/components/RoleViewPanel').then((m) => ({ default: m.RoleViewPanel })));
const CreateRolePanel = lazy(() => import('@/features/team/components/CreateRolePanel').then((m) => ({ default: m.CreateRolePanel })));
const RoleMatrixPanel = lazy(() => import('@/features/team/components/RoleMatrixPanel').then((m) => ({ default: m.RoleMatrixPanel })));
const DuplicateRolePanel = lazy(() => import('@/features/team/components/DuplicateRolePanel').then((m) => ({ default: m.DuplicateRolePanel })));
const EffectiveAccessPanel = lazy(() => import('@/features/team/components/EffectiveAccessPanel').then((m) => ({ default: m.EffectiveAccessPanel })));
const CreateModulePanel = lazy(() => import('@/features/team/components/CreateModulePanel').then((m) => ({ default: m.CreateModulePanel })));
const CreatePermissionPanel = lazy(() => import('@/features/team/components/CreatePermissionPanel').then((m) => ({ default: m.CreatePermissionPanel })));
const RegisterClientPanel = lazy(() => import('@/features/developers/components/RegisterClientPanel').then((m) => ({ default: m.RegisterClientPanel })));
const ManageClientPanel = lazy(() => import('@/features/developers/components/ManageClientPanel').then((m) => ({ default: m.ManageClientPanel })));
const SecretRevealPanel = lazy(() => import('@/features/developers/components/SecretRevealPanel').then((m) => ({ default: m.SecretRevealPanel })));
const WebhookFormPanel = lazy(() => import('@/features/developers/components/WebhookFormPanel').then((m) => ({ default: m.WebhookFormPanel })));
const DeliveriesPanel = lazy(() => import('@/features/developers/components/DeliveriesPanel').then((m) => ({ default: m.DeliveriesPanel })));
const DataExportPanel = lazy(() => import('@/features/security/components/DataExportPanel').then((m) => ({ default: m.DataExportPanel })));
const ErasurePanel = lazy(() => import('@/features/security/components/ErasurePanel').then((m) => ({ default: m.ErasurePanel })));
const DeletionCertificatePanel = lazy(() => import('@/features/security/components/DeletionCertificatePanel').then((m) => ({ default: m.DeletionCertificatePanel })));
const ReportBreachPanel = lazy(() => import('@/features/security/components/ReportBreachPanel').then((m) => ({ default: m.ReportBreachPanel })));
const BreakGlassPanel = lazy(() => import('@/features/security/components/BreakGlassPanel').then((m) => ({ default: m.BreakGlassPanel })));
const PrescriptionDetailPanel = lazy(() => import('@/features/patients/components/PrescriptionDetailPanel').then((m) => ({ default: m.PrescriptionDetailPanel })));
const IssuePrescriptionPanel = lazy(() => import('@/features/patients/components/IssuePrescriptionPanel').then((m) => ({ default: m.IssuePrescriptionPanel })));
const LabReportDetailPanel = lazy(() => import('@/features/patients/components/LabReportDetailPanel').then((m) => ({ default: m.LabReportDetailPanel })));
const UploadReportPanel = lazy(() => import('@/features/patients/components/UploadReportPanel').then((m) => ({ default: m.UploadReportPanel })));
const AbdmDetailPanel = lazy(() => import('@/features/patients/components/AbdmDetailPanel').then((m) => ({ default: m.AbdmDetailPanel })));
const RegisterBrokerPanel = lazy(() => import('@/features/commission/components/RegisterBrokerPanel').then((m) => ({ default: m.RegisterBrokerPanel })));
const ManageBrokerPanel = lazy(() => import('@/features/commission/components/ManageBrokerPanel').then((m) => ({ default: m.ManageBrokerPanel })));
const CommissionRulePanel = lazy(() => import('@/features/commission/components/CommissionRulePanel').then((m) => ({ default: m.CommissionRulePanel })));
const RaiseDisputePanel = lazy(() => import('@/features/commission/components/RaiseDisputePanel').then((m) => ({ default: m.RaiseDisputePanel })));
const ResolveDisputePanel = lazy(() => import('@/features/commission/components/ResolveDisputePanel').then((m) => ({ default: m.ResolveDisputePanel })));
const BeginImpersonationPanel = lazy(() => import('@/features/impersonation/components/BeginImpersonationPanel').then((m) => ({ default: m.BeginImpersonationPanel })));
import { BOOKINGS } from '@/lib/data';
import { useUI, type Panel } from '@/stores/ui';

/** Panel types that can be restored from the URL without an in-memory payload. */
type PanelType = Panel['type'];
/** Transient panels that must NEVER appear in the URL:
 *  - one-time-payload reveals (`clientSecret`, `deletionCertificate`);
 *  - all CLINICAL panels — they carry a declared purpose-of-use and/or a PHI
 *    record id, neither of which may be URL-encoded or survive a refresh
 *    (re-entry must pass through the purpose gate again). */
type TransientPanelType =
  | 'clientSecret'
  | 'deletionCertificate'
  | 'prescriptionDetail'
  | 'issuePrescription'
  | 'labReportDetail'
  | 'uploadReport'
  | 'abdmDetail';
/** URL-addressable panel types. */
type UrlPanelType = Exclude<PanelType, TransientPanelType>;
const TRANSIENT_SET = new Set<PanelType>([
  'clientSecret', 'deletionCertificate',
  'prescriptionDetail', 'issuePrescription', 'labReportDetail', 'uploadReport', 'abdmDetail',
]);
/** Type guard: narrows a panel to the URL-addressable subset. */
function isUrlPanel(type: PanelType): type is UrlPanelType {
  return !TRANSIENT_SET.has(type);
}

const PAYLOADLESS: PanelType[] = [
  'newBooking', 'addDoctor', 'addPatient', 'bookTime', 'inviteUser', 'createRole',
  'createModule', 'createPermission',
  'registerClient', 'createWebhook',
  'exportData', 'reportBreach', 'breakGlass',
  'registerBroker', 'createCommissionRule',
  'beginImpersonation',
];

function panelToSearch(panel: Panel | null): { panel?: UrlPanelType; id?: string } {
  if (!panel) return {};
  // Transient panels are NOT URL-addressable — they carry plaintext (a secret /
  // an erasure certificate) or clinical PHI + a declared purpose, none of which
  // may be URL-encoded or survive a refresh. Return {} so the writer clears any
  // existing param while one is open.
  if (!isUrlPanel(panel.type)) return {};
  if ('bookingId' in panel) return { panel: panel.type as UrlPanelType, id: panel.bookingId };
  if ('booking' in panel && panel.booking) return { panel: panel.type as UrlPanelType, id: panel.booking.id };
  if (panel.type === 'manageUser') return { panel: panel.type, id: panel.userId };
  if (panel.type === 'editUser') return { panel: panel.type, id: panel.userId };
  if (panel.type === 'roleView') return { panel: panel.type, id: panel.roleId };
  if (panel.type === 'roleMatrix' || panel.type === 'duplicateRole') return { panel: panel.type, id: panel.roleId };
  if (panel.type === 'effectiveAccess') return { panel: panel.type, id: panel.userId };
  if (panel.type === 'manageClient') return { panel: panel.type, id: panel.clientId };
  if (panel.type === 'webhookForm' || panel.type === 'webhookDeliveries') return { panel: panel.type, id: panel.webhookId };
  if (panel.type === 'eraseData') return panel.requestId ? { panel: panel.type, id: panel.requestId } : { panel: panel.type };
  if (panel.type === 'manageBroker') return { panel: panel.type, id: panel.brokerId };
  if (panel.type === 'raiseDispute') return { panel: panel.type, id: panel.attributionId };
  if (panel.type === 'resolveDispute') return { panel: panel.type, id: panel.disputeId };
  return { panel: panel.type };
}

/** Reconstruct a Panel from URL params (used on refresh / deep-link). */
function searchToPanel(type: PanelType | undefined, id: string | undefined): Panel | null {
  if (!type) return null;
  // Booking detail panels restore from the URL by id alone — the panel re-fetches
  // the full booking (no BOOKINGS.find), so a deep link / refresh works for a REAL
  // booking, not just the prototype rows.
  if (type === 'conversation' || type === 'manage' || type === 'approve') {
    return id ? { type, bookingId: id } : null;
  }
  if (type === 'bookTime') {
    const booking = id ? (BOOKINGS.find((b) => b.id === id) ?? null) : null;
    return { type, booking };
  }
  // Team panels carry only an id (the panel fetches its own data by id).
  if (type === 'manageUser') return id ? { type, userId: id } : null;
  if (type === 'editUser') return id ? { type, userId: id } : null;
  if (type === 'roleView') return id ? { type, roleId: id } : null;
  if (type === 'roleMatrix' || type === 'duplicateRole') return id ? { type, roleId: id } : null;
  if (type === 'effectiveAccess') return id ? { type, userId: id } : null;
  // Developer panels that carry an id.
  if (type === 'manageClient') return id ? { type, clientId: id } : null;
  if (type === 'webhookForm') return id ? { type, webhookId: id } : null;
  if (type === 'webhookDeliveries') return id ? { type, webhookId: id } : null;
  // Security: eraseData carries an OPTIONAL source DPDP requestId.
  if (type === 'eraseData') return { type, requestId: id };
  // Commission panels that carry an id.
  if (type === 'manageBroker') return id ? { type, brokerId: id } : null;
  if (type === 'raiseDispute') return id ? { type, attributionId: id } : null;
  if (type === 'resolveDispute') return id ? { type, disputeId: id } : null;
  if (PAYLOADLESS.includes(type)) return { type } as Panel;
  return null;
}

export function SlideOverHost() {
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { panel?: UrlPanelType; id?: string };
  const panel = useUI((s) => s.panel);
  const openPanel = useUI((s) => s.openPanel);
  const closePanel = useUI((s) => s.closePanel);

  // The URL (`?panel=&id=`) is the durable source of truth. Two effects keep the
  // store and URL in sync. The ordering matters to survive a hard refresh:
  //
  //  - `hydratedRef` flips true only AFTER we've reconciled the store from the
  //    URL at least once. The store→URL writer refuses to run until then, so the
  //    initial render (store=null, URL=manage) never erases the deep-linked param
  //    before hydration — that was the refresh-loses-panel bug.
  //  - `lastSyncedType` dedupes both directions so the two effects don't ping-pong.
  const lastSyncedType = useRef<UrlPanelType | undefined>(undefined);
  const writerFirstRun = useRef(true);

  // URL → store: hydrate on first mount and whenever the param changes
  // out-of-band (back/forward, deep link).
  useEffect(() => {
    const urlType = search.panel;
    if (urlType !== lastSyncedType.current) {
      lastSyncedType.current = urlType;
      const next = searchToPanel(urlType, search.id);
      if (next) openPanel(next);
      else if (panel) closePanel();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search.panel, search.id]);

  // store → URL: write the param when the store changes. The first invocation
  // (on mount) is a no-op: the URL is authoritative at that point and the store
  // may still be catching up to a deep-linked param, so writing here could erase
  // it (the refresh-loses-panel bug). After that, we only write when the store's
  // panel TYPE differs from what we last synced — a genuine user open/close.
  useEffect(() => {
    if (writerFirstRun.current) {
      writerFirstRun.current = false;
      return;
    }
    const next = panelToSearch(panel);
    if ((next.panel ?? undefined) === lastSyncedType.current) return;
    lastSyncedType.current = next.panel;
    void navigate({
      to: '.',
      search: (prev: Record<string, unknown>) => ({ ...prev, panel: next.panel, id: next.id }),
      replace: true,
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [panel]);

  if (!panel) return null;

  // Panels are lazy — render them inside a Suspense boundary. The fallback is
  // null (transparent): the panel chunk loads in a few ms and the trigger has
  // already given feedback, so a flashing skeleton would be noisier than nothing.
  return <Suspense fallback={null}>{renderPanel(panel, closePanel)}</Suspense>;
}

function renderPanel(panel: Panel, closePanel: () => void) {
  switch (panel.type) {
    case 'newBooking':
      return <NewBookingPanel open onClose={closePanel} />;
    case 'manage':
      return <ManageAppointmentPanel bookingId={panel.bookingId} open onClose={closePanel} />;
    case 'conversation':
      return <ConversationPanel bookingId={panel.bookingId} open onClose={closePanel} />;
    case 'approve':
      return <ApproveCollectPanel bookingId={panel.bookingId} open onClose={closePanel} />;
    case 'bookTime':
      return <BookTimePanel open onClose={closePanel} />;
    case 'addDoctor':
      return <AddDoctorPanel open onClose={closePanel} />;
    case 'addPatient':
      return <AddPatientPanel open onClose={closePanel} />;
    case 'inviteUser':
      return <InviteUserPanel open onClose={closePanel} />;
    case 'manageUser':
      return <ManageUserPanel userId={panel.userId} open onClose={closePanel} />;
    case 'editUser':
      return <EditUserPanel userId={panel.userId} open onClose={closePanel} />;
    case 'roleView':
      return <RoleViewPanel roleId={panel.roleId} open onClose={closePanel} />;
    case 'createRole':
      return <CreateRolePanel open onClose={closePanel} />;
    case 'roleMatrix':
      return <RoleMatrixPanel roleId={panel.roleId} open onClose={closePanel} />;
    case 'duplicateRole':
      return <DuplicateRolePanel roleId={panel.roleId} open onClose={closePanel} />;
    case 'effectiveAccess':
      return <EffectiveAccessPanel userId={panel.userId} open onClose={closePanel} />;
    case 'createModule':
      return <CreateModulePanel open onClose={closePanel} />;
    case 'createPermission':
      return <CreatePermissionPanel open onClose={closePanel} />;
    case 'registerClient':
      return <RegisterClientPanel open onClose={closePanel} />;
    case 'manageClient':
      return <ManageClientPanel clientId={panel.clientId} open onClose={closePanel} />;
    case 'clientSecret':
      return <SecretRevealPanel result={panel.result} kind={panel.kind} open onClose={closePanel} />;
    case 'createWebhook':
      return <WebhookFormPanel open onClose={closePanel} />;
    case 'webhookForm':
      return <WebhookFormPanel webhookId={panel.webhookId} open onClose={closePanel} />;
    case 'webhookDeliveries':
      return <DeliveriesPanel webhookId={panel.webhookId} open onClose={closePanel} />;
    case 'exportData':
      return <DataExportPanel open onClose={closePanel} />;
    case 'eraseData':
      return <ErasurePanel requestId={panel.requestId} open onClose={closePanel} />;
    case 'deletionCertificate':
      return <DeletionCertificatePanel result={panel.result} open onClose={closePanel} />;
    case 'reportBreach':
      return <ReportBreachPanel open onClose={closePanel} />;
    case 'breakGlass':
      return <BreakGlassPanel open onClose={closePanel} />;
    case 'prescriptionDetail':
      return <PrescriptionDetailPanel prescriptionId={panel.prescriptionId} purpose={panel.purpose} open onClose={closePanel} />;
    case 'issuePrescription':
      return <IssuePrescriptionPanel patientId={panel.patientId} open onClose={closePanel} />;
    case 'labReportDetail':
      return <LabReportDetailPanel reportId={panel.reportId} purpose={panel.purpose} open onClose={closePanel} />;
    case 'uploadReport':
      return <UploadReportPanel patientId={panel.patientId} open onClose={closePanel} />;
    case 'abdmDetail':
      return <AbdmDetailPanel recordId={panel.recordId} patientId={panel.patientId} purpose={panel.purpose} open onClose={closePanel} />;
    case 'registerBroker':
      return <RegisterBrokerPanel open onClose={closePanel} />;
    case 'manageBroker':
      return <ManageBrokerPanel brokerId={panel.brokerId} open onClose={closePanel} />;
    case 'createCommissionRule':
      return <CommissionRulePanel open onClose={closePanel} />;
    case 'raiseDispute':
      return <RaiseDisputePanel attributionId={panel.attributionId} open onClose={closePanel} />;
    case 'resolveDispute':
      return <ResolveDisputePanel disputeId={panel.disputeId} open onClose={closePanel} />;
    case 'beginImpersonation':
      return <BeginImpersonationPanel open onClose={closePanel} />;
    default:
      return null;
  }
}
