import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { EjectPanelComponent } from './eject-panel.component';
import { LakehouseService } from './lakehouse.service';
import { FakeLakehouseService } from './test-doubles';

describe('EjectPanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<EjectPanelComponent>;

  const bundle = {
    bundle: '20260726T075432Z',
    createdUtc: '2026-07-26T07:54:32Z',
    snapshotId: 19,
    includesHistory: true,
    isSigned: false,
    complete: true,
    tables: [
      { schema: 'main', table: 'orders', rowCount: 2, sha256: 'abc123', bytes: 348 },
    ],
  };

  async function mount() {
    fixture = TestBed.createComponent(EjectPanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    await fixture.whenStable();
    return fixture;
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('ejects without a dry run, because an eject changes nothing', async () => {
    await mount();
    (fixture.nativeElement.querySelector('.panel-controls .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    // Unlike expiry and cleanup, this is read-only against the catalog — there is nothing to confirm.
    expect(api.lastArgs('eject')).toEqual(['demo', 'analytics', false]);
  });

  it('passes the history choice through', async () => {
    await mount();
    const checkbox = fixture.nativeElement.querySelector('.checkbox input') as HTMLInputElement;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('.panel-controls .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api.lastArgs('eject')).toEqual(['demo', 'analytics', true]);
  });

  it('re-reads the bundle list after writing one', async () => {
    await mount();
    expect(api.countOf('listEjects')).toBe(1);

    (fixture.nativeElement.querySelector('.panel-controls .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api.countOf('listEjects')).toBe(2);
  });

  it('reports what the bundle contains and that it verified', async () => {
    await mount();
    (fixture.nativeElement.querySelector('.panel-controls .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(text()).toContain('/bundles/latest');
    expect(text()).toContain('Verified.');
  });

  it('marks a bundle with no manifest as untrusted rather than merely unsigned', async () => {
    // A bundle that died partway has no manifest. "no" in the signed column would read as a normal
    // unsigned bundle, which is a different and safe thing.
    api.ejects = [{ ...bundle, complete: false }];
    await mount();

    expect(text()).toContain('incomplete');
  });

  it('expands a bundle to its per-table attestation, and collapses it again', async () => {
    api.ejects = [bundle];
    await mount();

    const open = fixture.nativeElement.querySelector('.cell-link') as HTMLButtonElement;
    open.click();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('.bundle-detail')).toBeTruthy();
    expect(text()).toContain('abc123');

    open.click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.bundle-detail')).toBeFalsy();
  });

  it('names the eject when one fails', async () => {
    api.failures.set('eject', 'row count mismatch on main.orders');
    await mount();
    (fixture.nativeElement.querySelector('.panel-controls .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    // A verification failure aborts before the manifest exists; the message names the table.
    expect(text()).toContain('Eject failed');
    expect(text()).toContain('row count mismatch');
  });

  it('carries neither the bundle list nor a failure across to another catalog', async () => {
    api.ejects = [bundle];
    await mount();
    expect(text()).toContain(bundle.bundle);

    api.failures.set('listEjects', 'boom');
    fixture.componentRef.setInput('catalog', 'other');
    await fixture.whenStable();

    // A catalog change does not destroy this panel the way a tab change does. A bundle is an
    // attestation about a specific catalog; showing one under a different catalog's name is the
    // one thing an attestation must never do.
    expect(text()).not.toContain(bundle.bundle);
    expect(text()).toContain('Could not list eject bundles');
    expect(text()).not.toContain('No eject bundles yet');

    api.failures.clear();
    fixture.componentRef.setInput('catalog', 'third');
    await fixture.whenStable();
    expect(text()).not.toContain('boom');
  });
});
