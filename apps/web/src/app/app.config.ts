import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { RouteReuseStrategy, provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { connectivityInterceptor } from './connectivity/connectivity.interceptor';
import { ProjectRouteReuseStrategy } from './projects/project-route-reuse';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    { provide: RouteReuseStrategy, useClass: ProjectRouteReuseStrategy },
    provideHttpClient(withFetch(), withInterceptors([connectivityInterceptor, authInterceptor])),
  ]
};
