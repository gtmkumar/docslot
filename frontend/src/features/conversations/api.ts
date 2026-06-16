// Conversations feature: the WhatsApp-mirrored thread for a booking. Its own
// feature folder since the thread is a distinct domain (the ConversationPanel
// lives under bookings but reads through this hook via the public api surface).

import { useQuery } from '@tanstack/react-query';
import { getConversation } from '@/lib/mock';

export function useConversation(bookingId: string | undefined) {
  return useQuery({
    queryKey: ['conversation', bookingId] as const,
    queryFn: () => getConversation(bookingId ?? ''),
    enabled: Boolean(bookingId),
  });
}
