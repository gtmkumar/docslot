// Languages section — per-DEVICE display language (English / हिंदी). Available to ALL
// users regardless of settings permissions. Selecting one switches i18next, persists to
// localStorage, and sets <html lang> (all three via setLanguage). This is a browser
// preference — distinct from a user's saved account language (edited in Team & roles),
// so the copy says "on this device". Zero hex — tokens only.

import { useTranslation } from 'react-i18next';
import { Info, Languages as LanguagesIcon } from 'lucide-react';
import { setLanguage, type AppLang } from '@/app/i18n';
import { SectionCard } from './SectionCard';

const OPTIONS: { lang: AppLang; labelKey: string; subKey: string }[] = [
  { lang: 'en', labelKey: 'settings.languages.english', subKey: 'settings.languages.englishSub' },
  { lang: 'hi', labelKey: 'settings.languages.hindi', subKey: 'settings.languages.hindiSub' },
];

export function LanguagesSection() {
  const { t, i18n } = useTranslation();
  const current: AppLang = i18n.language.startsWith('hi') ? 'hi' : 'en';

  return (
    <SectionCard
      anchorId="languages"
      icon={<LanguagesIcon size={16} aria-hidden="true" />}
      title={t('settings.languages.title')}
      caption={t('settings.languages.caption')}
    >
      <div role="radiogroup" aria-label={t('settings.languages.title')} className="flex flex-col gap-2">
        {OPTIONS.map((o) => {
          const active = current === o.lang;
          return (
            <button
              key={o.lang}
              type="button"
              role="radio"
              aria-checked={active}
              onClick={() => setLanguage(o.lang)}
              className={[
                'flex items-center gap-3 rounded-[var(--radius-sm)] border px-3 py-2.5 text-left transition-colors',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary',
                active ? 'border-primary bg-primary-soft' : 'border-line hover:bg-surface-sunk',
              ].join(' ')}
            >
              <span
                aria-hidden="true"
                className={[
                  'flex h-4 w-4 shrink-0 items-center justify-center rounded-full border',
                  active ? 'border-primary' : 'border-line',
                ].join(' ')}
              >
                {active ? <span className="h-2 w-2 rounded-full bg-primary" /> : null}
              </span>
              <span className="min-w-0">
                <span className={`block text-[13px] font-medium text-ink ${o.lang === 'hi' ? 'deva' : ''}`}>
                  {t(o.labelKey)}
                </span>
                <span className="mt-0.5 block text-[12px] text-muted">{t(o.subKey)}</span>
              </span>
            </button>
          );
        })}
      </div>

      <p className="mt-3 flex items-start gap-1.5 text-[11px] text-muted-2">
        <Info size={12} aria-hidden="true" className="mt-0.5 shrink-0" />
        {t('settings.languages.deviceNote')}
      </p>
    </SectionCard>
  );
}
