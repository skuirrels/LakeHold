import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LakehouseService } from './lakehouse.service';
import { FakeLakehouseService } from './test-doubles';
import { Tenant } from './models';
import { WorkspaceAdministrationComponent } from './workspace-administration.component';

describe('WorkspaceAdministrationComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<WorkspaceAdministrationComponent>;

  beforeEach(async () => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
    fixture = TestBed.createComponent(WorkspaceAdministrationComponent);
    await fixture.whenStable();
  });

  function inputs(): HTMLInputElement[] {
    return [...fixture.nativeElement.querySelectorAll('input')];
  }

  async function type(input: HTMLInputElement, value: string): Promise<void> {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  function submit(): HTMLButtonElement {
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    button.click();
    return button;
  }

  it('creates a trimmed workspace and selects the slug as its default display name', async () => {
    const announced: Tenant[] = [];
    fixture.componentInstance.created.subscribe((workspace) => announced.push(workspace));
    const [slug] = inputs();

    await type(slug, '  northwind  ');
    submit();
    await fixture.whenStable();

    expect(api.lastArgs('createTenant')).toEqual(['northwind', 'northwind']);
    expect(announced).toEqual([{ slug: 'northwind', displayName: 'northwind', catalogs: [] }]);
    expect(fixture.nativeElement.textContent).toContain(
      "Workspace 'northwind' (northwind) created",
    );
    expect(slug.value).toBe('');
  });

  it('uses the optional display name when supplied', async () => {
    const [slug, displayName] = inputs();
    await type(slug, 'northwind');
    await type(displayName, 'Northwind Traders');

    submit();
    await fixture.whenStable();

    expect(api.lastArgs('createTenant')).toEqual(['northwind', 'Northwind Traders']);
  });

  it('rejects an invalid slug before making a request', async () => {
    const [slug] = inputs();
    await type(slug, 'North Wind');

    const button = submit();

    expect(button.disabled).toBe(true);
    expect(slug.getAttribute('aria-invalid')).toBe('true');
    expect(api.countOf('createTenant')).toBe(0);
  });

  it('keeps the form populated and announces nothing when creation is refused', async () => {
    api.failures.set('createTenant', "Workspace 'acme' already exists.");
    const announced: Tenant[] = [];
    fixture.componentInstance.created.subscribe((workspace) => announced.push(workspace));
    const [slug, displayName] = inputs();
    await type(slug, 'acme');
    await type(displayName, 'Acme');

    submit();
    await fixture.whenStable();

    expect(announced).toEqual([]);
    expect(slug.value).toBe('acme');
    expect(displayName.value).toBe('Acme');
    expect(fixture.nativeElement.textContent).toContain('already exists');
  });
});
