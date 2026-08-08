import { ChangeDetectionStrategy, Component, OnChanges, inject, input, signal } from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { DataConnector, DataConnectorDefinitionRequest, DataConnectorRun } from './models';

/**
 * One administrator form for every registered connector kind. It persists precisely the same
 * definition exposed to MCP; secret fields are references only and are never populated with values.
 */
@Component({ selector: 'lh-managed-connectors', changeDetection: ChangeDetectionStrategy.OnPush, templateUrl: './managed-connectors.component.html', styleUrl: './admin-page.css' })
export class ManagedConnectorsComponent implements OnChanges {
  private readonly api = inject(LakehouseService);
  readonly tenant = input.required<string>(); readonly catalog = input.required<string>();
  protected readonly connectors = signal<DataConnector[]>([]); protected readonly loading = signal(false); protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null); protected readonly notice = signal<string | null>(null); protected readonly showCreate = signal(false);
  protected readonly editing = signal<DataConnector | null>(null); protected readonly selected = signal<DataConnector | null>(null);
  protected readonly runs = signal<DataConnectorRun[]>([]); protected readonly deadLetters = signal<DataConnectorRun[]>([]); protected readonly diagnosticsLoading = signal(false);
  protected readonly kind = signal('rest'); protected readonly name = signal(''); protected readonly description = signal(''); protected readonly owner = signal('administrator'); protected readonly tags = signal('');
  protected readonly endpoint = signal('https://'); protected readonly targetSchema = signal('main'); protected readonly targetTable = signal(''); protected readonly enabled = signal(true); protected readonly schedule = signal('');
  protected readonly pageSize = signal('100'); protected readonly keys = signal(''); protected readonly required = signal(''); protected readonly notNull = signal(''); protected readonly mappings = signal('');
  protected readonly sourceTable = signal(''); protected readonly cursorColumn = signal(''); protected readonly cursorType = signal('int64'); protected readonly properties = signal('');
  protected readonly brokers = signal(''); protected readonly topic = signal(''); protected readonly group = signal('lakehold'); protected readonly registry = signal('');
  protected readonly authKind = signal('none'); protected readonly secretReference = signal(''); protected readonly usernameReference = signal(''); protected readonly passwordReference = signal('');

  ngOnChanges(): void { this.reload(); }
  protected reload(): void { if (!this.tenant() || !this.catalog()) return; this.loading.set(true); this.api.listConnectors(this.tenant(), this.catalog()).subscribe({ next: x => { this.connectors.set(x); this.loading.set(false); }, error: (e: Error) => { this.error.set(e.message); this.loading.set(false); } }); }
  protected supports(kind: string): boolean { return this.kind() === kind; }
  protected list(value: string): string[] { return value.split(',').map(x => x.trim()).filter(Boolean); }
  protected save(): void {
    if (this.busy()) return;
    const kind = this.kind(); const endpoint = kind === 'kafkaavro' ? this.registry().trim() : this.endpoint().trim();
    const incremental = ['postgresql', 'hubspot', 'kafkaavro'].includes(kind);
    const definition: DataConnectorDefinitionRequest = {
      name: this.name().trim(), description: this.description().trim() || null, owner: this.owner().trim(), tags: this.list(this.tags()), kind, endpointUrl: endpoint,
      credentialEnvironmentVariable: null, restResponseFormat: kind === 'rest' ? 'json-array' : null, targetSchema: this.targetSchema().trim() || 'main', targetTable: this.targetTable().trim(),
      minimumRows: 1, requiredColumns: this.list(this.required()), notNullColumns: this.list(this.notNull()), enabled: this.enabled(), refreshIntervalSeconds: this.schedule().trim() ? Number(this.schedule()) : null,
      adapterId: `lakehold.${kind === 'kafkaavro' ? 'kafka-avro' : kind === 'hubspot' ? 'hubspot-contacts' : kind}`, adapterVersion: 1, readMode: incremental ? 'incremental' : 'full-snapshot', schemaPolicy: this.mappings().trim() ? 'mapped-version' : 'reject', keyColumns: incremental ? this.list(this.keys()) : [],
      fieldMappings: this.parseMappings(), sourceSettings: { pageSize: Number(this.pageSize()), sourceTable: this.sourceTable().trim() || null, cursorColumn: this.cursorColumn().trim() || null, cursorType: this.cursorType().trim() || null, cursorIsCommitMonotonic: kind === 'postgresql', properties: this.list(this.properties()), kafkaBootstrapServers: this.brokers().trim() || null, kafkaTopic: this.topic().trim() || null, kafkaConsumerGroup: this.group().trim() || null, schemaRegistryUrl: this.registry().trim() || null },
      authentication: { kind: this.authKind(), secretReference: this.secretReference().trim() || null, usernameSecretReference: this.usernameReference().trim() || null, passwordSecretReference: this.passwordReference().trim() || null },
    };
    if (!definition.name || !definition.owner || !definition.endpointUrl || !definition.targetTable || (incremental && definition.keyColumns!.length === 0)) { this.error.set('Name, owner, endpoint, target table, and incremental keys are required.'); return; }
    this.busy.set(true); this.error.set(null); const existing = this.editing(); const request = existing ? this.api.updateConnector(this.tenant(), this.catalog(), existing.id, existing.version, definition) : this.api.createConnector(this.tenant(), this.catalog(), definition);
    request.subscribe({ next: () => { this.busy.set(false); this.notice.set(existing ? 'Connector updated.' : 'Connector saved.'); this.cancelEdit(); this.reload(); }, error: (e: Error) => { this.busy.set(false); this.error.set(e.message); } });
  }
  protected parseMappings(): { source: string; target: string; transform: string }[] { return this.mappings().split(',').map(value => value.trim()).filter(Boolean).map(value => { const [source, target, transform = 'none'] = value.split(':').map(x => x.trim()); return { source, target, transform }; }); }
  protected edit(c: DataConnector): void { this.editing.set(c); this.showCreate.set(true); this.kind.set(c.kind); this.name.set(c.name); this.description.set(c.description || ''); this.owner.set(c.owner); this.tags.set(c.tags.join(', ')); this.endpoint.set(c.endpointUrl); this.targetSchema.set(c.targetSchema); this.targetTable.set(c.targetTable); this.enabled.set(c.enabled); this.schedule.set(c.refreshIntervalSeconds?.toString() || ''); this.pageSize.set(c.sourceSettings.pageSize.toString()); this.keys.set(c.keyColumns.join(', ')); this.required.set(c.requiredColumns.join(', ')); this.notNull.set(c.notNullColumns.join(', ')); this.mappings.set(c.fieldMappings.map(x => `${x.source}:${x.target}:${x.transform}`).join(', ')); this.sourceTable.set(c.sourceSettings.sourceTable || ''); this.cursorColumn.set(c.sourceSettings.cursorColumn || ''); this.cursorType.set(c.sourceSettings.cursorType || 'int64'); this.properties.set(c.sourceSettings.properties.join(', ')); this.brokers.set(c.sourceSettings.kafkaBootstrapServers || ''); this.topic.set(c.sourceSettings.kafkaTopic || ''); this.group.set(c.sourceSettings.kafkaConsumerGroup || 'lakehold'); this.registry.set(c.sourceSettings.schemaRegistryUrl || ''); this.authKind.set(c.authentication.kind); this.secretReference.set(c.authentication.secretReference || ''); this.usernameReference.set(c.authentication.usernameSecretReference || ''); this.passwordReference.set(c.authentication.passwordSecretReference || ''); }
  protected cancelEdit(): void { this.showCreate.set(false); this.editing.set(null); this.kind.set('rest'); this.name.set(''); this.description.set(''); this.owner.set('administrator'); this.tags.set(''); this.endpoint.set('https://'); this.targetSchema.set('main'); this.targetTable.set(''); this.enabled.set(true); this.schedule.set(''); this.pageSize.set('100'); this.keys.set(''); this.required.set(''); this.notNull.set(''); this.mappings.set(''); this.sourceTable.set(''); this.cursorColumn.set(''); this.cursorType.set('int64'); this.properties.set(''); this.brokers.set(''); this.topic.set(''); this.group.set('lakehold'); this.registry.set(''); this.authKind.set('none'); this.secretReference.set(''); this.usernameReference.set(''); this.passwordReference.set(''); }
  protected inspect(c: DataConnector): void { this.selected.set(c); this.diagnosticsLoading.set(true); this.api.listConnectorRuns(this.tenant(), this.catalog(), c.id).subscribe({ next: x => { this.runs.set(x); this.diagnosticsLoading.set(false); }, error: (e: Error) => { this.error.set(e.message); this.diagnosticsLoading.set(false); } }); this.api.listConnectorDeadLetters(this.tenant(), this.catalog(), c.id).subscribe({ next: x => this.deadLetters.set(x), error: (e: Error) => this.error.set(e.message) }); }
  protected retry(c: DataConnector): void { this.lifecycle(this.api.retryConnector(this.tenant(), this.catalog(), c.id, c.version), `Connector '${c.name}' retried.`); }
  protected remove(c: DataConnector): void { if (confirm(`Retire '${c.name}'? This does not delete its governed table.`)) this.lifecycle(this.api.deleteConnector(this.tenant(), this.catalog(), c.id, c.version), `Connector '${c.name}' retired.`); }
  protected toggle(c: DataConnector): void { this.lifecycle(c.pausedUtc ? this.api.resumeConnector(this.tenant(), this.catalog(), c.id, c.version) : this.api.pauseConnector(this.tenant(), this.catalog(), c.id, c.version), 'Connector state updated.'); }
  protected run(c: DataConnector): void { this.lifecycle(this.api.runConnector(this.tenant(), this.catalog(), c.id), `Connector '${c.name}' ran.`); }
  private lifecycle(operation: { subscribe: Function }, notice: string): void { if (this.busy()) return; this.busy.set(true); operation.subscribe({ next: () => { this.busy.set(false); this.notice.set(notice); this.reload(); }, error: (e: Error) => { this.busy.set(false); this.error.set(e.message); } }); }
}
