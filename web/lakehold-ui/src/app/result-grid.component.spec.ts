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

    const headers = fixture.nativeElement.querySelectorAll('th:not(.gutter):not(.filler)');
    const cells = fixture.nativeElement.querySelectorAll('tbody td.cell');
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
    expect(fixture.nativeElement.querySelectorAll('tbody td.cell')).toHaveLength(1);
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
    expect(fixture.nativeElement.querySelector('tbody td.cell')?.textContent?.trim()).toBe('1');
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

      (fixture.nativeElement.querySelector('tbody td.cell') as HTMLElement).dispatchEvent(
        new MouseEvent('dblclick', { bubbles: true }),
      );
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

  describe('virtualisation', () => {
    function manyRows(count: number): QueryResponse {
      return {
        columns: [
          { name: 'id', dataType: 'BIGINT', clrType: 'Int64' },
          { name: 'label', dataType: 'VARCHAR', clrType: 'String' },
        ],
        rows: Array.from({ length: count }, (_, index) => [index + 1, `row ${index + 1}`]),
        truncated: false,
        elapsedMilliseconds: 1,
        rowsAffected: null,
      };
    }

    function bodyRows(): HTMLTableRowElement[] {
      return [...fixture.nativeElement.querySelectorAll('tbody tr:not(.spacer)')];
    }

    it('renders a window of a large result and stands in for the rest with a spacer', async () => {
      await mount(manyRows(5000));

      const rows = bodyRows();
      expect(rows.length).toBeGreaterThan(0);
      expect(rows.length).toBeLessThan(200);
      // The scrollbar still describes every returned row.
      const spacer = fixture.nativeElement.querySelector('tbody tr.spacer td') as HTMLElement;
      expect(parseInt(spacer.style.height, 10)).toBe((5000 - rows.length) * 28);
      expect(fixture.nativeElement.querySelector('table').getAttribute('aria-rowcount')).toBe(
        '5001',
      );
      // One tab stop for the whole grid, not a button per cell.
      expect(fixture.nativeElement.querySelectorAll('tbody button')).toHaveLength(0);
      expect(fixture.nativeElement.querySelectorAll('[tabindex="0"]')).toHaveLength(1);
    });

    it('renders the rows under the scroll position', async () => {
      await mount(manyRows(5000));

      const scroller = fixture.nativeElement.querySelector('.grid-scroll') as HTMLElement;
      scroller.scrollTop = 28 * 3000;
      scroller.dispatchEvent(new Event('scroll'));
      await new Promise((resolve) => requestAnimationFrame(() => resolve(null)));
      await fixture.whenStable();

      const numbers = bodyRows().map((row) => Number(row.querySelector('.gutter')?.textContent));
      expect(numbers).toContain(3001);
      expect(numbers).not.toContain(1);
      expect(bodyRows()[0].getAttribute('aria-rowindex')).toBe(String(numbers[0] + 1));
    });
  });

  describe('sorting', () => {
    const values: QueryResponse = {
      columns: [
        { name: 'amount', dataType: 'HUGEINT', clrType: 'BigInteger' },
        { name: 'name', dataType: 'VARCHAR', clrType: 'String' },
      ],
      // Integers beyond 2^53 arrive as strings; a lexical or double sort orders them wrongly.
      rows: [
        ['9007199254740993', 'item 10'],
        [null, 'item 2'],
        [5, 'item 1'],
        ['9007199254740992', null],
      ],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    };

    function heading(index: number): HTMLElement {
      return fixture.nativeElement.querySelectorAll('thead th:not(.gutter):not(.filler)')[index];
    }

    function column(index: number): string[] {
      return [...fixture.nativeElement.querySelectorAll('tbody tr:not(.spacer)')].map(
        (row) => (row as HTMLElement).querySelectorAll('td.cell')[index].textContent!.trim(),
      );
    }

    it('cycles a heading through ascending, descending, and response order', async () => {
      await mount(values);

      heading(0).click();
      await fixture.whenStable();
      expect(heading(0).getAttribute('aria-sort')).toBe('ascending');
      expect(column(0)).toEqual(['5', '9007199254740992', '9007199254740993', 'NULL']);

      heading(0).click();
      await fixture.whenStable();
      expect(heading(0).getAttribute('aria-sort')).toBe('descending');
      // Nulls stay last whichever way the column is sorted.
      expect(column(0)).toEqual(['9007199254740993', '9007199254740992', '5', 'NULL']);

      heading(0).click();
      await fixture.whenStable();
      expect(heading(0).hasAttribute('aria-sort')).toBe(false);
      expect(column(0)).toEqual(['9007199254740993', 'NULL', '5', '9007199254740992']);
    });

    it('collates text naturally and keeps the response row number', async () => {
      await mount(values);

      heading(1).click();
      await fixture.whenStable();

      expect(column(1)).toEqual(['item 1', 'item 2', 'item 10', 'NULL']);
      const numbers = [...fixture.nativeElement.querySelectorAll('tbody .gutter')].map((cell) =>
        (cell as HTMLElement).textContent!.trim(),
      );
      expect(numbers).toEqual(['3', '2', '1', '4']);
    });

    it('says a truncated result is sorted only within what came back', async () => {
      await mount({ ...values, truncated: true });
      expect(fixture.nativeElement.querySelector('.result-footer').textContent).not.toContain(
        'Sorted within',
      );

      heading(1).click();
      await fixture.whenStable();
      expect(fixture.nativeElement.querySelector('.result-footer').textContent).toContain(
        'Sorted within the returned rows',
      );
    });
  });

  describe('keyboard', () => {
    let writeText: ReturnType<typeof vi.fn>;
    let previous: PropertyDescriptor | undefined;

    beforeEach(() => {
      previous = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
      writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    });

    afterEach(() => {
      if (previous) Object.defineProperty(navigator, 'clipboard', previous);
      else Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    });

    function grid(): HTMLTableElement {
      return fixture.nativeElement.querySelector('table[role="grid"]');
    }

    async function press(key: string, init: KeyboardEventInit = {}): Promise<KeyboardEvent> {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
      grid().dispatchEvent(event);
      await fixture.whenStable();
      return event;
    }

    function activeText(): string | null {
      const id = grid().getAttribute('aria-activedescendant');
      return id ? (fixture.nativeElement.querySelector(`#${id}`)?.textContent?.trim() ?? null) : null;
    }

    const result: QueryResponse = {
      columns: [
        { name: 'id', dataType: 'BIGINT', clrType: 'Int64' },
        { name: 'country', dataType: 'VARCHAR', clrType: 'String' },
      ],
      rows: [
        [1, 'Denmark'],
        [2, 'Sweden'],
      ],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    };

    it('moves the active cell with the arrow keys and copies it with Enter', async () => {
      await mount(result);

      grid().focus();
      await fixture.whenStable();
      expect(activeText()).toBe('1');

      expect((await press('ArrowDown')).defaultPrevented).toBe(true);
      expect(activeText()).toBe('2');
      await press('ArrowRight');
      expect(activeText()).toBe('Sweden');
      // Movement stops at the edges rather than wrapping or leaving the grid.
      await press('ArrowDown');
      expect(activeText()).toBe('Sweden');

      await press('Enter');
      expect(writeText).toHaveBeenCalledWith('Sweden');
      expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain(
        'Cell copied',
      );
    });

    it('sorts from the heading row with Enter', async () => {
      await mount(result);

      grid().focus();
      await fixture.whenStable();
      await press('ArrowRight');
      await press('ArrowUp');
      expect(activeText()).toContain('country');

      await press('Enter');
      const headings = fixture.nativeElement.querySelectorAll('thead th:not(.gutter):not(.filler)');
      expect(headings[1].getAttribute('aria-sort')).toBe('ascending');
      expect(writeText).not.toHaveBeenCalled();
    });

    it('copies the active cell with the platform copy shortcut', async () => {
      await mount(result);

      grid().focus();
      await fixture.whenStable();
      await press('ArrowRight');
      await press('c', { ctrlKey: true });

      expect(writeText).toHaveBeenCalledWith('Denmark');
    });

    it('leaves ordinary keys to the browser', async () => {
      await mount(result);

      grid().focus();
      await fixture.whenStable();

      expect((await press('Tab')).defaultPrevented).toBe(false);
      expect((await press('a')).defaultPrevented).toBe(false);
    });

    it('leaves Mod-Enter alone rather than treating it as a copy', async () => {
      await mount(result);

      grid().focus();
      await fixture.whenStable();

      expect((await press('Enter', { metaKey: true })).defaultPrevented).toBe(false);
      expect((await press('Enter', { ctrlKey: true })).defaultPrevented).toBe(false);
      expect(writeText).not.toHaveBeenCalled();
    });
  });

  it('says when a query returned columns but no rows, rather than blaming a search', async () => {
    await mount({
      columns: [{ name: 'id', dataType: 'BIGINT', clrType: 'Int64' }],
      rows: [],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });

    expect(fixture.nativeElement.textContent).toContain('The query returned no rows.');
    expect(fixture.nativeElement.textContent).not.toContain('match this search');
  });
});
