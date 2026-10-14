import type { ReactNode } from 'react';
import { Descriptions, Tag, Typography } from 'antd';
import type { ColumnType } from 'antd/es/table';
import { Link } from 'react-router-dom';
import i18n from '../i18n';
import { fmtAuto } from '../utils/format';
import type { Row } from '../api/types';

/** Translated field (column/description) label: f.<key>. */
export const fl = (key: string): string => i18n.t(`f.${key}`);

/** Translates an enumeration value when a translation exists, otherwise shows the data value as-is. */
export const tv = (v: unknown): string => {
  if (v === null || v === undefined || v === '') return '—';
  const s = String(v);
  const k = `v.${s}`;
  return i18n.exists(k) ? i18n.t(k) : s;
};

const RED = ['High', 'Critical', 'ConfirmedMatch', 'Failed', 'Rejected', 'DeadLetter', 'Escalated', 'Frozen', 'Blocked', 'Error', 'ErrorFound', 'Overdue', 'Held', 'Exited'];
const ORANGE = ['Medium', 'PotentialMatch', 'UnderReview', 'PendingApproval', 'PendingClosureApproval', 'Submitted', 'Pending', 'Incomplete', 'InProgress', 'ToBeConfirmed', 'UnableToDetermine', 'Draft', 'PendingInformation', 'Stale', 'Missing', 'Prospect', 'Retrying'];
const GREEN = ['Low', 'Active', 'Completed', 'Approved', 'Published', 'Succeeded', 'Processed', 'Closed', 'FalsePositive', 'Complete', 'Passed', 'Resolved', 'Acknowledged', 'Present', 'Clear', 'Released', 'Valid', 'OK', 'Responded'];
const BLUE = ['New', 'Open', 'Assigned', 'UnderInvestigation', 'ConvertedToCase', 'NotScreened', 'Enabled'];

export const tagColor = (v: string): string | undefined => {
  if (RED.includes(v)) return 'red';
  if (ORANGE.includes(v)) return 'orange';
  if (GREEN.includes(v) || v.startsWith('Closed')) return 'green';
  if (BLUE.includes(v)) return 'blue';
  if (v === 'Disabled' || v === 'Superseded' || v === 'Retired' || v === 'Inactive' || v === 'Withdrawn') return 'default';
  return undefined;
};

export function StatusTag({ value, testId }: { value?: string | null; testId?: string }) {
  if (!value) return <span data-testid={testId}>—</span>;
  return (
    <Tag color={tagColor(value)} data-testid={testId} data-value={value}>
      {tv(value)}
    </Tag>
  );
}

export function SampleTag({ show = true }: { show?: boolean }) {
  if (!show) return null;
  return (
    <Tag color="gold" data-testid="sample-tag">
      {i18n.t('common.sampleNotRegulatory')}
    </Tag>
  );
}

const TAG_KEYS = /(^status$|Status$|^severity$|^level$|Level$|^priority$|^decision$|^outcome$|^result$)/;

const ENUM_KEYS = new Set(['customerType', 'caseType', 'reportType', 'transactionType', 'trigger', 'aggregationMethod', 'factorType', 'alertType', 'confidentiality', 'listType', 'controlType', 'ownerType', 'mappingCategory', 'ruleType', 'direction', 'actionType', 'requestType', 'matchType', 'method', 'side', 'relation', 'windowType', 'subjectType', 'rateType', 'dataType', 'issueType', 'missingDataHandling', 'recommendedAction', 'finalDecision', 'role', 'contactType', 'linkType', 'mode', 'fxStatus', 'channel', 'type']);

export const renderAuto = (key: string, v: unknown): ReactNode => {
  if (typeof v === 'string' && TAG_KEYS.test(key)) return <StatusTag value={v} />;
  if (typeof v === 'string' && ENUM_KEYS.has(key)) return tv(v);
  return fmtAuto(key, v);
};

export interface ColOpts<T> {
  title?: string;
  sort?: string | boolean;
  render?: (v: any, row: T) => ReactNode; // eslint-disable-line @typescript-eslint/no-explicit-any
  width?: number;
  link?: (row: T) => string;
  fixed?: 'left' | 'right';
}

/** Builds a table column whose header is translated via f.<key> and whose cells are auto-formatted. */
export function col<T extends Row>(key: string, opts: ColOpts<T> = {}): ColumnType<T> {
  return {
    key: typeof opts.sort === 'string' ? opts.sort : key,
    dataIndex: key.split('.'),
    title: opts.title ?? fl(key.split('.').pop() as string),
    sorter: opts.sort ? true : undefined,
    width: opts.width,
    fixed: opts.fixed,
    render: (v: unknown, row: T) => {
      const content = opts.render ? opts.render(v, row) : renderAuto(key, v);
      return opts.link ? <Link to={opts.link(row)}>{content}</Link> : content;
    },
  };
}

export const cols = <T extends Row>(...keys: Array<string | ColumnType<T>>): ColumnType<T>[] =>
  keys.map((k) => (typeof k === 'string' ? col<T>(k) : k));

/** Description list with translated labels and auto-formatted values. */
export function KV({
  data,
  keys,
  render = {},
  column = { xs: 1, sm: 1, md: 2, lg: 3 },
  testId,
  labels = {},
}: {
  labels?: Record<string, ReactNode>;
  data: Row;
  keys: string[];
  render?: Record<string, (v: any, d: Row) => ReactNode>; // eslint-disable-line @typescript-eslint/no-explicit-any
  column?: number | Record<string, number>;
  testId?: string;
}) {
  return (
    <Descriptions
      data-testid={testId}
      size="small"
      bordered
      column={column as never}
      items={keys.map((k) => ({
        key: k,
        label: labels[k] ?? fl(k),
        children: render[k] ? render[k](data[k], data) : renderAuto(k, data[k]),
      }))}
    />
  );
}

/** Read-only JSON/text block (always rendered as text, never as HTML). */
export function JsonBlock({ value, maxHeight = 320 }: { value: unknown; maxHeight?: number }) {
  let text: string;
  if (typeof value === 'string') {
    try {
      text = JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      text = value;
    }
  } else text = JSON.stringify(value, null, 2);
  return (
    <pre className="json-block" style={{ maxHeight }} tabIndex={0}>
      {text}
    </pre>
  );
}

export const Muted = ({ children }: { children: ReactNode }) => <Typography.Text type="secondary">{children}</Typography.Text>;

/** Customer display name in the current language (falls back to the other script). */
export const customerName = (c: { nameLatin?: string | null; nameArabic?: string | null }) =>
  (i18n.language === 'ar' ? c.nameArabic || c.nameLatin : c.nameLatin || c.nameArabic) || '—';
