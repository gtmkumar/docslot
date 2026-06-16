// 404 screen for unmatched routes (TanStack Router notFoundComponent). Rendered
// INSIDE the AppShell (sidebar + topbar stay usable) when an authed user hits an
// unknown path. Focusable h1 (pattern 14) + an empty-state with a primary
// "back to Overview" action. Bilingual; tokens only.

import { useNavigate } from '@tanstack/react-router';
import { Compass } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';

export function NotFoundScreen() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
        {t('notFound.title')}
      </h1>
      <Card>
        <EmptyState
          icon={<Compass size={28} aria-hidden="true" />}
          title={t('notFound.title')}
          description={t('notFound.body')}
          actionLabel={t('notFound.backHome')}
          onAction={() => void navigate({ to: '/' })}
        />
      </Card>
    </section>
  );
}
