import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api, ApiError, type Query } from '../api/client';
import type { LookupValue } from '../api/types';

export interface AsyncState<T> {
  data: T | undefined;
  error: ApiError | undefined;
  loading: boolean;
  reload: () => void;
  setData: (d: T) => void;
}

const toApiError = (e: unknown): ApiError =>
  e instanceof ApiError ? e : new ApiError(0, { detail: e instanceof Error ? e.message : String(e) }, 'Error');

/** Loads JSON from an API path; pass null to skip. Re-fetches when the path or query changes. */
export function useApi<T>(path: string | null, query?: Query): AsyncState<T> {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<ApiError>();
  const [loading, setLoading] = useState<boolean>(!!path);
  const [tick, setTick] = useState(0);
  const qkey = JSON.stringify(query ?? {});
  const seq = useRef(0);

  useEffect(() => {
    if (!path) {
      setLoading(false);
      return;
    }
    const my = ++seq.current;
    setLoading(true);
    setError(undefined);
    api
      .get<T>(path, JSON.parse(qkey) as Query)
      .then((d) => my === seq.current && setData(d))
      .catch((e) => {
        if (my !== seq.current) return;
        setError(toApiError(e));
        setData(undefined);
      })
      .finally(() => my === seq.current && setLoading(false));
  }, [path, qkey, tick]);

  const reload = useCallback(() => setTick((x) => x + 1), []);
  return { data, error, loading, reload, setData };
}

/** Wraps an async action with loading state. */
export function useAction<A extends unknown[], R>(fn: (...args: A) => Promise<R>) {
  const [running, setRunning] = useState(false);
  const run = useCallback(
    async (...args: A) => {
      setRunning(true);
      try {
        return await fn(...args);
      } finally {
        setRunning(false);
      }
    },
    [fn],
  );
  return { run, running };
}

const lookupCache = new Map<string, Promise<LookupValue[]>>();

/** Lookup values by category (cached per session). */
export function useLookups(category: string): LookupValue[] {
  const [items, setItems] = useState<LookupValue[]>([]);
  useEffect(() => {
    let p = lookupCache.get(category);
    if (!p) {
      p = api.get<LookupValue[]>('/api/config/lookups', { category }).catch(() => {
        lookupCache.delete(category);
        return [] as LookupValue[];
      });
      lookupCache.set(category, p);
    }
    let alive = true;
    p.then((v) => alive && setItems(v.filter((x) => x.isActive)));
    return () => {
      alive = false;
    };
  }, [category]);
  return items;
}

/** Select options from a lookup category, labelled in the current language. */
export function useLookupOptions(category: string) {
  const items = useLookups(category);
  const { i18n } = useTranslation();
  return items.map((x) => ({ value: x.code, label: i18n.language === 'ar' ? x.labelAr || x.labelEn : x.labelEn }));
}
