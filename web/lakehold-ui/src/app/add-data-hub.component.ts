import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { Schema, TabularImportResult } from './models';
import { TabularImportComponent } from './tabular-import.component';

interface DataSourceCard {
  id: string;
  name: string;
  kind: string;
  description: string;
  badge: string;
}

const SOURCES: DataSourceCard[] = [
  {
    id: 'rest',
    name: 'REST API',
    kind: 'Full snapshot',
    description: 'Read a bounded JSON array or NDJSON resource over HTTPS.',
    badge: 'REST',
  },
  {
    id: 'grpc',
    name: 'gRPC stream',
    kind: 'Full snapshot',
    description: 'Read the LakeHold server-streaming data source contract.',
    badge: 'gRPC',
  },
  {
    id: 'postgresql',
    name: 'PostgreSQL',
    kind: 'Incremental',
    description: 'Poll a commit-monotonic cursor and replay keyed upserts safely.',
    badge: 'PG',
  },
  {
    id: 'hubspot',
    name: 'HubSpot Contacts',
    kind: 'Incremental',
    description: 'Ingest bounded, overlapping contact windows with OAuth renewal.',
    badge: 'HS',
  },
  {
    id: 'kafkaavro',
    name: 'Kafka Avro',
    kind: 'Incremental',
    description: 'Consume Confluent-wire Avro records through governed checkpoints.',
    badge: 'KA',
  },
];

/** Focused entry point for file ingestion and the connector catalogue LakeHold actually ships. */
@Component({
  selector: 'lh-add-data-hub',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TabularImportComponent],
  template: `
    <section class="add-data-page">
      <header class="page-header">
        <div>
          <p class="eyebrow">Data</p>
          <h1>Add data</h1>
          <p>Import a local file or configure a governed source for the selected catalog.</p>
        </div>
      </header>

      <label class="source-search"
        ><span class="visually-hidden">Search data sources</span>
        <input
          type="search"
          placeholder="Search data sources…"
          [value]="search()"
          (input)="search.set($any($event.target).value)"
        />
      </label>

      @if (fileVisible()) {
        <section class="file-section">
          <h2>Files</h2>
          <article class="source-card featured">
            <span class="source-icon">↑</span>
            <div>
              <h3>Create table from file</h3>
              <p>Upload CSV, Excel or Avro and validate its inferred schema before publication.</p>
            </div>
            @if (readOnly()) {
              <span class="unavailable">Requires editor access</span>
            } @else {
              <lh-tabular-import
                [tenant]="tenant()"
                [catalog]="catalog()"
                [schemas]="schemas()"
                (imported)="imported.emit($event)"
              />
            }
          </article>
        </section>
      }

      <section>
        <div class="section-title">
          <div>
            <h2>Managed connectors</h2>
            <p>Five built-in adapters, using deployment-owned secret references.</p>
          </div>
        </div>
        <div class="source-grid">
          @for (source of visibleSources(); track source.id) {
            <article class="source-card">
              <span class="source-icon compact">{{ source.badge }}</span>
              <div>
                <h3>{{ source.name }}</h3>
                <span class="kind">{{ source.kind }}</span>
                <p>{{ source.description }}</p>
              </div>
              @if (canManageConnectors()) {
                <button
                  class="configure"
                  type="button"
                  (click)="configureConnector.emit(source.id)"
                >
                  Configure
                </button>
              } @else {
                <span class="unavailable">Requires owner access</span>
              }
            </article>
          } @empty {
            <p class="empty">No data sources match this search.</p>
          }
        </div>
      </section>
    </section>
  `,
  styleUrl: './add-data-hub.component.css',
})
export class AddDataHubComponent {
  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  readonly schemas = input.required<Schema[]>();
  readonly readOnly = input(false);
  readonly canManageConnectors = input(false);
  readonly imported = output<TabularImportResult>();
  readonly configureConnector = output<string>();
  protected readonly search = signal('');
  protected readonly fileVisible = computed(() => {
    const term = this.search().trim().toLowerCase();
    return !term || 'file csv xlsx excel avro upload create table'.includes(term);
  });
  protected readonly visibleSources = computed(() => {
    const term = this.search().trim().toLowerCase();
    return !term
      ? SOURCES
      : SOURCES.filter((source) =>
          `${source.name} ${source.kind} ${source.description}`.toLowerCase().includes(term),
        );
  });
}
