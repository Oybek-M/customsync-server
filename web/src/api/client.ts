/**
 * CustomSync API client.
 *
 * Xususiyatlari:
 *  - Token yangilash (refresh token rotation) va eskirganda avtomatik refresh
 *  - Single-flight refresh: parallel so'rovlar bitta refresh va'dasini ulashadi
 *  - 401 da bir marta tokenni yangilab qayta urinish (retry)
 *  - O'tkinchi xatolar (5xx, network error) da tokenni o'chirib yubormaslik (faqat 401 da o'chiriladi)
 *  - Xavfsiz storage: localStorage xato berganda (private browsing) sessiya xotirada ishlayveradi
 *  - Hech qanday token xato xabariga yoki konsolga chiqmaydi
 */

// Server JSON'ni snake_case'da yozadi va o'qiydi (Program.cs: SnakeCaseLower).
interface RefreshResponse {
  refresh_token: string
  access_token: string
  expires_at: string
}

interface EnrollResponse extends RefreshResponse {
  device_id: string
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string) {
    super(code)
    this.status = status
    this.code = code
    this.name = 'ApiError'
  }
}

export const storage = {
  get(key: string): string | null {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  },
  set(key: string, value: string): void {
    try {
      localStorage.setItem(key, value)
    } catch {
      // storage unavailable
    }
  },
  remove(key: string): void {
    try {
      localStorage.removeItem(key)
    } catch {
      // storage unavailable
    }
  },
}

let memoryDeviceId: string | null = storage.get('cs.deviceId')
let memoryRefreshToken: string | null = storage.get('cs.refreshToken')
let accessToken: string | null = null
let expiresAt = 0
let refreshPromise: Promise<string> | null = null

type AuthListener = (enrolled: boolean) => void
const authListeners = new Set<AuthListener>()

export function onAuthChange(listener: AuthListener): () => void {
  authListeners.add(listener)
  return () => authListeners.delete(listener)
}

function notifyAuthChange(enrolled: boolean) {
  for (const listener of authListeners) {
    try {
      listener(enrolled)
    } catch {
      // listener error shouldn't break auth flow
    }
  }
}

export function getStoredDeviceId(): string | null {
  return memoryDeviceId ?? storage.get('cs.deviceId')
}

export function getStoredRefreshToken(): string | null {
  return memoryRefreshToken ?? storage.get('cs.refreshToken')
}

export function setCredentials(deviceId: string, refreshToken: string): void {
  memoryDeviceId = deviceId
  memoryRefreshToken = refreshToken
  storage.set('cs.deviceId', deviceId)
  storage.set('cs.refreshToken', refreshToken)
  notifyAuthChange(true)
}

export function clearCredentials(): void {
  memoryDeviceId = null
  memoryRefreshToken = null
  accessToken = null
  expiresAt = 0
  refreshPromise = null
  storage.remove('cs.deviceId')
  storage.remove('cs.refreshToken')
  notifyAuthChange(false)
}

/** Testlar uchun: xotiradagi holatni qayta yuklash/tozalash */
export function clearTokens(): void {
  memoryDeviceId = storage.get('cs.deviceId')
  memoryRefreshToken = storage.get('cs.refreshToken')
  accessToken = null
  expiresAt = 0
  refreshPromise = null
}

/** Testlar uchun: access tokenni to'g'ridan-to'g'ri o'rnatish */
export function setAccessTokenForTest(token: string | null, exp: number = Date.now() + 3600_000): void {
  accessToken = token
  expiresAt = exp
}

async function parseErrorCode(response: Response): Promise<string> {
  try {
    const contentType = response.headers.get('content-type') || ''
    if (contentType.includes('application/json')) {
      const data = await response.json()
      if (data && typeof data === 'object' && typeof data.error === 'string') {
        return data.error
      }
    }
  } catch {
    // JSON bo'lmasa o'tkazib yuboramiz
  }

  switch (response.status) {
    case 400: return 'bad_request'
    case 401: return 'unauthorized'
    case 403: return 'forbidden'
    case 404: return 'not_found'
    case 500: return 'server_error'
    case 502: return 'bad_gateway'
    case 503: return 'service_unavailable'
    default: return `http_${response.status}`
  }
}

async function doRefresh(): Promise<string> {
  const deviceId = getStoredDeviceId()
  const refreshToken = getStoredRefreshToken()

  if (!deviceId || !refreshToken) {
    clearCredentials()
    throw new ApiError(401, 'not_enrolled')
  }

  let response: Response
  try {
    response = await fetch('/api/v1/devices/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ device_id: deviceId, refresh_token: refreshToken }),
    })
  } catch (err) {
    if (err instanceof ApiError) throw err
    throw new ApiError(0, 'network_error')
  }

  if (response.status === 401) {
    clearCredentials()
    throw new ApiError(401, 'unauthorized')
  }

  if (!response.ok) {
    const code = await parseErrorCode(response)
    throw new ApiError(response.status, code)
  }

  const data = (await response.json()) as RefreshResponse
  memoryRefreshToken = data.refresh_token
  storage.set('cs.refreshToken', data.refresh_token)
  accessToken = data.access_token
  expiresAt = Date.parse(data.expires_at)

  return accessToken!
}

export function refreshTokenRequest(): Promise<string> {
  if (refreshPromise) {
    return refreshPromise
  }

  refreshPromise = (async () => {
    try {
      return await doRefresh()
    } finally {
      refreshPromise = null
    }
  })()

  return refreshPromise
}

export async function ensureToken(): Promise<string> {
  if (accessToken && Date.now() < expiresAt - 30_000) {
    return accessToken
  }
  return refreshTokenRequest()
}

export async function api<T>(
  path: string,
  options: RequestInit & { signal?: AbortSignal } = {},
  isRetry = false
): Promise<T> {
  const token = await ensureToken()

  const headers = new Headers(options.headers)
  headers.set('Authorization', `Bearer ${token}`)
  if (typeof options.body === 'string' && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  let response: Response
  try {
    response = await fetch(path, {
      ...options,
      headers,
    })
  } catch (err) {
    if (err instanceof ApiError) throw err
    // Bekor qilingan so'rov -- tarmoq xatosi emas: aks holda tez yozilgan
    // qidiruvda UI "server bilan aloqa yo'q" deb ko'rsatardi.
    if (err instanceof DOMException && err.name === 'AbortError') throw err
    throw new ApiError(0, 'network_error')
  }

  if (response.status === 401 && !isRetry) {
    accessToken = null
    expiresAt = 0
    await refreshTokenRequest()
    return api<T>(path, options, true)
  }

  if (!response.ok) {
    const code = await parseErrorCode(response)
    throw new ApiError(response.status, code)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export async function enroll(code: string, name = 'Web panel'): Promise<void> {
  const trimmedCode = code.trim()
  const trimmedName = name.trim() || 'Web panel'

  let response: Response
  try {
    response = await fetch('/api/v1/devices/enroll', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        code: trimmedCode,
        name: trimmedName,
        platform: 'web',
      }),
    })
  } catch (err) {
    if (err instanceof ApiError) throw err
    throw new ApiError(0, 'network_error')
  }

  if (!response.ok) {
    const errCode = await parseErrorCode(response)
    throw new ApiError(response.status, errCode)
  }

  const data = (await response.json()) as EnrollResponse
  setCredentials(data.device_id, data.refresh_token)
  accessToken = data.access_token
  expiresAt = Date.parse(data.expires_at)
}
