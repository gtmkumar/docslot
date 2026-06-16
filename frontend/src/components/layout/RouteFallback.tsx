// Suspense fallback for lazily-loaded route screens (loading hierarchy, pattern
// 13). A generic route-level skeleton: a heading bar + a content grid, shaped
// loosely like a screen so the transition to the real content is calm. Token
// colors only; reduced-motion honoured by the Skeleton primitive.

import { Skeleton } from '@/components/ui/Skeleton';

export function RouteFallback() {
  return (
    <div className="flex flex-col gap-5" role="status" aria-busy="true">
      <Skeleton className="h-7 w-48" />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-24 w-full" />
        ))}
      </div>
      <Skeleton className="h-64 w-full" />
    </div>
  );
}
