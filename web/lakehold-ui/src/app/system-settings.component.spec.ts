import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LakehouseService } from './lakehouse.service';
import { SystemSettingsComponent } from './system-settings.component';
import { FakeLakehouseService } from './test-doubles';

describe('SystemSettingsComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<SystemSettingsComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(SystemSettingsComponent);
    await fixture.whenStable();
  }

  it('loads the persisted MCP settings and shows the client endpoint', async () => {
    api.systemSettings.mcpPublicBaseUrl = 'https://lakehold.example.com';

    await mount();

    expect(api.countOf('getSystemSettings')).toBe(1);
    expect(fixture.nativeElement.textContent).toContain('https://lakehold.example.com/mcp');
  });

  it('uses the Workbench origin when no public base URL is configured', async () => {
    await mount();

    expect(fixture.nativeElement.textContent).toContain(`${window.location.origin}/mcp`);
  });

  it('shows an initial load error and retries successfully', async () => {
    api.failures.set('getSystemSettings', 'System settings are temporarily unavailable.');
    await mount();

    expect(fixture.nativeElement.textContent).toContain(
      'System settings are temporarily unavailable.',
    );
    expect(fixture.nativeElement.querySelector('form.settings-card')).toBeNull();
    expect(fixture.nativeElement.querySelector('lh-workspace-administration')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('lh-catalog-administration')).toBeTruthy();

    api.failures.delete('getSystemSettings');
    (fixture.nativeElement.querySelector('.retry') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api.countOf('getSystemSettings')).toBe(2);
    expect(fixture.nativeElement.querySelector('form.settings-card')).toBeTruthy();
  });

  it('limits the public base URL to the persisted maximum length', async () => {
    await mount();

    const input = fixture.nativeElement.querySelector('input[type="url"]') as HTMLInputElement;
    expect(input.maxLength).toBe(2048);
  });

  it('saves the current version and reports immediate application', async () => {
    await mount();
    const inputs = fixture.nativeElement.querySelectorAll('input');
    (inputs[0] as HTMLInputElement).checked = false;
    inputs[0].dispatchEvent(new Event('change'));
    (fixture.nativeElement.querySelector('form.settings-card') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    expect(api.lastArgs('saveSystemSettings')?.[0]).toEqual(
      expect.objectContaining({ mcpEnabled: false, version: 1 }),
    );
    expect(fixture.nativeElement.textContent).toContain('no restart is required');
  });

  it('round-trips the operator-command setting', async () => {
    api.systemSettings.mcpAllowOperatorCommands = true;
    await mount();

    const toggles = fixture.nativeElement.querySelectorAll(
      'input[type="checkbox"]',
    ) as NodeListOf<HTMLInputElement>;
    expect(toggles[2].checked).toBe(true);

    toggles[2].checked = false;
    toggles[2].dispatchEvent(new Event('change'));
    (fixture.nativeElement.querySelector('form.settings-card') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    expect(api.lastArgs('saveSystemSettings')?.[0]).toEqual(
      expect.objectContaining({ mcpAllowOperatorCommands: false }),
    );
  });

  it('does not hide a save conflict', async () => {
    api.failures.set('saveSystemSettings', 'System settings changed since they were loaded.');
    await mount();

    (fixture.nativeElement.querySelector('form.settings-card') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain(
      'System settings changed since they were loaded.',
    );
  });

  it('creates another workspace, refreshes the shell, and selects it for catalog creation', async () => {
    await mount();
    const announced: unknown[] = [];
    fixture.componentInstance.resourcesChanged.subscribe(() => announced.push(true));
    const workspace = fixture.nativeElement.querySelector(
      'lh-workspace-administration',
    ) as HTMLElement;
    const [slug, displayName] = workspace.querySelectorAll('input');
    slug.value = 'northwind';
    slug.dispatchEvent(new Event('input'));
    displayName.value = 'Northwind Traders';
    displayName.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    (workspace.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api.lastArgs('createTenant')).toEqual(['northwind', 'Northwind Traders']);
    expect(announced).toEqual([true]);
    const catalogWorkspace = fixture.nativeElement.querySelector(
      'lh-catalog-administration select',
    ) as HTMLSelectElement;
    expect(catalogWorkspace.value).toBe('northwind');
  });
});
