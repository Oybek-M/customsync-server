import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
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

export const router = createRouter({
  history: createWebHistory('/'),
  routes,
})
