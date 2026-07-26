import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { BackupsPanelComponent } from './backups-panel.component';
import { LakehouseService } from './lakehouse.service';
import { FakeLakehouseService } from './test-doubles';

describe('BackupsPanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<BackupsPanelComponent>;

  const complete = {
    generation: '20260726T075432Z',
    createdUtc: '2026-07-26T07:54:32Z',
    snapshotId: 19,
    tableCount: 32,
    complete: true,
  };

  async function mount() {
    fixture = TestBed.createComponent(BackupsPanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    await fixture.whenStable();
    return fixture;
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  async function openRestoreForm() {
    await mount();
    (fixture.nativeElement.querySelector('.row-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('says so plainly when there is nothing to restore from', async () => {
    await mount();
    expect(text()).toContain('No backup generations');
  });

  it('offers no restore for a generation with no manifest', async () => {
    // An interrupted export missing its delete-file table would silently reinstate deleted rows.
    // The server refuses it; the UI should not invite the attempt in the first place.
    api.backups = [{ ...complete, complete: false }];
    await mount();

    expect(fixture.nativeElement.querySelector('.row-btn')).toBeFalsy();
    expect(text()).toContain('incomplete');
  });

  it('proposes a target naming the catalog and the generation', async () => {
    api.backups = [complete];
    await openRestoreForm();

    const target = fixture.nativeElement.querySelector('.restore-form input') as HTMLInputElement;
    expect(target.value).toBe('analytics-restored-20260726T075432Z.ducklake');
  });

  it('warns that the target must not already exist', async () => {
    api.backups = [complete];
    await openRestoreForm();

    // Restore never overwrites. The form should say so before the server has to.
    expect(text()).toContain('never overwrites');
  });

  it('restores the generation the form was opened for', async () => {
    api.backups = [complete];
    await openRestoreForm();
    (fixture.nativeElement.querySelector('.restore-form .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api.lastArgs('restoreBackup')).toEqual([
      'demo',
      'analytics',
      '20260726T075432Z',
      'analytics-restored-20260726T075432Z.ducklake',
    ]);
  });

  it('reports where the rebuilt catalog went, and that the original is untouched', async () => {
    api.backups = [complete];
    await openRestoreForm();
    (fixture.nativeElement.querySelector('.restore-form .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(text()).toContain('restored.ducklake');
    expect(text()).toContain('untouched');
  });

  it('forwards the refusal verbatim under a heading naming the restore', async () => {
    api.backups = [complete];
    await openRestoreForm();

    api.failures.set('restoreBackup', "'x.ducklake' already exists. Restore never overwrites");
    (fixture.nativeElement.querySelector('.restore-form .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    // The server names which of the two refusals it was; a generic "failed" would lose that.
    expect(text()).toContain('Restore failed');
    expect(text()).toContain('already exists');
  });

  it('cancelling closes the form without restoring anything', async () => {
    api.backups = [complete];
    await openRestoreForm();

    const buttons = [...fixture.nativeElement.querySelectorAll('.restore-actions .btn')];
    (buttons.at(-1) as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('.restore-form')).toBeFalsy();
    expect(api.countOf('restoreBackup')).toBe(0);
  });

  it('re-reads the list when the workbench says a backup committed', async () => {
    await mount();
    expect(api.countOf('listBackups')).toBe(1);

    // The workbench calls this through a viewChild after the Backup button commits.
    fixture.componentInstance.reload();
    await fixture.whenStable();

    expect(api.countOf('listBackups')).toBe(2);
  });

  it('carries neither a generation list nor a failure across to another catalog', async () => {
    api.backups = [complete];
    await mount();
    expect(text()).toContain(complete.generation);

    api.failures.set('listBackups', 'boom');
    fixture.componentRef.setInput('catalog', 'other');
    await fixture.whenStable();

    // A catalog change does not destroy this panel the way a tab change does. Leaving one catalog's
    // generations listed under another catalog's name would offer a Restore… that rebuilds the
    // wrong thing.
    expect(text()).not.toContain(complete.generation);
    expect(text()).toContain('Could not list backups');
    expect(text()).not.toContain('No backup generations');

    api.failures.clear();
    fixture.componentRef.setInput('catalog', 'third');
    await fixture.whenStable();
    expect(text()).not.toContain('boom');
  });
});
