import { ApplicationConfig, mergeApplicationConfig } from '@angular/core';
import { provideServerRendering, withRoutes } from '@angular/ssr';
import { appConfig } from './app.config';
import { serverRoutes } from './app.routes.server';

/**
 * Server-side additions to {@link appConfig}, used only by the build-time prerenderer. Everything the
 * browser configuration provides is inherited; this layer just registers the render-mode map so each
 * route is emitted as prerendered HTML or a client shell.
 */
const serverConfig: ApplicationConfig = {
  providers: [provideServerRendering(withRoutes(serverRoutes))],
};

export const config = mergeApplicationConfig(appConfig, serverConfig);
