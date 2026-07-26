import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CatalogExplorerComponent } from './catalog-explorer.component';
import { Schema } from './models';

describe('CatalogExplorerComponent', () => {
  let fixture: ComponentFixture<CatalogExplorerComponent>;
  const schemas: Schema[] = [
    {
      name: 'main',
      tables: [
        {
          name: 'orders',
          kind: 'BASE TABLE',
          columns: [
            { name: 'order_id', dataType: 'BIGINT', isNullable: false },
            { name: 'customer_name', dataType: 'VARCHAR', isNullable: true },
          ],
        },
        {
          name: 'revenue',
          kind: 'VIEW',
          columns: [{ name: 'total', dataType: 'DECIMAL', isNullable: true }],
        },
      ],
    },
  ];

  async function mount() {
    fixture = TestBed.createComponent(CatalogExplorerComponent);
    fixture.componentRef.setInput('schemas', schemas);
    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  it('finds a table by one of its column names', async () => {
    await mount();
    const filter = fixture.nativeElement.querySelector('input[type="search"]') as HTMLInputElement;
    filter.value = 'customer';
    filter.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('orders');
    expect(fixture.nativeElement.textContent).not.toContain('revenue');
  });

  it('expands a table to show its typed columns', async () => {
    await mount();
    (fixture.nativeElement.querySelector('.table-toggle') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('order_id');
    expect(fixture.nativeElement.textContent).toContain('BIGINT');
    expect(fixture.nativeElement.querySelector('.table-toggle').getAttribute('aria-expanded')).toBe(
      'true',
    );
  });

  it('emits a bounded SELECT for the selected table', async () => {
    await mount();
    const emitted: string[] = [];
    fixture.componentInstance.insertSql.subscribe((sql) => emitted.push(sql));

    (fixture.nativeElement.querySelector('button.insert') as HTMLButtonElement).click();

    expect(emitted).toEqual(['SELECT *\nFROM main.orders\nLIMIT 100;']);
  });
});
