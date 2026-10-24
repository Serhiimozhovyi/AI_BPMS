import { OverlayContainer } from '@angular/cdk/overlay';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { EVENT_SOURCE_FACTORY } from '../app/executions/executions-api.service';
import { ShadowOverlayContainer, elementApiInterceptor, elementEventSourceFactory } from './element-services';

/** Застосунок `<openbp-canvas>`: без роутера й auth-перенаправлень SPA; Api — за `api-base`/`token` елемента. */
export const elementConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withFetch(), withInterceptors([elementApiInterceptor])),
    { provide: EVENT_SOURCE_FACTORY, useFactory: elementEventSourceFactory },
    { provide: OverlayContainer, useClass: ShadowOverlayContainer },
  ],
};
