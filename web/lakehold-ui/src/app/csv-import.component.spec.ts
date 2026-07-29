import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of, throwError } from 'rxjs';
import { CsvImportComponent, suggestTableName } from './csv-import.component';
import { ApiError, LakehouseService } from './lakehouse.service';
import { CsvImportRequest } from './models';
import { FakeLakehouseService } from './test-doubles';

describe('CsvImportComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<CsvImportComponent>;

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(CsvImportComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('schemas', [{ name: 'main', tables: [] }]);
    await fixture.whenStable();
  }

  async function openWithFile(name = 'predicted schedules.csv'): Promise<File> {
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();
    const file = new File(['id,name\n1,Alice\n'], name, { type: 'text/csv' });
    const input = fixture.nativeElement.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { configurable: true, value: [file] });
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    return file;
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('uses native DuckDB auto detection as the standard upload path', async () => {
    await mount();
    const file = await openWithFile();

    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    const [tenant, catalog, uploaded, request] = api.lastArgs('importCsv') as [
      string,
      string,
      File,
      CsvImportRequest,
    ];
    expect([tenant, catalog, uploaded]).toEqual(['demo', 'analytics', file]);
    expect(request.schema).toBe('main');
    expect(request.table).toBe('predicted_schedules');
    expect(request.mode).toBe('automatic');
    expect(fixture.nativeElement.textContent).toContain('main.customers is ready');
  });

  it('offers an explicit tolerant retry and resubmits the retained file with the exact profile', async () => {
    vi.spyOn(api, 'importCsv')
      .mockReturnValueOnce(
        throwError(
          () =>
            new ApiError(
              'CSV line 904218 contains 135 columns; 157 were expected.',
              400,
              'csv_parse_error',
              true,
            ),
        ),
      )
      .mockReturnValueOnce(of(api.csvImport));
    await mount();
    const file = await openWithFile('sch_predicted_schedules.csv');

    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain(
      'CSV line 904218 contains 135 columns; 157 were expected.',
    );
    const retry = Array.from<HTMLButtonElement>(
      fixture.nativeElement.querySelectorAll('button'),
    ).find((button) =>
      button.textContent?.includes('Retry with semicolon / CRLF tolerant profile'),
    );
    expect(retry).toBeDefined();

    retry!.click();
    await fixture.whenStable();

    expect(api.importCsv).toHaveBeenCalledTimes(2);
    const [tenant, catalog, uploaded, request] = vi.mocked(api.importCsv).mock.calls[1] as [
      string,
      string,
      File,
      CsvImportRequest,
    ];
    expect([tenant, catalog, uploaded]).toEqual(['demo', 'analytics', file]);
    expect(request).toMatchObject({
      mode: 'custom',
      delimiter: ';',
      quote: '"',
      escape: '',
      newLine: 'crlf',
      header: true,
      sampleSize: -1,
      ignoreErrors: true,
      storeRejects: true,
    });
    expect(fixture.nativeElement.textContent).toContain('main.customers is ready');
  });

  it('replicates the semicolon CRLF full-file rejects profile in custom mode', async () => {
    api.csvImport = {
      ...api.csvImport,
      table: 'predicted_schedules',
      rejectedRows: 1,
      recordedErrors: 1,
      rejects: [
        {
          line: 3,
          columnName: 'name',
          errorType: 'MISSING COLUMNS',
          csvLine: '2',
          errorMessage: 'Expected 2 values but found 1.',
        },
      ],
    };
    await mount();
    await openWithFile('sch_predicted_schedules.csv');

    const custom = fixture.nativeElement.querySelector(
      'input[type="radio"][value="custom"]',
    ) as HTMLInputElement;
    custom.click();
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    const request = api.lastArgs('importCsv')?.[3] as CsvImportRequest;
    expect(request).toMatchObject({
      mode: 'custom',
      delimiter: ';',
      quote: '"',
      escape: '',
      newLine: 'crlf',
      header: true,
      sampleSize: -1,
      ignoreErrors: true,
      storeRejects: true,
    });
    expect(fixture.nativeElement.textContent).toContain('1 rejected row');
    expect(fixture.nativeElement.textContent).toContain('MISSING COLUMNS');
  });

  it('keeps reject reporting and skip-errors settings in a valid combination', async () => {
    await mount();
    await openWithFile();

    const custom = fixture.nativeElement.querySelector(
      'input[type="radio"][value="custom"]',
    ) as HTMLInputElement;
    custom.click();
    await fixture.whenStable();

    const checkboxes = fixture.nativeElement.querySelectorAll(
      '.check-grid input[type="checkbox"]',
    ) as NodeListOf<HTMLInputElement>;
    const ignoreErrors = checkboxes[1];
    const storeRejects = checkboxes[2];
    expect(ignoreErrors.disabled).toBe(true);

    storeRejects.click();
    await fixture.whenStable();
    expect(ignoreErrors.disabled).toBe(false);

    ignoreErrors.click();
    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    const request = api.lastArgs('importCsv')?.[3] as CsvImportRequest;
    expect(request.ignoreErrors).toBe(false);
    expect(request.storeRejects).toBe(false);
  });

  it('starts each reopened dialog without the previous file or table', async () => {
    await mount();
    await openWithFile('first.csv');
    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    const done = Array.from<HTMLButtonElement>(
      fixture.nativeElement.querySelectorAll('button'),
    ).find((button) => button.textContent?.trim() === 'Done')!;
    done.click();
    await fixture.whenStable();

    const begin = Array.from<HTMLButtonElement>(
      fixture.nativeElement.querySelectorAll('button'),
    ).find((button) => button.textContent?.trim() === 'Import CSV')!;
    begin.click();
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).not.toContain('first.csv');
    const table = fixture.nativeElement.querySelector(
      'input[type="text"][maxlength="63"]',
    ) as HTMLInputElement;
    const create = Array.from<HTMLButtonElement>(
      fixture.nativeElement.querySelectorAll('button'),
    ).find((button) => button.textContent?.trim() === 'Create table')!;
    expect(table.value).toBe('');
    expect(create.disabled).toBe(true);
  });

  it('updates an untouched suggested table when a different file is selected', async () => {
    await mount();
    await openWithFile('first.csv');

    const second = new File(['id\n2\n'], 'second export.csv', { type: 'text/csv' });
    const input = fixture.nativeElement.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { configurable: true, value: [second] });
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    const table = fixture.nativeElement.querySelector(
      'input[type="text"][maxlength="63"]',
    ) as HTMLInputElement;
    expect(table.value).toBe('second_export');
  });

  it('keeps invalid or unsafe file-name characters out of the proposed identifier', () => {
    expect(suggestTableName('6402432349 — Sch Prédicted.csv')).toBe('csv_6402432349_sch_predicted');
  });
});
