import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { Subject } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import { SavedQueriesPanelComponent } from './saved-queries-panel.component';
import { FakeLakehouseService, savedQuery } from './test-doubles';

describe('SavedQueriesPanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<SavedQueriesPanelComponent>;

  async function mount(readOnly = false, sql = 'SELECT 42') {
    fixture = TestBed.createComponent(SavedQueriesPanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('sql', sql);
    fixture.componentRef.setInput('readOnly', readOnly);
    await fixture.whenStable();
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  function buttons(label: string): HTMLButtonElement[] {
    return [...fixture.nativeElement.querySelectorAll('button')].filter(
      (button) => button.textContent?.trim() === label,
    ) as HTMLButtonElement[];
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('lists catalog-scoped definitions and executes the persisted id', async () => {
    api.savedQueries = [savedQuery({ id: 17 })];
    await mount();

    let executed: number | undefined;
    fixture.componentInstance.executeQuery.subscribe((id) => (executed = id));
    buttons('Run')[0].click();
    await fixture.whenStable();

    expect(text()).toContain('Revenue by country');
    expect(executed).toBe(17);
  });

  it('saves the current editor SQL with its metadata', async () => {
    await mount(false, 'SELECT * FROM events');
    buttons('Save current')[0].click();
    await fixture.whenStable();

    const inputs = fixture.nativeElement.querySelectorAll('.query-form input');
    (inputs[0] as HTMLInputElement).value = 'All events';
    inputs[0].dispatchEvent(new Event('input'));
    (fixture.nativeElement.querySelector('.query-form textarea') as HTMLTextAreaElement).value =
      'Reusable event feed';
    fixture.nativeElement.querySelector('.query-form textarea').dispatchEvent(new Event('input'));
    await fixture.whenStable();

    buttons('Save')[0].click();
    await fixture.whenStable();

    expect(api.lastArgs('createSavedQuery')).toEqual([
      'demo',
      'analytics',
      {
        name: 'All events',
        description: 'Reusable event feed',
        sql: 'SELECT * FROM events',
      },
    ]);
  });

  it('marks a published view stale when the definition revision moved on', async () => {
    api.savedQueries = [
      savedQuery({
        revision: 3,
        publishedSchema: 'main',
        publishedViewName: 'revenue_by_country',
        publishedRevision: 2,
      }),
    ];
    await mount();

    expect(text()).toContain('main.revenue_by_country');
    expect(text()).toContain('republish needed');
  });

  it('publishes with an explicit schema and view and tells the workbench to refresh', async () => {
    api.savedQueries = [savedQuery({ id: 9, name: 'Revenue by country' })];
    await mount();

    let schemaChanges = 0;
    fixture.componentInstance.schemaChanged.subscribe(() => schemaChanges++);
    buttons('Publish')[0].click();
    await fixture.whenStable();
    buttons('Publish view')[0].click();
    await fixture.whenStable();

    expect(api.lastArgs('publishSavedQuery')).toEqual([
      'demo',
      'analytics',
      9,
      1,
      'main',
      'revenue_by_country',
    ]);
    expect(schemaChanges).toBe(1);
  });

  it('lets readers run and open definitions without showing mutation controls', async () => {
    api.savedQueries = [savedQuery()];
    await mount(true);

    expect(buttons('Run')).toHaveLength(1);
    expect(buttons('Open')).toHaveLength(1);
    expect(buttons('Save current')).toHaveLength(0);
    expect(buttons('Edit')).toHaveLength(0);
    expect(buttons('Publish')).toHaveLength(0);
    expect(buttons('Delete')).toHaveLength(0);
  });

  it('cancels requests owned by the previous catalog', async () => {
    const analytics = new Subject<ReturnType<typeof savedQuery>[]>();
    const finance = new Subject<ReturnType<typeof savedQuery>[]>();
    vi.spyOn(api, 'listSavedQueries').mockImplementation((_, catalog) =>
      catalog === 'analytics' ? analytics : finance,
    );

    await mount();
    fixture.componentRef.setInput('catalog', 'finance');
    await fixture.whenStable();

    analytics.next([savedQuery({ name: 'Late analytics query' })]);
    finance.next([savedQuery({ name: 'Finance query' })]);
    await fixture.whenStable();

    expect(text()).toContain('Finance query');
    expect(text()).not.toContain('Late analytics query');
    expect(analytics.observed).toBe(false);
  });
});
