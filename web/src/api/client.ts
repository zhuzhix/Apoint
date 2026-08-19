export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    const body = await response.text()
    throw new ApiError(response.status, body || response.statusText)
  }
  return response.status === 204 ? undefined as T : await response.json() as T
}

export const api = {
  get: <T>(url: string) => request<T>(url),
  patch: <T>(url: string, body: unknown) => request<T>(url, {
    method: 'PATCH', body: JSON.stringify(body),
  }),
  post: <T>(url: string, body?: unknown) => request<T>(url, {
    method: 'POST', body: body === undefined ? undefined : JSON.stringify(body),
  }),
}

export function queryString(values: object) {
  const params = new URLSearchParams()
  Object.entries(values).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') params.set(key, String(value))
  })
  const text = params.toString()
  return text ? `?${text}` : ''
}
