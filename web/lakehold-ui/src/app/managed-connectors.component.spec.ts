import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { ManagedConnectorsComponent } from './managed-connectors.component';
import { FakeLakehouseService, dataConnector } from './test-doubles';

describe('ManagedConnectorsComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<ManagedConnectorsComponent>;

  async function mount() {
    fixture = TestBed.createComponent(ManagedConnectorsComponent);
    fixture.componentRef.setInput('tenant', 'acme');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  function click(label: string): void {
    const button = [...fixture.nativeElement.querySelectorAll('button')].find(
      (b: HTMLButtonElement) => b.textContent?.trim() === label,
    ) as HTMLButtonElement | undefined;
    if (!button) {
      throw new Error(`No button labelled '${label}'. Buttons: ${
        [...fixture.nativeElement.querySelectorAll('button')].map((b: HTMLButtonElement) => b.textContent?.trim()).join(', ')}`);
    }
    button.click();
    fixture.detectChanges();
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('lists the catalog it was given, not a global connector list', async () => {
    api.connectors = [dataConnector({ name: 'orders' })];
    await mount();

    expect(api.lastArgs('listConnectors')).toEqual(['acme', 'analytics']);
    expect(text()).toContain('orders');
  });

  // The 202 outcome is the whole point of the acknowledgement work: the batch is durable but the
  // source was never told. Reporting it as an ordinary success is how an operator misses the one
  // state that needs them.
  it('reports a published-but-unacknowledged run as something to act on, not as a clean run', async () => {
    api.connectors = [dataConnector({ name: 'orders' })];
    api.execution = {
      runId: 7,
      status: 'published-source-acknowledgement-pending',
      rowsRead: 12,
      rowsPublished: 12,
      sourceVersion: 'orders:0:41',
      error: 'LakeHold published the batch, but source acknowledgement is pending.',
    };
    await mount();

    click('Run');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain('not acknowledged');
    expect(fixture.nativeElement.querySelector('[role="status"]')).toBeNull();
  });

  it('reports an ordinary run with the outcome the server actually returned', async () => {
    api.connectors = [dataConnector({ name: 'orders' })];
    await mount();

    click('Run');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain('succeeded');
    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeNull();
  });

  it('blocks Run and offers recovery while a batch awaits source acknowledgement', async () => {
    api.connectors = [
      dataConnector({
        name: 'orders',
        sourceAcknowledgementPendingUtc: '2026-08-08T12:00:00Z',
        sourceAcknowledgementError: 'the broker did not commit',
      }),
    ];
    await mount();

    const run = [...fixture.nativeElement.querySelectorAll('button')].find(
      (b: HTMLButtonElement) => b.textContent?.trim() === 'Run',
    ) as HTMLButtonElement;
    expect(run.disabled).toBe(true);
    expect(text()).toContain('Recover and retry');
  });

  // Retiring and pausing carry the row's optimistic version. Dropping it would let the UI overwrite
  // a change made from MCP or another tab.
  it('sends the connector version with every lifecycle operation', async () => {
    api.connectors = [dataConnector({ id: 9, version: 12 })];
    await mount();

    click('Pause');
    expect(api.lastArgs('pauseConnector')).toEqual(['acme', 'analytics', 9, 12]);

    click('Retry');
    expect(api.lastArgs('retryConnector')).toEqual(['acme', 'analytics', 9, 12]);
  });

  it('saves a Kafka Avro connector with the registry as its endpoint and its keys intact', async () => {
    await mount();

    click('Add connector');
    const component = fixture.componentInstance as unknown as {
      kind: { set(value: string): void };
      name: { set(value: string): void };
      targetTable: { set(value: string): void };
      registry: { set(value: string): void };
      brokers: { set(value: string): void };
      topic: { set(value: string): void };
      keys: { set(value: string): void };
      registryUsernameReference: { set(value: string): void };
      registryPasswordReference: { set(value: string): void };
      save(): void;
    };
    component.kind.set('kafkaavro');
    component.name.set('lifecycle');
    component.targetTable.set('lifecycle');
    component.registry.set('https://registry.example.test');
    component.brokers.set('broker.example.test:9092');
    component.topic.set('lifecycle');
    component.keys.set('id');
    component.registryUsernameReference.set('vault://registry-user');
    component.registryPasswordReference.set('vault://registry-password');
    component.save();
    await fixture.whenStable();

    const definition = api.lastArgs('createConnector')?.[2] as {
      endpointUrl: string;
      readMode: string;
      keyColumns: string[];
      adapterId: string;
      sourceSettings: { schemaRegistryUrl: string | null; kafkaTopic: string | null };
      authentication: { schemaRegistryUsernameSecretReference: string | null };
    };
    // The server refuses a definition whose endpoint names a different host from its registry, so
    // the form must not offer a way to build one.
    expect(definition.endpointUrl).toBe('https://registry.example.test');
    expect(definition.sourceSettings.schemaRegistryUrl).toBe('https://registry.example.test');
    expect(definition.adapterId).toBe('lakehold.kafka-avro');
    expect(definition.readMode).toBe('incremental');
    expect(definition.keyColumns).toEqual(['id']);
    expect(definition.authentication.schemaRegistryUsernameSecretReference).toBe('vault://registry-user');
  });

  it('refuses to save an incremental connector with no key columns instead of letting the server reject it', async () => {
    await mount();

    click('Add connector');
    const component = fixture.componentInstance as unknown as {
      kind: { set(value: string): void };
      name: { set(value: string): void };
      targetTable: { set(value: string): void };
      registry: { set(value: string): void };
      save(): void;
    };
    component.kind.set('kafkaavro');
    component.name.set('lifecycle');
    component.targetTable.set('lifecycle');
    component.registry.set('https://registry.example.test');
    component.save();
    fixture.detectChanges();

    expect(api.countOf('createConnector')).toBe(0);
    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain('keys are required');
  });

  // `endpointUrl` means something different per adapter — a resource URL, a channel address, a
  // fixed API base, a connection URI — so a value carried across an adapter change is one the
  // server refuses on its scheme rule. HubSpot's base is the case that fails most quietly.
  it('replaces the endpoint when the adapter changes rather than carrying the old meaning across', async () => {
    await mount();

    click('Add connector');
    const component = fixture.componentInstance as unknown as {
      endpoint: { (): string; set(value: string): void };
      chooseKind(value: string): void;
    };
    component.endpoint.set('https://source.example.test/v1/orders');

    component.chooseKind('hubspot');
    expect(component.endpoint()).toBe('https://api.hubapi.com');

    component.chooseKind('postgresql');
    expect(component.endpoint()).toBe('postgresql://');

    component.chooseKind('rest');
    expect(component.endpoint()).toBe('https://');
  });

  // REST and gRPC read their response whole; only the paging adapters consult it. Offering the
  // field on an adapter that ignores it is a setting an administrator can tune with no effect.
  it('offers page size only to the adapters that page', async () => {
    await mount();

    click('Add connector');
    const component = fixture.componentInstance as unknown as { chooseKind(value: string): void };
    const pageSize = () =>
      [...fixture.nativeElement.querySelectorAll('.field > span')].some(
        (span: HTMLElement) => span.textContent?.includes('Page size'),
      );

    expect(pageSize()).toBe(false);

    component.chooseKind('postgresql');
    fixture.detectChanges();
    expect(pageSize()).toBe(true);

    component.chooseKind('grpc');
    fixture.detectChanges();
    expect(pageSize()).toBe(false);
  });

  it('arms Retire before it retires, rather than deleting on one click', async () => {
    api.connectors = [dataConnector({ id: 9, version: 12, name: 'orders' })];
    await mount();

    click('Retire');
    expect(api.countOf('deleteConnector')).toBe(0);

    click('Confirm retire');
    expect(api.lastArgs('deleteConnector')).toEqual(['acme', 'analytics', 9, 12]);
  });

  // An armed row belongs to the list it was armed against. Left armed across a reload, the second
  // click retires whoever holds that id in the new list — a different connector entirely.
  it('disarms Retire when the list is reloaded underneath it', async () => {
    api.connectors = [dataConnector({ id: 9, name: 'orders' })];
    await mount();

    click('Retire');
    api.connectors = [dataConnector({ id: 9, name: 'invoices' })];
    click('Refresh');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).not.toContain('Confirm retire');
    click('Retire');
    expect(api.countOf('deleteConnector')).toBe(0);
  });

  it('surfaces a failed load instead of showing an empty catalog', async () => {
    api.failures.set('listConnectors', 'the control plane is unreachable');
    await mount();

    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain('unreachable');
  });
});
