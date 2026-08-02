import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { NavigationEnd, NavigationStart, Router } from '@angular/router';

const ANALYTICS_CONFIG_URL = '/analytics-config.json';
const ANALYTICS_SCRIPT_ID = 'lakehold-google-analytics';
const MEASUREMENT_ID_PATTERN = /^G-[A-Z0-9]+$/;

const WEBSITE_PATHS = new Set([
  '/',
  '/compare',
  '/docs',
  '/docs/linq-workbench',
  '/provider',
  '/provider/docs',
]);

interface AnalyticsConfig {
  readonly measurementId?: unknown;
}

interface AnalyticsWindow extends Window {
  dataLayer?: IArguments[];
  gtag?: (...args: unknown[]) => void;
}

/**
 * Loads Google Analytics only for the public website routes.
 *
 * The same Angular artifact also powers the private Workbench. The measurement ID is therefore
 * fetched lazily from an endpoint exposed only by the website nginx configuration, rather than
 * being compiled into the shared HTML. Collection is disabled again before a Workbench page view
 * can be sent when a visitor moves there from the public site.
 */
@Injectable({ providedIn: 'root' })
export class GoogleAnalyticsService {
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly platformId = inject(PLATFORM_ID);

  private initialized = false;
  private navigationSequence = 0;
  private measurementId: string | null = null;
  private measurementIdRequest: Promise<string | null> | null = null;
  private configured = false;

  /** Starts route-aware tracking. Called once by the application shell. */
  init(): void {
    if (this.initialized || !isPlatformBrowser(this.platformId)) {
      return;
    }

    this.initialized = true;
    this.router.events.subscribe((event) => {
      if (event instanceof NavigationStart) {
        // Set the boundary before Angular changes browser history. GA4 Enhanced Measurement owns
        // SPA page views, so it sees public history changes once and is already disabled when the
        // destination is the Workbench.
        this.setCollectionDisabled(!isWebsitePath(analyticsPath(event.url)));
      } else if (event instanceof NavigationEnd) {
        void this.onNavigation(event.urlAfterRedirects);
      }
    });
  }

  private async onNavigation(url: string): Promise<void> {
    const sequence = ++this.navigationSequence;
    const path = analyticsPath(url);

    if (!isWebsitePath(path)) {
      this.setCollectionDisabled(true);
      return;
    }

    const measurementId = await this.getMeasurementId();
    if (sequence !== this.navigationSequence || measurementId === null) {
      return;
    }

    this.measurementId = measurementId;
    this.setCollectionDisabled(false);
    this.configure(measurementId);
  }

  private getMeasurementId(): Promise<string | null> {
    this.measurementIdRequest ??= this.fetchMeasurementId();
    return this.measurementIdRequest;
  }

  private async fetchMeasurementId(): Promise<string | null> {
    try {
      const response = await globalThis.fetch(ANALYTICS_CONFIG_URL, {
        credentials: 'same-origin',
      });
      if (!response.ok) {
        return null;
      }

      const config = (await response.json()) as AnalyticsConfig;
      const measurementId =
        typeof config.measurementId === 'string' ? config.measurementId.trim() : '';

      return MEASUREMENT_ID_PATTERN.test(measurementId) ? measurementId : null;
    } catch {
      // Local development, private Workbench deployments, and content blockers may not expose the
      // config endpoint. Analytics is optional, so the application should remain silent and usable.
      return null;
    }
  }

  private configure(measurementId: string): void {
    if (this.configured) {
      return;
    }

    const analyticsWindow = this.analyticsWindow();
    if (analyticsWindow === null) {
      return;
    }

    analyticsWindow.dataLayer ??= [];
    analyticsWindow.gtag ??= function (..._args: unknown[]): void {
      analyticsWindow.dataLayer?.push(arguments);
    };

    analyticsWindow.gtag('js', new Date());
    // The initial config call records the current page. GA4 Enhanced Measurement records later
    // Angular history changes; sending another manual page_view here would double-count them.
    analyticsWindow.gtag('config', measurementId);

    const script = this.document.createElement('script');
    script.id = ANALYTICS_SCRIPT_ID;
    script.async = true;
    script.src = `https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(measurementId)}`;
    this.document.head.appendChild(script);
    this.configured = true;
  }

  private setCollectionDisabled(disabled: boolean): void {
    if (this.measurementId === null) {
      return;
    }

    const analyticsWindow = this.analyticsWindow();
    if (analyticsWindow !== null) {
      Reflect.set(analyticsWindow, `ga-disable-${this.measurementId}`, disabled);
    }
  }

  private analyticsWindow(): AnalyticsWindow | null {
    return this.document.defaultView as AnalyticsWindow | null;
  }
}

/** Public for the focused boundary test: only these routes belong to the website. */
export function isWebsitePath(path: string): boolean {
  return WEBSITE_PATHS.has(path);
}

function analyticsPath(url: string): string {
  const path = url.split(/[?#]/, 1)[0] || '/';
  return path.length > 1 ? path.replace(/\/+$/, '') : path;
}
