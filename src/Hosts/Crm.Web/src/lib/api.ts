import { refreshSession, sessionStore } from './auth';

/** Shape of a problem+json body (ProblemDetails plus the extensions every response carries). */
export type Problem = {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
};

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: Problem | undefined,
  ) {
    super(`API request failed with ${status}`);
  }
}

export type ApiFetchInit = RequestInit & { json?: unknown };

// J25: only login and refresh itself are excluded from the 401→refresh→replay handling. Logout (and every
// other /v1/auth/* call) gets the standard treatment, so a logout issued on an expired access token still
// revokes the refresh token server-side instead of silently doing nothing (C-1).
const NO_REFRESH_PATHS = new Set(['/v1/auth/login', '/v1/auth/refresh']);

function send(path: string, init: ApiFetchInit): Promise<Response> {
  const { json, headers, ...rest } = init;
  const session = sessionStore.get();
  return fetch(path, {
    ...rest,
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...(session ? { Authorization: `Bearer ${session.accessToken}` } : {}),
      ...(headers as Record<string, string> | undefined),
    },
    body: json !== undefined ? JSON.stringify(json) : rest.body,
  });
}

async function toProblem(response: Response): Promise<Problem | undefined> {
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) return undefined;
  return (await response.json().catch(() => undefined)) as Problem | undefined;
}

/**
 * Thin fetch wrapper: same-origin, JSON in/out, bearer auth from the session store.
 * A 401 on a non-auth request refreshes the session once (sharing refreshSession's
 * single in-flight promise) and replays the request; a 204 resolves to `undefined`.
 */
export async function apiFetch<T>(path: string, init: ApiFetchInit = {}): Promise<T> {
  const skipRefresh = NO_REFRESH_PATHS.has(path);
  const hadSession = sessionStore.get() !== null;

  let response = await send(path, init);

  if (response.status === 401 && !skipRefresh && hadSession) {
    const session = await refreshSession();
    if (session) {
      response = await send(path, init);
    }
  }

  if (!response.ok) {
    throw new ApiError(response.status, await toProblem(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}
