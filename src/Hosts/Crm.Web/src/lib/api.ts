export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: unknown,
  ) {
    super(`API request failed with ${status}`);
  }
}

/** Thin fetch wrapper: same-origin, JSON in/out, throws ApiError with the problem+json body on non-2xx. */
export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', ...(init.headers ?? {}) },
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => undefined);
    throw new ApiError(response.status, problem);
  }
  return (await response.json()) as T;
}
