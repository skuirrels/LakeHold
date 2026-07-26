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
});
