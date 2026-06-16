// Login form schema (zod). Used with react-hook-form via the dependency-free
// resolver pattern established in the bookings feature.

import { z } from 'zod';

export const loginSchema = z.object({
  email: z.string().trim().email('email'),
  password: z.string().min(1, 'password'),
});

export type LoginForm = z.infer<typeof loginSchema>;
