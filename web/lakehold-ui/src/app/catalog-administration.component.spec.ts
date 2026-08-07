import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CatalogAdministrationComponent } from './catalog-administration.component';
import { LakehouseService } from './lakehouse.service';
import { FakeLakehouseService } from './test-doubles';

describe('CatalogAdministrationComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<CatalogAdministrationComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    api.tenants = [
      { slug: 'acme', displayName: 'Acme', catalogs: [] },
      { slug: 'northwind', displayName: 'Northwind', catalogs: [] },
    ];
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(CatalogAdministrationComponent);
    await settle();
  }

  /** Matches the placement component's debounce; see its spec for why the wait is real. */
  async function settle(): Promise<void> {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 380));
    await fixture.whenStable();
  }

  function nameInput(): HTMLInputElement {
    return fixture.nativeElement.querySelector('input[type="text"]') as HTMLInputElement;
  }

  function submit(): void {
    const button = [...fixture.nativeElement.querySelectorAll('button')].find((candidate) =>
      (candidate as HTMLElement).textContent?.includes('Create catalog'),
    ) as HTMLButtonElement;
    button.click();
  }

  async function type(value: string): Promise<void> {
    const input = nameInput();
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle();
  }

  it('creates in the selected workspace with no placement by default', async () => {
    await mount();
    await type('ledger');
    submit();
    await fixture.whenStable();

    // Undefined, not an object of nulls: the default path sends the same body it always did.
    expect(api.lastArgs('createCatalog')).toEqual(['acme', 'ledger', undefined]);
    expect(fixture.nativeElement.textContent).toContain("Catalog 'ledger' created in acme.");
  });

  it('passes an explicit placement through when one is chosen', async () => {
    await mount();
    await type('ledger');

    const radios = [...fixture.nativeElement.querySelectorAll('input[type="radio"]')];
    (radios[1] as HTMLInputElement).checked = true;
    radios[1].dispatchEvent(new Event('change'));
    await settle();

    const path = fixture.nativeElement.querySelectorAll(
      'input[type="text"]',
    )[1] as HTMLInputElement;
    path.value = 's3://customer-bucket/lakehold/acme/ledger';
    path.dispatchEvent(new Event('input'));
    await settle();

    submit();
    await fixture.whenStable();

    expect(api.lastArgs('createCatalog')?.[2]).toEqual(
      expect.objectContaining({ dataPath: 's3://customer-bucket/lakehold/acme/ledger' }),
    );
  });

  it('announces the new catalog so the workspace picker is not left stale', async () => {
    // The workbench loads its workspace list once and does not reload it on the way back from
    // System Settings, so a catalog created here would otherwise be absent from the picker until
    // the page was reloaded — reported as created and apparently missing.
    await mount();
    const announced: unknown[] = [];
    fixture.componentInstance.created.subscribe(() => announced.push(true));

    await type('ledger');
    submit();
    await fixture.whenStable();

    expect(announced.length).toBe(1);
  });

  it('does not announce a catalog the server refused', async () => {
    api.failures.set('createCatalog', 'DataPath is already assigned to another catalog.');
    await mount();
    const announced: unknown[] = [];
    fixture.componentInstance.created.subscribe(() => announced.push(true));

    await type('ledger');
    submit();
    await fixture.whenStable();

    expect(announced.length).toBe(0);
  });

  it('keeps the form populated when the server refuses the placement', async () => {
    api.failures.set('createCatalog', 'DataPath is already assigned to another catalog.');
    await mount();
    await type('ledger');
    submit();
    await fixture.whenStable();

    // A rejected placement is usually one character from a working one; clearing the form would
    // make the operator retype a URI to find that out.
    expect(fixture.nativeElement.textContent).toContain('already assigned to another catalog');
    expect(nameInput().value).toBe('ledger');
  });

  it('says a catalog needs a workspace before it can offer the form', async () => {
    api.tenants = [];
    await mount();

    expect(fixture.nativeElement.textContent).toContain('There are no workspaces yet');
    expect(nameInput()).toBeNull();
  });

  it('will not submit an unnamed catalog', async () => {
    await mount();

    const button = [...fixture.nativeElement.querySelectorAll('button')].find((candidate) =>
      (candidate as HTMLElement).textContent?.includes('Create catalog'),
    ) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });
});
