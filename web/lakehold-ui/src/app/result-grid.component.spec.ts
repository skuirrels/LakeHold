import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QueryResponse } from './models';
import { ResultGridComponent } from './result-grid.component';

describe('ResultGridComponent', () => {
  let fixture: ComponentFixture<ResultGridComponent>;

  async function mount(result: QueryResponse) {
    fixture = TestBed.createComponent(ResultGridComponent);
    fixture.componentRef.setInput('result', result);
    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  it('renders null and nested values without losing their shape', async () => {
    await mount({
      columns: [
        { name: 'missing', dataType: 'VARCHAR', clrType: 'String' },
        { name: 'payload', dataType: 'STRUCT', clrType: 'Object' },
      ],
      rows: [[null, { source: 'e2e', values: [1, 2] }]],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });

    expect(fixture.nativeElement.textContent).toContain('NULL');
    expect(fixture.nativeElement.textContent).toContain('{"source":"e2e","values":[1,2]}');
    expect(fixture.nativeElement.querySelector('td.null')).toBeTruthy();
  });

  it('right-aligns numeric CLR types but not numeric-looking text', async () => {
    await mount({
      columns: [
        { name: 'amount', dataType: 'BIGINT', clrType: 'Int64' },
        { name: 'code', dataType: 'VARCHAR', clrType: 'String' },
      ],
      rows: [[42, '123']],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });

    const headers = fixture.nativeElement.querySelectorAll('th:not(.gutter)');
    const cells = fixture.nativeElement.querySelectorAll('tbody td:not(.gutter)');
    expect(headers[0].classList).toContain('numeric');
    expect(headers[1].classList).not.toContain('numeric');
    expect(cells[0].classList).toContain('numeric');
    expect(cells[1].classList).not.toContain('numeric');
  });

  it('states that a no-column statement completed', async () => {
    await mount({
      columns: [],
      rows: [],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: 3,
    });

    expect(fixture.nativeElement.textContent).toContain('Statement completed. No rows returned.');
  });

  it('filters returned rows and keeps the footer honest about the bounded response', async () => {
    await mount({
      columns: [{ name: 'country', dataType: 'VARCHAR', clrType: 'String' }],
      rows: [['Denmark'], ['Sweden']],
      truncated: true,
      elapsedMilliseconds: 12.34,
      rowsAffected: null,
    });

    const search = fixture.nativeElement.querySelector('.result-search input') as HTMLInputElement;
    search.value = 'swed';
    search.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('Sweden');
    expect(fixture.nativeElement.textContent).not.toContain('Denmark');
    expect(fixture.nativeElement.querySelector('.result-footer').textContent).toContain(
      '1 of 2 returned rows',
    );
    expect(fixture.nativeElement.querySelector('.result-footer').textContent).toContain('12.3 ms');
    expect(fixture.nativeElement.querySelector('.result-footer').textContent).toContain(
      'Row limit reached',
    );
  });

  it('lets the operator hide a column without changing the response', async () => {
    await mount({
      columns: [
        { name: 'id', dataType: 'BIGINT', clrType: 'Int64' },
        { name: 'payload', dataType: 'JSON', clrType: 'Object' },
      ],
      rows: [[7, { ok: true }]],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });

    (fixture.nativeElement.querySelector('.column-picker .tool-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    const checks = fixture.nativeElement.querySelectorAll('.column-menu input');
    (checks[1] as HTMLInputElement).click();
    await fixture.whenStable();

    expect(
      [...fixture.nativeElement.querySelectorAll('.col-name')].map(
        (node: Element) => node.textContent,
      ),
    ).toEqual(['id']);
    expect(fixture.nativeElement.querySelectorAll('tbody td:not(.gutter)')).toHaveLength(1);
  });

  it('treats duplicate column names as separate result positions', async () => {
    await mount({
      columns: [
        { name: 'id', dataType: 'BIGINT', clrType: 'Int64' },
        { name: 'id', dataType: 'BIGINT', clrType: 'Int64' },
      ],
      rows: [[1, 2]],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });

    (fixture.nativeElement.querySelector('.column-picker .tool-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    const checks = fixture.nativeElement.querySelectorAll('.column-menu input');
    (checks[1] as HTMLInputElement).click();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelectorAll('.col-name')).toHaveLength(1);
    expect(fixture.nativeElement.querySelector('tbody .cell-copy')?.textContent?.trim()).toBe('1');
  });

  it('reports clipboard refusal instead of leaking an unhandled rejection', async () => {
    const previous = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: vi.fn().mockRejectedValue(new Error('permission denied')) },
    });
    try {
      await mount({
        columns: [{ name: 'id', dataType: 'BIGINT', clrType: 'Int64' }],
        rows: [[1]],
        truncated: false,
        elapsedMilliseconds: 1,
        rowsAffected: null,
      });

      (fixture.nativeElement.querySelector('.cell-copy') as HTMLButtonElement).click();
      await fixture.whenStable();
      expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain(
        'Clipboard unavailable',
      );
    } finally {
      if (previous) Object.defineProperty(navigator, 'clipboard', previous);
      else Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    }
  });

  it('resets result-specific search and column choices for a new execution', async () => {
    await mount({
      columns: [
        { name: 'country', dataType: 'VARCHAR', clrType: 'String' },
        { name: 'amount', dataType: 'BIGINT', clrType: 'Int64' },
      ],
      rows: [['Denmark', 1]],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });

    const search = fixture.nativeElement.querySelector('.result-search input') as HTMLInputElement;
    search.value = 'missing';
    search.dispatchEvent(new Event('input'));
    (fixture.nativeElement.querySelector('.column-picker .tool-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    (fixture.nativeElement.querySelectorAll('.column-menu input')[1] as HTMLInputElement).click();
    await fixture.whenStable();

    fixture.componentRef.setInput('result', {
      columns: [
        { name: 'status', dataType: 'VARCHAR', clrType: 'String' },
        { name: 'count', dataType: 'BIGINT', clrType: 'Int64' },
      ],
      rows: [['ready', 3]],
      truncated: false,
      elapsedMilliseconds: 2,
      rowsAffected: null,
    });
    await fixture.whenStable();

    expect(
      (fixture.nativeElement.querySelector('.result-search input') as HTMLInputElement).value,
    ).toBe('');
    expect(fixture.nativeElement.querySelectorAll('.col-name')).toHaveLength(2);
    expect(fixture.nativeElement.textContent).toContain('ready');
  });
});
