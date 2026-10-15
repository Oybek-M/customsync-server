import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  api,
  enroll,
  refreshTokenRequest,
  ApiError,
  clearTokens,
  setAccessTokenForTest,
  getStoredDeviceId,
  getStoredRefreshToken,
} from '../api/client'

describe('api/client.ts', () => {
  beforeEach(() => {
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
  })

  it('enroll success stores deviceId+refreshToken, sends platform: "web", code trimmed only', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          deviceId: 'dev-123',
          refreshToken: 'ref-token-secret-111',
          accessToken: 'acc-token-secret-222',
          expiresAt: new Date(Date.now() + 3600_000).toISOString(),
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } }
      )
    )

    // Code with untrimmed whitespace/dashes -- code should only be trimmed, not reformatted
    const rawCode = '  ABC-123-XYZ  '
    await enroll(rawCode, '  My Laptop  ')

    expect(fetchSpy).toHaveBeenCalledTimes(1)
    const [url, init] = fetchSpy.mock.calls[0]
    expect(url).toBe('/api/v1/devices/enroll')
    expect(init?.method).toBe('POST')
    expect(JSON.parse(init?.body as string)).toEqual({
      code: 'ABC-123-XYZ',
      name: 'My Laptop',
      platform: 'web',
    })

    // Credentials stored
    expect(getStoredDeviceId()).toBe('dev-123')
    expect(getStoredRefreshToken()).toBe('ref-token-secret-111')
    expect(localStorage.getItem('cs.deviceId')).toBe('dev-123')
    expect(localStorage.getItem('cs.refreshToken')).toBe('ref-token-secret-111')
    // Never store access token in localStorage
    expect(localStorage.getItem('cs.accessToken')).toBeNull()
  })

  it('no token string appears in thrown error messages', async () => {
    const sensitiveToken = 'super-secret-refresh-token-xyz-999'
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(
        JSON.stringify({ error: 'invalid_or_used_code', debugToken: sensitiveToken }),
        { status: 400, headers: { 'Content-Type': 'application/json' } }
      )
    )

    try {
      await enroll('bad-code', 'Web panel')
      expect.fail('Should have thrown')
    } catch (err) {
      expect(err).toBeInstanceOf(ApiError)
      const apiErr = err as ApiError
      expect(apiErr.status).toBe(400)
      expect(apiErr.code).toBe('invalid_or_used_code')
      expect(apiErr.message).not.toContain(sensitiveToken)
      expect(JSON.stringify(apiErr)).not.toContain(sensitiveToken)
    }
  })

  it('refresh 401 clears credentials; refresh 503 / network error keeps them', async () => {
    // Seed credentials
    localStorage.setItem('cs.deviceId', 'dev-123')
    localStorage.setItem('cs.refreshToken', 'refresh-token-init')
    clearTokens() // reload from storage

    // 1. Transient error: 503 Service Unavailable
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response('Service Unavailable', { status: 503 })
    )

    await expect(refreshTokenRequest()).rejects.toThrow(ApiError)
    // Credentials MUST be kept
    expect(getStoredDeviceId()).toBe('dev-123')
    expect(getStoredRefreshToken()).toBe('refresh-token-init')
    expect(localStorage.getItem('cs.deviceId')).toBe('dev-123')
    expect(localStorage.getItem('cs.refreshToken')).toBe('refresh-token-init')

    // 2. Transient error: Network error (status 0)
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new TypeError('Failed to fetch'))

    try {
      await refreshTokenRequest()
      expect.fail('Should have thrown')
    } catch (err) {
      expect(err).toBeInstanceOf(ApiError)
      expect((err as ApiError).status).toBe(0)
    }
    // Credentials still kept
    expect(getStoredRefreshToken()).toBe('refresh-token-init')

    // 3. 401 Unauthorized (invalid / revoked token)
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response('Unauthorized', { status: 401 })
    )

    await expect(refreshTokenRequest()).rejects.toThrow(ApiError)
    // Credentials MUST now be cleared!
    expect(getStoredDeviceId()).toBeNull()
    expect(getStoredRefreshToken()).toBeNull()
    expect(localStorage.getItem('cs.deviceId')).toBeNull()
    expect(localStorage.getItem('cs.refreshToken')).toBeNull()
  })

  it('two concurrent api() calls with expired token -> exactly one /refresh fetch', async () => {
    localStorage.setItem('cs.deviceId', 'dev-123')
    localStorage.setItem('cs.refreshToken', 'refresh-token-init')
    clearTokens()

    let refreshCallCount = 0
    let apiCallCount = 0

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/devices/refresh') {
        refreshCallCount++
        // Simulate small latency
        await new Promise((r) => setTimeout(r, 20))
        return new Response(
          JSON.stringify({
            refreshToken: 'refresh-token-rotated-2',
            accessToken: 'new-access-token-jwt',
            expiresAt: new Date(Date.now() + 3600_000).toISOString(),
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } }
        )
      }
      if (u.startsWith('/api/v1/data')) {
        apiCallCount++
        return new Response(JSON.stringify({ ok: true, url: u }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    // Concurrent api calls
    const [res1, res2] = await Promise.all([
      api<{ ok: boolean }>('/api/v1/data-1'),
      api<{ ok: boolean }>('/api/v1/data-2'),
    ])

    expect(res1.ok).toBe(true)
    expect(res2.ok).toBe(true)
    expect(refreshCallCount).toBe(1) // Single-flight refresh!
    expect(apiCallCount).toBe(2)
  })

  it('401 on API call -> one refresh + one retry; second 401 throws', async () => {
    localStorage.setItem('cs.deviceId', 'dev-123')
    localStorage.setItem('cs.refreshToken', 'refresh-token-init')
    clearTokens()
    setAccessTokenForTest('access-token-initial')

    let refreshCount = 0
    let apiAttempts = 0

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/devices/refresh') {
        refreshCount++
        return new Response(
          JSON.stringify({
            refreshToken: 'refresh-token-rotated-2',
            accessToken: 'access-token-fresh',
            expiresAt: new Date(Date.now() + 3600_000).toISOString(),
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } }
        )
      }
      if (u === '/api/v1/protected') {
        apiAttempts++
        if (apiAttempts === 1) {
          // First attempt gets 401
          return new Response(JSON.stringify({ error: 'token_expired' }), {
            status: 401,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        // Second attempt succeeds
        return new Response(JSON.stringify({ secret: 'data' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    const result = await api<{ secret: string }>('/api/v1/protected')
    expect(result.secret).toBe('data')
    expect(refreshCount).toBe(1)
    expect(apiAttempts).toBe(2)

    // Now test second 401 throws
    vi.restoreAllMocks()
    setAccessTokenForTest('access-token-initial-2')
    let second401Attempts = 0
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/devices/refresh') {
        return new Response(
          JSON.stringify({
            refreshToken: 'refresh-token-rotated-3',
            accessToken: 'access-token-fresh-2',
            expiresAt: new Date(Date.now() + 3600_000).toISOString(),
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } }
        )
      }
      if (u === '/api/v1/always-401') {
        second401Attempts++
        return new Response(JSON.stringify({ error: 'unauthorized' }), {
          status: 401,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    await expect(api('/api/v1/always-401')).rejects.toThrow(ApiError)
    // Attempt 1 -> 401 -> refresh -> Attempt 2 -> 401 -> throw (no 3rd attempt)
    expect(second401Attempts).toBe(2)
  })

  it('localStorage throwing -> enroll + api still work in memory, nothing thrown to UI', async () => {
    // Stub localStorage to throw on everything
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('Denied', 'SecurityError')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Denied', 'SecurityError')
    })
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Denied', 'SecurityError')
    })

    clearTokens()

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/devices/enroll') {
        return new Response(
          JSON.stringify({
            deviceId: 'mem-dev-1',
            refreshToken: 'mem-refresh-1',
            accessToken: 'mem-access-1',
            expiresAt: new Date(Date.now() + 3600_000).toISOString(),
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } }
        )
      }
      if (u === '/api/v1/mem-test') {
        return new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    // Enroll should succeed in memory without throwing to caller
    await expect(enroll('TEST-CODE', 'Web Panel')).resolves.not.toThrow()
    expect(getStoredDeviceId()).toBe('mem-dev-1')
    expect(getStoredRefreshToken()).toBe('mem-refresh-1')

    // Calling API should succeed using in-memory token
    const res = await api<{ ok: boolean }>('/api/v1/mem-test')
    expect(res.ok).toBe(true)
  })

  it('refresh token rotated in storage after refresh', async () => {
    localStorage.setItem('cs.deviceId', 'dev-123')
    localStorage.setItem('cs.refreshToken', 'token-v1')
    clearTokens()

    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          refreshToken: 'token-v2-rotated',
          accessToken: 'new-acc',
          expiresAt: new Date(Date.now() + 3600_000).toISOString(),
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } }
      )
    )

    await refreshTokenRequest()
    expect(getStoredRefreshToken()).toBe('token-v2-rotated')
    expect(localStorage.getItem('cs.refreshToken')).toBe('token-v2-rotated')
  })

  it('headers: does not override caller Content-Type, sets JSON only when body is string without content-type, passes signal', async () => {
    localStorage.setItem('cs.deviceId', 'dev-123')
    localStorage.setItem('cs.refreshToken', 'token-v1')
    clearTokens()
    setAccessTokenForTest('valid-acc')

    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(() =>
      Promise.resolve(
        new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      )
    )

    // Call with custom content-type
    const controller = new AbortController()
    await api('/api/v1/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-custom' },
      body: 'custom-data',
      signal: controller.signal,
    })

    const [, init1] = fetchSpy.mock.calls[0]
    expect(init1?.signal).toBe(controller.signal)
    const headers1 = new Headers(init1?.headers)
    expect(headers1.get('Content-Type')).toBe('application/x-custom')
    expect(headers1.get('Authorization')).toBe('Bearer valid-acc')

    // Call with string body but no Content-Type -> sets application/json
    await api('/api/v1/test2', {
      method: 'POST',
      body: JSON.stringify({ key: 'val' }),
    })
    const [, init2] = fetchSpy.mock.calls[1]
    const headers2 = new Headers(init2?.headers)
    expect(headers2.get('Content-Type')).toBe('application/json')
  })
})
