// Shared section shell for the Settings screen — a Card with an icon, a title, and a
// one-line caption (mirrors the Security-tab SectionCard). The <h2> carries the rail
// anchor id (`section-<param>`) and is focusable (tabIndex -1) so the section rail can
// move focus + scroll it into view on navigation. Zero hex — tokens only.

import type { ReactNode } from 'react';
import { Card } from '@/components/ui/Card';

export function SectionCard({
  anchorId,
  icon,
  title,
  caption,
  action,
  children,
}: {
  /** URL section param (e.g. 'organization') → DOM id `section-organization`. */
  anchorId: string;
  icon: ReactNode;
  title: string;
  caption: string;
  /** Optional trailing control (e.g. a status chip). */
  action?: ReactNode;
  children: ReactNode;
}) {
  return (
    // scroll-mt keeps the focused heading clear of the top edge when scrolled into view.
    <Card className="scroll-mt-6 p-4 sm:p-5">
      <div className="mb-4 flex items-start justify-between gap-3">
        <div className="flex items-start gap-2.5">
          <span className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-primary-soft text-primary">
            {icon}
          </span>
          <div className="min-w-0">
            <h2 id={`section-${anchorId}`} tabIndex={-1} className="text-sm font-semibold text-ink outline-none">
              {title}
            </h2>
            <p className="mt-0.5 text-[12px] text-muted">{caption}</p>
          </div>
        </div>
        {action ? <div className="shrink-0">{action}</div> : null}
      </div>
      {children}
    </Card>
  );
}
