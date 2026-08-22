import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AddDataHubComponent } from './add-data-hub.component';
import { LakehouseService } from './lakehouse.service';
import { FakeLakehouseService } from './test-doubles';

describe('AddDataHubComponent', () => {
  let fixture: ComponentFixture<AddDataHubComponent>;

  async function mount(canManageConnectors = true): Promise<void> {
    fixture = TestBed.createComponent(AddDataHubComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('schemas', []);
    fixture.componentRef.setInput('canManageConnectors', canManageConnectors);
    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: LakehouseService, useValue: new FakeLakehouseService() },
      ],
    });
  });

  it('offers file import and exactly the managed sources LakeHold ships', async () => {
    await mount();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Create table from file');
    expect(text).toContain('REST API');
    expect(text).toContain('gRPC stream');
    expect(text).toContain('PostgreSQL');
    expect(text).toContain('HubSpot Contacts');
    expect(text).toContain('Kafka Avro');
    expect(fixture.nativeElement.querySelectorAll('.source-grid .source-card')).toHaveLength(5);
  });

  it('filters sources and emits the selected adapter kind', async () => {
    await mount();
    const search = fixture.nativeElement.querySelector('.source-search input') as HTMLInputElement;
    search.value = 'hubspot';
    search.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    let kind: string | undefined;
    fixture.componentInstance.configureConnector.subscribe((value) => (kind = value));
    (fixture.nativeElement.querySelector('.configure') as HTMLButtonElement).click();

    expect(fixture.nativeElement.querySelectorAll('.source-grid .source-card')).toHaveLength(1);
    expect(fixture.nativeElement.textContent).not.toContain('Create table from file');
    expect(kind).toBe('hubspot');
  });
});
