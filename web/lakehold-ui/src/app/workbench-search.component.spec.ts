import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WorkbenchSearchComponent } from './workbench-search.component';
import { savedQuery } from './test-doubles';

describe('WorkbenchSearchComponent', () => {
  let fixture: ComponentFixture<WorkbenchSearchComponent>;

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(WorkbenchSearchComponent);
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('schemas', [
      {
        name: 'main',
        tables: [
          {
            name: 'orders',
            kind: 'BASE TABLE',
            columns: [{ name: 'customer_id', dataType: 'BIGINT', isNullable: false }],
          },
        ],
      },
    ]);
    fixture.componentRef.setInput('queries', [
      savedQuery({ name: 'Customer revenue', sql: 'select 42' }),
    ]);
    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  it('finds a table by column metadata and opens its inspector', async () => {
    await mount();
    const input = fixture.nativeElement.querySelector('.search-box input') as HTMLInputElement;
    expect(document.activeElement).toBe(input);
    input.value = 'customer_id';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    let selected: { schemaName: string; tableName: string } | undefined;
    fixture.componentInstance.inspectTable.subscribe((table) => (selected = table));
    (fixture.nativeElement.querySelector('.search-result') as HTMLButtonElement).click();

    expect(selected).toEqual({ schemaName: 'main', tableName: 'orders' });
  });

  it('finds saved queries and returns source to the editor', async () => {
    await mount();
    const input = fixture.nativeElement.querySelector('.search-box input') as HTMLInputElement;
    input.value = 'customer revenue';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    let source: { language: string; source: string } | undefined;
    fixture.componentInstance.openSource.subscribe((value) => (source = value));
    (fixture.nativeElement.querySelector('.search-result') as HTMLButtonElement).click();

    expect(source).toEqual({ language: 'sql', source: 'select 42' });
  });

  it('tracks repeated history records by audit id and clears a dismissed search', async () => {
    await mount();
    fixture.componentRef.setInput(
      'history',
      [1, 2].map((id) => ({
        id,
        catalogName: 'analytics',
        sql: 'select repeated_value',
        language: 'sql',
        startedUtc: '2026-08-21T12:00:00Z',
        elapsedMilliseconds: 1,
        rowCount: 1,
        succeeded: true,
        error: null,
        tokenId: null,
        tokenName: null,
        memberId: null,
        actorKind: 'Unknown' as const,
        actorName: null,
        origin: 'Workbench' as const,
      })),
    );
    await fixture.whenStable();

    const input = fixture.nativeElement.querySelector('.search-box input') as HTMLInputElement;
    input.value = 'repeated_value';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelectorAll('.search-result')).toHaveLength(2);

    (fixture.nativeElement.querySelector('.search-backdrop') as HTMLButtonElement).click();
    fixture.componentRef.setInput('open', false);
    await fixture.whenStable();
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
    expect(
      (fixture.nativeElement.querySelector('.search-box input') as HTMLInputElement).value,
    ).toBe('');
  });
});
