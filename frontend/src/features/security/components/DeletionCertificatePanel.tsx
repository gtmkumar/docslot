// Deletion certificate — shown ONCE after a cryptographic erasure (mirrors the
// developer portal's secret-reveal pattern). Displays the signed certificate
// (id, destroyed key ids, pre/post hashes, signature, affected record counts)
// with a download action and a "save now" warning.
//
// SECURITY: the certificate (incl. its signature) arrives via the in-store panel
// payload — NOT the URL, NOT any query cache. The `deletionCertificate` panel type
// is excluded from URL sync; the ui store is not persisted, so it cannot survive
// a refresh.

import { Download, FileCheck2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { dateTime } from '@/lib/format';
import type { ErasureResult } from '@/lib/mock/contracts';

export function DeletionCertificatePanel({ result, open, onClose }: { result: ErasureResult; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();

  const rows: { label: string; value: string; mono?: boolean }[] = [
    { label: t('security.erase.certId'), value: result.certificateId, mono: true },
    { label: t('security.erase.destroyedKeys'), value: result.destroyedKeyIds.join(', '), mono: true },
    { label: t('security.erase.preHash'), value: result.preHash, mono: true },
    { label: t('security.erase.postHash'), value: result.postHash, mono: true },
    { label: t('security.erase.signature'), value: `${result.signatureAlgorithm} · ${result.digitalSignature.slice(0, 40)}…`, mono: true },
  ];
  const recordsTotal = Object.values(result.deletedRecordCounts).reduce((a, b) => a + b, 0);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('security.erase.certEyebrow')}
      title={t('security.erase.certTitle')}
      description={t('security.erase.certWarning')}
      footer={
        <Button variant="primary" size="md" onClick={onClose}>
          {t('security.erase.certDone')}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        <div className="flex items-center gap-2 rounded-[var(--radius-sm)] bg-primary-soft px-3 py-2.5 text-[13px] text-primary">
          <FileCheck2 size={16} aria-hidden="true" />
          {t('security.erase.erased')}
        </div>

        <div className="flex items-start gap-2 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2.5 text-[12px] text-warn">
          {t('security.erase.certWarning')}
        </div>

        <dl className="flex flex-col gap-3 rounded-[var(--radius)] border border-line p-3">
          {rows.map((r) => (
            <div key={r.label}>
              <dt className="text-[11px] uppercase tracking-wider text-muted-2">{r.label}</dt>
              <dd className={`mt-0.5 break-all text-[12px] text-ink ${r.mono ? 'mono' : ''}`}>{r.value}</dd>
            </div>
          ))}
          <div>
            <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('security.erase.recordsDeleted')}</dt>
            <dd className="mono mt-0.5 text-[12px] text-ink">
              {recordsTotal} · {Object.entries(result.deletedRecordCounts).map(([k, v]) => `${k}:${v}`).join('  ')}
            </dd>
          </div>
          <div>
            <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('security.export.checksum')}</dt>
            <dd className="mono mt-0.5 text-[12px] text-ink">{dateTime(result.certifiedAt)}</dd>
          </div>
        </dl>

        <Button variant="ghost" size="md">
          <Download size={15} aria-hidden="true" />
          {t('security.erase.downloadCert')}
        </Button>
      </div>
    </SlideOver>
  );
}
