import { useEffect, useMemo, useRef, useState } from 'react';
import { Table, type TableProps } from 'antd';
import type { ColumnsType, SorterResult } from 'antd/es/table/interface';
import { useTranslation } from 'react-i18next';
import { api, ApiError, type Paged, type Query } from '../api/client';
import { StateEmpty, StateError, StateLoading } from './StateViews';
import type { Row } from '../api/types';

export interface TableQuery {
  page: number;
  pageSize: number;
  sort?: string;
  dir?: 'asc' | 'desc';
}

export interface DataTableProps<T extends Row> {
  columns: ColumnsType<T>;
  /** Server endpoint; may return a Paged<T> (server paging/sorting) or a plain array (paged/sorted in the browser). */
  url?: string;
  /** Server-side filters; changing them resets to page 1. */
  params?: Query;
  /** Custom loader (e.g. POST search). */
  fetcher?: (q: TableQuery) => Promise<Paged<T> | T[]>;
  /** Static rows (already loaded). */
  data?: T[];
  /** Maps a non-standard response (e.g. {results:[...]}) into rows. */
  transform?: (raw: any) => Paged<T> | T[]; // eslint-disable-line @typescript-eslint/no-explicit-any
  rowKey?: string | ((r: T) => string | number);
  reloadKey?: unknown;
  testId?: string;
  pageSize?: number;
  rowSelection?: TableProps<T>['rowSelection'];
  expandable?: TableProps<T>['expandable'];
  rowClassName?: TableProps<T>['rowClassName'];
  defaultSort?: { sort: string; dir: 'asc' | 'desc' };
  emptyText?: string;
  onLoaded?: (rows: T[]) => void;
  size?: 'small' | 'middle' | 'large';
}

const isPaged = <T,>(x: unknown): x is Paged<T> => !!x && typeof x === 'object' && Array.isArray((x as Paged<T>).items);

const cmp = (a: unknown, b: unknown) => {
  if (a === b) return 0;
  if (a === null || a === undefined) return -1;
  if (b === null || b === undefined) return 1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b));
};

/**
 * Reusable server-side table: paging, sorting and filtering are delegated to the API.
 * Explicit states: loading, empty, error (problem detail + correlation reference) and 403 forbidden.
 */
export function DataTable<T extends Row>(props: DataTableProps<T>) {
  const { t } = useTranslation();
  const { columns, url, params, fetcher, data, transform, rowKey = 'id', reloadKey, testId, pageSize: ps = 25 } = props;
  const [q, setQ] = useState<TableQuery>({ page: 1, pageSize: ps, sort: props.defaultSort?.sort, dir: props.defaultSort?.dir });
  const [rows, setRows] = useState<T[] | undefined>(data);
  const [total, setTotal] = useState(0);
  const [clientSide, setClientSide] = useState(!!data);
  const [allRows, setAllRows] = useState<T[]>(data ?? []);
  const [loading, setLoading] = useState(!data);
  const [error, setError] = useState<ApiError>();
  const [tick, setTick] = useState(0);
  const pkey = JSON.stringify(params ?? {});
  const seq = useRef(0);
  const first = useRef(true);
  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;
  const onLoadedRef = useRef(props.onLoaded);
  onLoadedRef.current = props.onLoaded;

  // Reset to first page when filters change.
  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    setQ((x) => ({ ...x, page: 1 }));
  }, [pkey]);

  useEffect(() => {
    if (data) {
      setAllRows(data);
      setClientSide(true);
      setLoading(false);
      return;
    }
    const my = ++seq.current;
    setLoading(true);
    setError(undefined);
    const load = fetcherRef.current
      ? fetcherRef.current(q)
      : api.get<unknown>(url as string, { ...(JSON.parse(pkey) as Query), page: q.page, pageSize: q.pageSize, sort: q.sort, dir: q.dir });
    load
      .then((raw) => {
        if (my !== seq.current) return;
        const res = transform ? transform(raw) : (raw as Paged<T> | T[]);
        if (isPaged<T>(res)) {
          setClientSide(false);
          setRows(res.items);
          setTotal(res.total);
          onLoadedRef.current?.(res.items);
        } else {
          const arr = Array.isArray(res) ? res : [];
          setClientSide(true);
          setAllRows(arr);
          onLoadedRef.current?.(arr);
        }
      })
      .catch((e) => {
        if (my !== seq.current) return;
        setError(e instanceof ApiError ? e : new ApiError(0, { detail: String(e) }, 'Error'));
        setRows(undefined);
      })
      .finally(() => my === seq.current && setLoading(false));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [url, pkey, q, tick, reloadKey, data]);

  // Client-side paging/sorting when the endpoint returns a plain list.
  const view = useMemo(() => {
    if (!clientSide) return rows;
    let arr = [...allRows];
    if (q.sort) {
      const key = q.sort;
      arr.sort((a, b) => cmp(a[key], b[key]) * (q.dir === 'desc' ? -1 : 1));
    }
    return arr.slice((q.page - 1) * q.pageSize, q.page * q.pageSize);
  }, [clientSide, allRows, rows, q]);

  const count = clientSide ? allRows.length : total;

  const onChange: TableProps<T>['onChange'] = (pg, _f, sorter) => {
    const s = (Array.isArray(sorter) ? sorter[0] : sorter) as SorterResult<T>;
    setQ({
      page: pg.current ?? 1,
      pageSize: pg.pageSize ?? q.pageSize,
      sort: s?.order ? String(s.columnKey ?? s.field) : undefined,
      dir: s?.order ? (s.order === 'descend' ? 'desc' : 'asc') : undefined,
    });
  };

  const cols = useMemo(
    () =>
      columns.map((c) => ({
        ...c,
        sortOrder: c.sorter && q.sort && c.key === q.sort ? (q.dir === 'desc' ? ('descend' as const) : ('ascend' as const)) : c.sorter ? null : undefined,
      })),
    [columns, q.sort, q.dir],
  );

  let body;
  if (error) body = <StateError error={error} onRetry={() => setTick((x) => x + 1)} />;
  else if (loading && view === undefined) body = <StateLoading />;
  else if (!loading && count === 0) body = <StateEmpty text={props.emptyText} />;
  else
    body = (
      <Table<T>
        columns={cols}
        dataSource={view ?? []}
        rowKey={rowKey as never}
        loading={loading}
        onChange={onChange}
        size={props.size ?? 'middle'}
        rowSelection={props.rowSelection}
        expandable={props.expandable}
        rowClassName={props.rowClassName}
        scroll={{ x: 'max-content' }}
        pagination={{
          current: q.page,
          pageSize: q.pageSize,
          total: count,
          showSizeChanger: true,
          pageSizeOptions: [10, 25, 50, 100],
          showTotal: (n) => t('table.total', { count: n }),
          hideOnSinglePage: count <= 10,
        }}
      />
    );

  return (
    <div className="data-table" data-testid={testId} aria-busy={loading}>
      {loading && view !== undefined && count === 0 && !error ? <StateLoading /> : body}
    </div>
  );
}
