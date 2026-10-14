import i18n from '../i18n';

/** All timestamps are stored in UTC and displayed in the institution's local time zone. */
export const DISPLAY_TZ = 'Asia/Amman';

/** Arabic locale (Jordan) with Latin digits so references/amounts stay unambiguous; English uses en-GB. */
export const intlLocale = () => (i18n.language === 'ar' ? 'ar-JO-u-nu-latn' : 'en-GB');

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;
const DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;

const parseUtc = (v: string): Date => {
  // Timestamps without an offset are UTC by contract.
  const s = /[zZ]|[+-]\d{2}:?\d{2}$/.test(v) ? v : `${v}Z`;
  return new Date(s);
};

export const fmtDateTime = (v?: string | null): string => {
  if (!v) return '—';
  const d = parseUtc(v);
  if (isNaN(d.getTime())) return v;
  return new Intl.DateTimeFormat(intlLocale(), {
    timeZone: DISPLAY_TZ,
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(d);
};

export const fmtDate = (v?: string | null): string => {
  if (!v) return '—';
  if (DATE_ONLY.test(v)) {
    const d = new Date(`${v}T00:00:00Z`);
    return new Intl.DateTimeFormat(intlLocale(), { timeZone: 'UTC', year: 'numeric', month: 'short', day: '2-digit' }).format(d);
  }
  const d = parseUtc(v);
  if (isNaN(d.getTime())) return v;
  return new Intl.DateTimeFormat(intlLocale(), { timeZone: DISPLAY_TZ, year: 'numeric', month: 'short', day: '2-digit' }).format(d);
};

export const fmtNumber = (v?: number | null, maxFractionDigits = 4): string => {
  if (v === null || v === undefined || Number.isNaN(v)) return '—';
  return new Intl.NumberFormat(intlLocale(), { maximumFractionDigits: maxFractionDigits }).format(v);
};

export const fmtAmount = (v?: number | null, currency?: string | null): string => {
  if (v === null || v === undefined) return '—';
  const n = new Intl.NumberFormat(intlLocale(), { minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(v);
  return currency ? `${n} ${currency}` : n;
};

/** Generic value formatter used by auto-generated tables and description lists. */
export const fmtAuto = (key: string, v: unknown): string => {
  if (v === null || v === undefined || v === '') return '—';
  if (typeof v === 'boolean') return i18n.t(v ? 'common.yes' : 'common.no');
  if (typeof v === 'number') {
    if (/id$/i.test(key) || /version$/i.test(key)) return String(v);
    return fmtNumber(v);
  }
  if (typeof v === 'string') {
    if (/local$/i.test(key)) return v.replace('T', ' ');
    if (DATE_ONLY.test(v)) return fmtDate(v);
    if (DATE_TIME.test(v)) return fmtDateTime(v);
    return v;
  }
  if (Array.isArray(v)) return v.map((x) => (typeof x === 'object' ? JSON.stringify(x) : String(x))).join(', ');
  return JSON.stringify(v);
};
