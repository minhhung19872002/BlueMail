import { accessToken, dropLocalSession, signIn } from "../auth/auth";
import { config } from "../config";

// All HTTP calls go through this module (PROMPT 12): it adds the bearer token, unwraps the
// { success, data } envelope and turns problem responses into ApiError with a stable code.

export interface ApiFieldError {
  field: string;
  code: string;
  message: string;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    public readonly title: string,
    public readonly fieldErrors: ApiFieldError[] = [],
  ) {
    super(title);
  }
}

interface Envelope<T> {
  success: boolean;
  data: T;
}

interface Problem {
  code?: string;
  title?: string;
  errors?: ApiFieldError[];
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const token = await accessToken();
  const headers: Record<string, string> = { Accept: "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";

  const res = await fetch(config().apiBase + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (res.status === 401) {
    await dropLocalSession();
    await signIn();
    throw new ApiError(401, "unauthenticated", "");
  }

  const text = await res.text();
  const json: unknown = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const p = (json ?? {}) as Problem;
    throw new ApiError(res.status, p.code ?? "http_" + res.status, p.title ?? res.statusText, p.errors ?? []);
  }
  return (json as Envelope<T>).data;
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body: unknown) => request<T>("POST", path, body),
  put: <T>(path: string, body: unknown) => request<T>("PUT", path, body),
  delete: <T>(path: string) => request<T>("DELETE", path),
};
