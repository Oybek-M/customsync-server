import {
  createRouter,
  createWebHistory,
  type RouteRecordRaw,
  type Router,
} from 'vue-router'
import { useAuthStore } from './stores/auth'

export const routes: RouteRecordRaw[] = [
  {
    path: '/enroll',
    name: 'enroll',
    component: () => import('./views/EnrollView.vue'),
    meta: { title: 'Ulanish' },
  },
  {
    path: '/',
    name: 'dashboard',
    component: () => import('./views/DashboardView.vue'),
    meta: { title: 'Boshqaruv paneli' },
  },
  {
    path: '/devices',
    name: 'devices',
    component: () => import('./views/DevicesView.vue'),
    meta: { title: 'Qurilmalar' },
  },
  {
    path: '/archive',
    name: 'archive',
    component: () => import('./views/ArchiveView.vue'),
    meta: { title: 'Arxiv' },
  },
  {
    path: '/stats',
    name: 'stats',
    component: () => import('./views/StatsView.vue'),
    meta: { title: 'Statistika' },
  },
  {
    path: '/storage',
    name: 'storage',
    component: () => import('./views/StorageView.vue'),
    meta: { title: 'Xotira' },
  },
  {
    path: '/keys',
    name: 'keys',
    component: () => import('./views/KeysView.vue'),
    meta: { title: 'Kalitlar' },
  },
  {
    path: '/settings',
    name: 'settings',
    component: () => import('./views/SettingsView.vue'),
    meta: { title: 'Sozlamalar' },
  },
  {
    path: '/audit',
    name: 'audit',
    component: () => import('./views/AuditView.vue'),
    meta: { title: 'Audit' },
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    redirect: '/',
  },
]

let activeRouterInstance: Router | null = null

export function getActiveRouter(): Router | null {
  return activeRouterInstance ?? router
}

export function setActiveRouter(r: Router | null): void {
  activeRouterInstance = r
}

export function setupAuthGuard(r: Router): void {
  setActiveRouter(r)
  r.beforeEach((to) => {
    const authStore = useAuthStore()
    if (!authStore.isEnrolled && to.path !== '/enroll') {
      return '/enroll'
    }
    if (authStore.isEnrolled && to.path === '/enroll') {
      return '/'
    }
  })
}

export const router = createRouter({
  history: createWebHistory('/'),
  routes,
})

setupAuthGuard(router)
