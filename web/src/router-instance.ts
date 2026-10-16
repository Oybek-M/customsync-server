import type { Router } from 'vue-router'

let activeRouterInstance: Router | null = null
let defaultRouterInstance: Router | null = null

export function setDefaultRouter(r: Router): void {
  defaultRouterInstance = r
}

export function getActiveRouter(): Router | null {
  return activeRouterInstance ?? defaultRouterInstance
}

export function setActiveRouter(r: Router | null): void {
  activeRouterInstance = r
}
