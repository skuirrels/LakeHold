import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';

/**
 * Attaches the stored bearer token to API requests.
 *
 * Scoped to the app's own `/api` calls so the credential is never sent anywhere else — an absolute
 * URL to a third party passes through untouched. When no token is set the request goes out
 * unchanged, which is what keeps the workbench working against an API that does not require one.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api')) {
    return next(req);
  }

  const token = inject(AuthService).token();
  if (!token) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
