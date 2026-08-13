import { ChangeDetectionStrategy, Component } from '@angular/core';
import { BrandMarkComponent } from './brand-mark.component';

/** The landing-page connectivity map, isolated so its responsive visual rules remain maintainable. */
@Component({
  selector: 'lh-landing-topology',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent],
  host: {
    class: 'topology',
    role: 'img',
    'aria-label':
      'LakeHold connectivity map: PostgreSQL, Kafka with Avro, and REST or gRPC feed governed LakeHold; people connect through enterprise SSO, agents connect through the MCP server, and consumers use SQL, Java, .NET, Go, Python, or open Parquet.',
  },
  template: `
    <div class="topology-grid" aria-hidden="true"></div>
    <span class="topology-label sources-label">Sources</span>
    <span class="topology-label outputs-label">Outputs</span>

    <svg class="topology-lines" viewBox="0 0 760 470" preserveAspectRatio="none" aria-hidden="true">
      <defs>
        <filter id="flow-glow" x="-20%" y="-20%" width="140%" height="140%">
          <feGaussianBlur stdDeviation="2.5" result="blur" />
          <feMerge>
            <feMergeNode in="blur" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>
      <g class="ingest-lines" filter="url(#flow-glow)">
        <path d="M190 145 H250 Q280 145 280 178 V222 H338" />
        <path d="M190 235 H338" />
        <path d="M190 325 H250 Q280 325 280 292 V250 H338" />
      </g>
      <g class="serve-lines" filter="url(#flow-glow)">
        <path d="M490 220 H526 Q550 220 550 176 V155 H590" />
        <path d="M490 238 H590" />
        <path d="M490 256 H526 Q550 256 550 302 V324 H590" />
      </g>
      <g class="access-lines">
        <path d="M366 100 V118 H382" />
        <path d="M508 100 V118 H472" />
        <path d="M405 326 V353 H372 V383" />
      </g>
    </svg>

    <div class="topology-node access-node sso-node">
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M12 2.5 20 6v5.7c0 4.7-3.2 8.4-8 10.3-4.8-1.9-8-5.6-8-10.3V6l8-3.5Z" />
        <path d="M12 7v8M9 11h6" />
      </svg>
      <span><strong>Enterprise SSO</strong><small>OIDC identity provider</small></span>
    </div>
    <div class="topology-node access-node mcp-node">
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <circle cx="6" cy="6" r="2.5" />
        <circle cx="18" cy="6" r="2.5" />
        <circle cx="18" cy="18" r="2.5" />
        <path d="M8.5 6h4A3.5 3.5 0 0 1 16 9.5V18M6 8.5V16a2 2 0 0 0 2 2h7.5" />
      </svg>
      <span><strong>MCP Server</strong><small>OAuth-secured agent access</small></span>
    </div>

    <div class="source-stack">
      <div class="topology-node">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <ellipse cx="12" cy="5" rx="7" ry="3" />
          <path d="M5 5v7c0 1.7 3.1 3 7 3s7-1.3 7-3V5M5 12v7c0 1.7 3.1 3 7 3s7-1.3 7-3v-7" />
        </svg>
        <span><strong>PostgreSQL</strong><small>Incremental connector</small></span>
      </div>
      <div class="topology-node">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <circle cx="6" cy="5" r="2.5" />
          <circle cx="18" cy="12" r="2.5" />
          <circle cx="6" cy="19" r="2.5" />
          <path d="m8.2 6.2 7.6 4.6M8.2 17.8l7.6-4.6M6 7.5v9" />
        </svg>
        <span><strong>Kafka + Avro</strong><small>Registry-backed events</small></span>
      </div>
      <div class="topology-node">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="M7.2 18.5H18a4 4 0 0 0 .5-8A6.5 6.5 0 0 0 6 9a4.8 4.8 0 0 0 1.2 9.5Z" />
        </svg>
        <span><strong>REST / gRPC</strong><small>Managed snapshots</small></span>
      </div>
    </div>

    <div class="lakehold-node">
      <lh-brand-mark [size]="58" />
      <strong>Governed LakeHold</strong>
      <small>DuckDB + DuckLake</small>
      <span class="node-status"><i></i> Snapshot current</span>
    </div>

    <div class="output-stack">
      <div class="topology-node">
        <span class="protocol-icon">SQL</span>
        <span><strong>SQL</strong><small>PostgreSQL endpoint</small></span>
      </div>
      <div class="topology-node">
        <span class="protocol-icon">API</span>
        <span
          ><strong>Java · .NET · Go · Python</strong><small>First-party source SDKs</small></span
        >
      </div>
      <div class="topology-node">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="M6 2.5h8l4 4V22H6zM14 2.5v5h4M9 13h6M9 17h6" />
        </svg>
        <span><strong>Parquet</strong><small>Open table storage</small></span>
      </div>
    </div>

    <div class="control-plane">
      <span class="control-plane-title">Control plane</span>
      <div>
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="M6 4h12v16H6zM9 8h6M9 12h6M9 16h4" />
        </svg>
        <strong>Catalog</strong><small>Schemas &amp; tables</small>
      </div>
      <div>
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <circle cx="9" cy="8" r="3" />
          <path
            d="M3.5 20v-2.5A4.5 4.5 0 0 1 8 13h2a4.5 4.5 0 0 1 4.5 4.5V20M16 11h5v7h-5zM17.5 11V9.5a1 1 0 0 1 2 0V11"
          />
        </svg>
        <strong>Tenant access</strong><small>SSO · roles · memberships</small>
      </div>
      <div>
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <circle cx="6" cy="6" r="2.5" />
          <circle cx="18" cy="6" r="2.5" />
          <circle cx="6" cy="18" r="2.5" />
          <circle cx="18" cy="18" r="2.5" />
          <path d="M8.5 6h7M6 8.5v7M18 8.5v7M8.5 18h7" />
        </svg>
        <strong>Connector runs</strong><small>Schedules &amp; evidence</small>
      </div>
      <div>
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="M6 3h9l3 3v15H6zM14 3v4h4M9 11h6M9 15h4" />
          <circle cx="17.5" cy="17.5" r="3" />
        </svg>
        <strong>Audit</strong><small>Events &amp; history</small>
      </div>
    </div>
  `,
  styleUrl: './landing-topology.component.css',
})
export class LandingTopologyComponent {}
