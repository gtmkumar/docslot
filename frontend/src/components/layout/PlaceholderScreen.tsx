// Titled placeholder for routes whose full screens land in later waves. Renders
// a focusable h1 (focus management pattern 14) + an empty state so the route is
// never blank.

import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { useTranslation } from 'react-i18next';

export function PlaceholderScreen({ titleKey }: { titleKey: string }) {
  const { t } = useTranslation();
  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink">
        {t(titleKey)}
      </h1>
      <Card>
        <EmptyState title={t('empty.genericTitle')} description={t('panel.placeholderBody')} />
      </Card>
    </section>
  );
}
