/** Typed fetch wrapper: adds the bearer token, parses RFC 7807 problem details into ApiError. */

export const TOKEN_KEY = 'aml.token';

export class ApiError extends Error {
  status: number;
  detail?: string;
  title?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;

  constructor(status: number, body: Partial<ProblemDetails> | null, fallback: string) {
    super(body?.detail || body?.title || fallback);
    this.status = status;
    this.detail = body?.detail ?? undefined;
    this.title = body?.title ?? undefined;
    this.correlationId = body?.correlationId ?? undefined;
    this.errors = body?.errors ?? undefined;
  }
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export type Query = Record<string, string | number | boolean | null | undefined | Array<string | number>>;

let unauthorizedHandler: (() => void) | null = null;
export const onUnauthorized = (fn: () => void) => {
  unauthorizedHandler = fn;
};

export const getToken = (): string | null => {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
};

export const qs = (q?: Query): string => {
  if (!q) return '';
  const p = new URLSearchParams();
  Object.entries(q).forEach(([k, v]) => {
    if (v === undefined || v === null || v === '') return;
    if (Array.isArray(v)) v.forEach((x) => p.append(k, String(x)));
    else p.append(k, String(v));
  });
  const s = p.toString();
  return s ? `?${s}` : '';
};

async function request<T>(method: string, path: string, body?: unknown, opts: { raw?: boolean; form?: FormData } = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json, application/problem+json' };
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  let payload: BodyInit | undefined;
  if (opts.form) payload = opts.form;
  else if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }
  let res: Response;
  try {
    res = await fetch(path, { method, headers, body: payload });
  } catch (e) {
    throw new ApiError(0, { detail: 'Network error: the server could not be reached.' }, 'Network error');
  }
  if (!res.ok) {
    let problem: ProblemDetails | null = null;
    try {
      const text = await res.text();
      problem = text ? (JSON.parse(text) as ProblemDetails) : null;
    } catch {
      problem = null;
    }
    if (res.status === 401 && token && unauthorizedHandler) unauthorizedHandler();
    // Never surface stack traces: only the problem detail/title is kept.
    throw new ApiError(res.status, problem, `HTTP ${res.status}`);
  }
  if (opts.raw) return res as unknown as T;
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(path: string, q?: Query) => request<T>('GET', path + qs(q)),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body ?? {}),
  del: <T>(path: string) => request<T>('DELETE', path),
  upload: <T>(path: string, file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    return request<T>('POST', path, undefined, { form: fd });
  },
  /** Downloads a binary response and saves it via an object URL. */
  download: async (path: string, fallbackName: string) => {
    const res = await request<Response>('GET', path, undefined, { raw: true });
    const blob = await res.blob();
    const cd = res.headers.get('content-disposition') ?? '';
    const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
    const name = m ? decodeURIComponent(m[1]) : fallbackName;
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  },
};

export const isApiError = (e: unknown): e is ApiError => e instanceof ApiError;
