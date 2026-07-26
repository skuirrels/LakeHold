import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { SchedulePanelComponent } from './schedule-panel.component';
import { FakeLakehouseService } from './test-doubles';

describe('SchedulePanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<SchedulePanelComponent>;

  const run = {
    job: 'backup',
    tenant: 'demo',
    catalog: 'analytics',
    startedUtc: '2026-07-26T03:00:00Z',
    elapsedMilliseconds: 412.7,
    succeeded: true,
    detail: '32 tables',
  };

  async function mount() {
    fixture = TestBed.createComponent(SchedulePanelComponent);
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

  it('reads the run log without being given a tenant or catalog', async () => {
    await mount();

    // The only panel with no inputs: the log is instance-wide and the server narrows the rows to
    // what the credential may see. That is what lets it answer "did last night's backup run" across
    // every catalog at once, rather than one catalog at a time.
    expect(api.countOf('getScheduledRuns')).toBe(1);
    expect(api.lastArgs('getScheduledRuns')).toEqual([]);
  });

  it('names the job, the catalog it ran against, and how long it took', async () => {
    api.scheduledRuns = [run];
    await mount();

    expect(text()).toContain('backup');
    expect(text()).toContain('analytics');
    expect(text()).toContain('413 ms');
  });

  it('marks a failure differently from a success', async () => {
    api.scheduledRuns = [{ ...run, succeeded: false, detail: 'lease held elsewhere' }];
    await mount();

    // A run log that renders a failure like a success is a log nobody reads twice.
    expect(fixture.nativeElement.querySelector('.warn-text')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.ok-text')).toBeFalsy();
    expect(text()).toContain('lease held elsewhere');
  });

  it('explains an empty log rather than showing a bare table', async () => {
    await mount();

    expect(fixture.nativeElement.querySelector('table')).toBeFalsy();
    expect(text()).toContain('No scheduled runs recorded yet');
  });

  it('shows why the log is empty when it could not be read at all', async () => {
    api.failures.set('getScheduledRuns', 'forbidden');
    await mount();

    // Silence here would read as "the scheduler has never run", which is a different and much more
    // alarming statement than "this credential cannot see the log".
    expect(text()).toContain('Could not load scheduled runs');
    expect(text()).toContain('forbidden');
    expect(text()).not.toContain('No scheduled runs recorded yet');
  });
});
