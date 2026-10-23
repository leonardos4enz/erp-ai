export interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  errors?: Record<string, string[]>;
  traceId?: string;
}

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;
  readonly fieldErrors: Record<string, string[]>;
  readonly traceId?: string;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail || problem.title || `Error ${status}`);
    this.status = status;
    this.title = problem.title || defaultTitle(status);
    this.detail = problem.detail;
    this.fieldErrors = problem.errors ?? {};
    this.traceId = problem.traceId;
  }
}

function defaultTitle(status: number): string {
  switch (status) {
    case 400: return 'Datos inválidos';
    case 401: return 'Sesión no válida';
    case 403: return 'No tienes permiso para esta acción';
    case 404: return 'No encontrado';
    case 409: return 'Conflicto de edición';
    case 423: return 'Cuenta bloqueada';
    case 429: return 'Demasiados intentos';
    default: return status >= 500 ? 'Error del servidor' : 'Error inesperado';
  }
}

type UnauthorizedHandler = () => void;
let onUnauthorized: UnauthorizedHandler | null = null;
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null) {
  onUnauthorized = handler;
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  query?: Record<string, string | number | boolean | undefined | null>;
  signal?: AbortSignal;
  /** Do not trigger the global 401 redirect (used by login / me). */
  skipAuthRedirect?: boolean;
}

export async function api<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const method = opts.method ?? 'GET';
  let url = `/api${path}`;
  if (opts.query) {
    const qs = new URLSearchParams();
    for (const [k, v] of Object.entries(opts.query)) {
      if (v !== undefined && v !== null && v !== '') qs.set(k, String(v));
    }
    const s = qs.toString();
    if (s) url += `?${s}`;
  }

  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') headers['X-ERP-Request'] = '1';
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';

  let res: Response;
  try {
    res = await fetch(url, {
      method,
      headers,
      credentials: 'same-origin',
      body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
      signal: opts.signal,
    });
  } catch (e) {
    if ((e as Error).name === 'AbortError') throw e;
    throw new ApiError(0, { title: 'No se pudo conectar con el servidor' });
  }

  if (res.status === 401 && !opts.skipAuthRedirect && onUnauthorized) {
    onUnauthorized();
  }

  if (!res.ok) {
    let problem: ProblemDetails = {};
    try {
      const text = await res.text();
      if (text) problem = JSON.parse(text) as ProblemDetails;
    } catch {
      /* body was not JSON */
    }
    throw new ApiError(res.status, problem);
  }

  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Field errors keyed case-insensitively, so "Nombre" and "nombre" both match. */
export function fieldError(err: ApiError | null, field: string): string | undefined {
  if (!err) return undefined;
  const key = Object.keys(err.fieldErrors).find((k) => k.toLowerCase() === field.toLowerCase());
  return key ? err.fieldErrors[key]?.[0] : undefined;
}
