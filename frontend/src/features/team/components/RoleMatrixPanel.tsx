// Role privilege-matrix slide-over. A thin SlideOver wrapper around the shared
// RoleMatrixView (the same body the Roles & privileges master-detail pane renders
// inline, #83). Kept so a deep link `?panel=roleMatrix&id=` still restores the
// matrix; the Duplicate CTA (built-in roles) opens the duplicate slide-over.

import { useTranslation } from 'react-i18next';
import { SlideOver } from '@/components/ui/SlideOver';
import { useUI } from '@/stores/ui';
import { useRoleMatrix } from '../api';
import { RoleMatrixView } from './RoleMatrixView';

export function RoleMatrixPanel({ roleId, open, onClose }: { roleId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const openPanel = useUI((s) => s.openPanel);
  const { data: matrix } = useRoleMatrix(roleId);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.matrix.eyebrow')}
      title={matrix?.name ?? t('team.matrix.eyebrow')}
      description={t('team.matrix.description')}
    >
      <RoleMatrixView roleId={roleId} onDuplicate={(id) => openPanel({ type: 'duplicateRole', roleId: id })} />
    </SlideOver>
  );
}
