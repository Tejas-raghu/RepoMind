import type { Router } from 'vue-router';
import { isAuthenticated } from '../services/authService';

/** Restrict pages marked with requiresAuth metadata. */
export function registerAuthGuard(router: Router): void {
  router.beforeEach((to) => {
    const needsSignIn = Boolean(to.meta.requiresAuth);
    if (needsSignIn && !isAuthenticated()) {
      return { name: 'login', query: { redirect: to.fullPath } };
    }
    if (to.name === 'login' && isAuthenticated()) {
      return { name: 'tasks' };
    }
    return true;
  });
}
