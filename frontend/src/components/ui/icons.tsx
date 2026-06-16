// Central icon lookup. Menu icon keys (from the backend nav tree) map to lucide
// components here so JSX never hardcodes an icon per route. lucide provides the
// glyph; COLOR always comes from a token (currentColor / text-* utilities) — the
// icon itself carries no color.

import {
  BarChart3,
  CalendarCheck,
  CalendarDays,
  Code,
  Handshake,
  LayoutDashboard,
  Shield,
  ShieldCheck,
  Stethoscope,
  Users,
  type LucideIcon,
} from 'lucide-react';

const ICONS: Record<string, LucideIcon> = {
  'layout-dashboard': LayoutDashboard,
  'calendar-check': CalendarCheck,
  'calendar-days': CalendarDays,
  stethoscope: Stethoscope,
  users: Users,
  'bar-chart-3': BarChart3,
  shield: Shield,
  'shield-check': ShieldCheck,
  code: Code,
  handshake: Handshake,
};

/** Resolve a backend icon key to a lucide component, falling back to a dashboard glyph. */
export function iconForKey(key: string): LucideIcon {
  return ICONS[key] ?? LayoutDashboard;
}
