// Client UI state (Zustand): workspace theming + the slide-over stack.
// Per REACT_SKILL.md, the slide-over is the primary CRUD modality and its open
// state lives in a client store rather than ad-hoc component state.

import { create } from 'zustand';
import type { Booking } from '@/lib/types';
import type { ApiClientSecretResult, CreateWebhookResult, ErasureResult, PurposeOfUse } from '@/lib/mock/contracts';

export type Theme = 'light' | 'dark';
export type Density = 'comfortable' | 'compact';

/** Discriminated union of every slide-over panel + its payload. */
export type Panel =
  // Booking detail panels carry only the booking id; the panel fetches the full
  // record (GET /bookings/{id} live / prototype seam in mock) so it opens for a
  // REAL booking and stays URL-restorable (?panel=&id=) across a refresh.
  | { type: 'conversation'; bookingId: string }
  | { type: 'manage'; bookingId: string }
  | { type: 'approve'; bookingId: string }
  | { type: 'newBooking' }
  | { type: 'bookTime'; booking?: Booking | null }
  | { type: 'addDoctor' }
  | { type: 'addPatient' }
  // Team & roles (Slice 01). manageUser/roleView carry an id (userId / roleId),
  // so they are URL-restorable via ?panel=&id=.
  | { type: 'inviteUser' }
  | { type: 'manageUser'; userId: string }
  | { type: 'roleView'; roleId: string }
  | { type: 'createRole' }
  // Roles & permissions privilege matrix (Slice 2). roleMatrix/duplicateRole carry
  // a roleId; effectiveAccess carries a userId — all URL-restorable via ?panel=&id=.
  | { type: 'roleMatrix'; roleId: string }
  | { type: 'duplicateRole'; roleId: string }
  | { type: 'effectiveAccess'; userId: string }
  // Developer / API platform portal (Slice 02). registerClient/createWebhook are
  // payloadless; manageClient/webhookForm(edit)/webhookDeliveries carry an id
  // (URL-restorable). `clientSecret` carries the one-time plaintext secret and is
  // DELIBERATELY NOT URL-restorable — the secret can't survive a refresh.
  | { type: 'registerClient' }
  | { type: 'manageClient'; clientId: string }
  | { type: 'clientSecret'; result: ApiClientSecretResult | CreateWebhookResult; kind: 'client' | 'webhook' }
  | { type: 'createWebhook' }
  | { type: 'webhookForm'; webhookId: string }
  | { type: 'webhookDeliveries'; webhookId: string }
  // Security & Compliance console (Slice 05). exportData/reportBreach/breakGlass
  // are payloadless; eraseData carries the optional source DPDP requestId
  // (URL-restorable). `deletionCertificate` carries the one-time erasure
  // certificate (signature/hashes) and is DELIBERATELY NOT URL-restorable — like
  // `clientSecret`, it must not survive a refresh.
  | { type: 'exportData' }
  | { type: 'eraseData'; requestId?: string }
  | { type: 'reportBreach' }
  | { type: 'breakGlass' }
  | { type: 'deletionCertificate'; result: ErasureResult }
  // Clinical records (Slice 03b). NONE are URL-addressable: clinical detail
  // carries the declared purpose-of-use + a PHI record id, neither of which may
  // appear in the URL or survive a refresh (re-entry must re-declare purpose).
  | { type: 'prescriptionDetail'; prescriptionId: string; purpose: PurposeOfUse }
  | { type: 'issuePrescription'; patientId: string }
  | { type: 'labReportDetail'; reportId: string; purpose: PurposeOfUse }
  | { type: 'uploadReport'; patientId: string }
  | { type: 'abdmDetail'; recordId: string; patientId: string; purpose: PurposeOfUse }
  // Commission / Care Partners (Slice 07). URL-addressable (no PHI/secret payload).
  | { type: 'registerBroker' }
  | { type: 'manageBroker'; brokerId: string }
  | { type: 'createCommissionRule' }
  | { type: 'raiseDispute'; attributionId: string }
  | { type: 'resolveDispute'; disputeId: string }
  // Support impersonation (issue #3). Payloadless + URL-addressable: a super_admin
  // opens it to begin acting as a tenant. No PHI/secret payload — the target
  // tenant id is picked inside the panel, never URL-encoded.
  | { type: 'beginImpersonation' };

interface UIState {
  orgId: string;
  theme: Theme;
  density: Density;
  accent: string;
  primary: string;
  panel: Panel | null;

  setOrg: (orgId: string) => void;
  setTheme: (theme: Theme) => void;
  toggleTheme: () => void;
  setDensity: (density: Density) => void;
  setAccent: (accent: string) => void;
  setPrimary: (primary: string) => void;
  openPanel: (panel: Panel) => void;
  closePanel: () => void;
}

export const useUI = create<UIState>((set) => ({
  orgId: 'apollo-andheri',
  theme: 'light',
  density: 'comfortable',
  accent: '#E0633A',
  primary: '#1F5E50',
  panel: null,

  setOrg: (orgId) => set({ orgId }),
  setTheme: (theme) => set({ theme }),
  toggleTheme: () => set((s) => ({ theme: s.theme === 'light' ? 'dark' : 'light' })),
  setDensity: (density) => set({ density }),
  setAccent: (accent) => set({ accent }),
  setPrimary: (primary) => set({ primary }),
  openPanel: (panel) => set({ panel }),
  closePanel: () => set({ panel: null }),
}));
