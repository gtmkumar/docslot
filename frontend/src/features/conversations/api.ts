// Conversations feature: the WhatsApp-mirrored thread for a booking. Its own
// feature folder since the thread is a distinct domain (the ConversationPanel
// lives under bookings but reads through this hook via the public api surface).

import { useQuery } from '@tanstack/react-query';
// Wired to the LIVE read API behind VITE_USE_REAL_API: real hits
// GET /bookings/{id}/conversation; mock serves the prototype thread (flag off).
import { getConversation } from '@/lib/backend';

export function useConversation(bookingId: string | undefined) {
  return useQuery({
    queryKey: ['conversation', bookingId] as const,
    queryFn: () => getConversation(bookingId ?? ''),
    enabled: Boolean(bookingId),
  });
}
