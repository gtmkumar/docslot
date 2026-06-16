// Command palette (REACT_SKILL pattern 3). cmdk dialog, Cmd/Ctrl+K toggle.
// Sections: actions / patients / bookings. Actions are permission-filtered from
// the in-memory effective set (usePermissions) — NEVER a role check. Seeded with
// mock patients + bookings. Built on Radix Dialog under the hood (cmdk's Command
// .Dialog) so focus-trap + Esc are correct.

import { Command } from 'cmdk';
import * as Dialog from '@radix-ui/react-dialog';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import { VisuallyHidden } from './VisuallyHidden';
import { BOOKINGS, PATIENTS } from '@/lib/data';
import { maskPhone } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';

interface CommandPaletteProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function CommandPalette({ open, onOpenChange }: CommandPaletteProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);

  // Cmd/Ctrl+K global toggle.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        onOpenChange(!open);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onOpenChange]);

  const run = (fn: () => void) => {
    fn();
    onOpenChange(false);
  };

  const actions = [
    can('docslot.booking.create') && {
      key: 'newWalkIn',
      label: t('command.actionNewWalkIn'),
      onSelect: () => openPanel({ type: 'newBooking' }),
    },
    can('docslot.slot.read') && {
      key: 'bookTime',
      label: t('command.actionBookTime'),
      onSelect: () => openPanel({ type: 'bookTime' }),
    },
    can('docslot.doctor.read') && {
      key: 'roster',
      label: t('command.actionTodaysRoster'),
      onSelect: () => navigate({ to: '/doctors' }),
    },
    can('docslot.doctor.read') && {
      key: 'addDoctor',
      label: t('command.actionAddDoctor'),
      onSelect: () => openPanel({ type: 'addDoctor' }),
    },
    can('docslot.patient.update') && {
      key: 'addPatient',
      label: t('command.actionAddPatient'),
      onSelect: () => openPanel({ type: 'addPatient' }),
    },
  ].filter(Boolean) as { key: string; label: string; onSelect: () => void }[];

  return (
    <Command.Dialog
      open={open}
      onOpenChange={onOpenChange}
      label={t('command.placeholder')}
      className="fixed left-1/2 top-[18vh] z-50 w-[min(92vw,560px)] -translate-x-1/2 overflow-hidden rounded-[var(--radius)] border border-line bg-surface shadow-[var(--shadow-lg)]"
      overlayClassName="fixed inset-0 z-40 bg-ink/30 backdrop-blur-[1px]"
      contentClassName="outline-none"
    >
      {/* a11y: Radix DialogContent (rendered by cmdk) requires a Title +
          Description. They're visually hidden since the input placeholder serves
          as the visible label. Distinct, meaningful strings improve the SR
          announcement (D7). */}
      <VisuallyHidden>
        <Dialog.Title>{t('command.title')}</Dialog.Title>
        <Dialog.Description>{t('command.description')}</Dialog.Description>
      </VisuallyHidden>
      <Command.Input
        placeholder={t('command.placeholder')}
        className="w-full border-b border-line bg-transparent px-4 py-3 text-sm text-ink outline-none placeholder:text-muted-2"
      />
      <Command.List className="max-h-[50vh] overflow-y-auto p-2">
        <Command.Empty className="px-3 py-6 text-center text-[13px] text-muted">
          {t('command.empty')}
        </Command.Empty>

        {actions.length > 0 ? (
          <Command.Group
            heading={t('command.sectionActions')}
            className="px-1 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-muted-2"
          >
            {actions.map((a) => (
              <Item key={a.key} onSelect={() => run(a.onSelect)}>
                {a.label}
              </Item>
            ))}
          </Command.Group>
        ) : null}

        {can('docslot.patient.read') ? (
          <Command.Group
            heading={t('command.sectionPatients')}
            className="px-1 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-muted-2"
          >
            {PATIENTS.slice(0, 6).map((p) => (
              // PHI: searchable value uses non-PHI tokens (name + id) — the raw
              // phone must never enter the DOM/search index. Only the masked
              // phone is rendered as visible text.
              <Item key={p.id} value={`${p.name} ${p.id}`} onSelect={() => run(() => navigate({ to: '/patients' }))}>
                <span className="text-ink">{p.name}</span>
                <span className="mono ml-2 text-muted-2">{maskPhone(p.phone)}</span>
              </Item>
            ))}
          </Command.Group>
        ) : null}

        {can('docslot.booking.read') ? (
          <Command.Group
            heading={t('command.sectionBookings')}
            className="px-1 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-muted-2"
          >
            {BOOKINGS.slice(0, 6).map((b) => (
              <Item
                key={b.id}
                value={`${b.id} ${b.patient} ${b.doctorName}`}
                onSelect={() => run(() => openPanel({ type: 'manage', bookingId: b.id }))}
              >
                <span className="mono text-muted">{b.id}</span>
                <span className="ml-2 text-ink">{b.patient}</span>
                <span className="ml-2 text-muted-2">{b.doctorName}</span>
              </Item>
            ))}
          </Command.Group>
        ) : null}
      </Command.List>
    </Command.Dialog>
  );
}

function Item({
  children,
  value,
  onSelect,
}: {
  children: React.ReactNode;
  value?: string;
  onSelect: () => void;
}) {
  return (
    <Command.Item
      value={value}
      onSelect={onSelect}
      className="flex cursor-pointer items-center rounded-[var(--radius-sm)] px-2 py-2 text-sm text-ink data-[selected=true]:bg-surface-sunk"
    >
      {children}
    </Command.Item>
  );
}
