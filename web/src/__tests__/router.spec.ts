import { describe, it, expect } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { routes } from '../router'

describe('router', () => {
  it('unknown route resolves to the dashboard', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes,
    })

    router.push('/some/unknown/deep/path/123')
    await router.isReady()

    expect(router.currentRoute.value.path).toBe('/')
  })
})
