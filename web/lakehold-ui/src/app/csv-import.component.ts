import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { ApiError, LakehouseService } from './lakehouse.service';
import { CsvImportMode, CsvImportRequest, CsvImportResult, CsvNewLine, Schema } from './models';

const TOLERANT_PROFILE = {
  delimiter: ';',
  quote: '"',
  escape: '',
  newLine: 'crlf' as CsvNewLine,
  header: true,
  sampleSize: -1,
  ignoreErrors: true,
  storeRejects: true,
};

/**
 * Browser CSV-to-table workflow.
 *
 * The component owns only transient upload state. The server performs parsing and table creation in
 * one request, so no staged upload id or node-local path has to survive between requests.
 */
@Component({
  selector: 'lh-csv-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './csv-import.component.html',
  styleUrl: './csv-import.component.css',
})
export class CsvImportComponent {
  private readonly api = inject(LakehouseService);

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  readonly schemas = input.required<Schema[]>();
  readonly imported = output<CsvImportResult>();

  protected readonly open = signal(false);
  protected readonly file = signal<File | null>(null);
  protected readonly schema = signal('main');
  protected readonly table = signal('');
  protected readonly mode = signal<CsvImportMode>('automatic');
  protected readonly delimiter = signal<string>(TOLERANT_PROFILE.delimiter);
  protected readonly quote = signal<string>(TOLERANT_PROFILE.quote);
  protected readonly escape = signal<string>(TOLERANT_PROFILE.escape);
  protected readonly newLine = signal<CsvNewLine>(TOLERANT_PROFILE.newLine);
  protected readonly header = signal<boolean>(TOLERANT_PROFILE.header);
  protected readonly sampleSize = signal<number>(TOLERANT_PROFILE.sampleSize);
  protected readonly ignoreErrors = signal<boolean>(TOLERANT_PROFILE.ignoreErrors);
  protected readonly storeRejects = signal<boolean>(TOLERANT_PROFILE.storeRejects);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly canRetryWithTolerantProfile = signal(false);
  protected readonly result = signal<CsvImportResult | null>(null);
  protected readonly canImport = computed(
    () =>
      Boolean(this.tenant() && this.catalog() && this.file() && this.schema() && this.table()) &&
      !this.busy(),
  );

  protected begin(): void {
    const available = this.schemas().map((schema) => schema.name);
    this.schema.set(available.includes('main') ? 'main' : (available[0] ?? 'main'));
    this.file.set(null);
    this.table.set('');
    this.open.set(true);
    this.error.set(null);
    this.canRetryWithTolerantProfile.set(false);
    this.result.set(null);
  }

  protected close(): void {
    if (!this.busy()) {
      this.open.set(false);
    }
  }

  protected chooseFile(event: Event): void {
    const previous = this.file();
    const previousSuggestion = previous ? suggestTableName(previous.name) : '';
    const selected = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.file.set(selected);
    this.error.set(null);
    this.canRetryWithTolerantProfile.set(false);
    this.result.set(null);
    if (selected && (!this.table().trim() || this.table() === previousSuggestion)) {
      this.table.set(suggestTableName(selected.name));
    }
  }

  protected setMode(value: string): void {
    this.mode.set(value === 'custom' ? 'custom' : 'automatic');
    this.error.set(null);
    this.canRetryWithTolerantProfile.set(false);
  }

  protected setIgnoreErrors(enabled: boolean): void {
    this.ignoreErrors.set(enabled);
    if (!enabled) {
      this.storeRejects.set(false);
    }
  }

  protected setStoreRejects(enabled: boolean): void {
    this.storeRejects.set(enabled);
    if (enabled) {
      this.ignoreErrors.set(true);
    }
  }

  protected retryWithTolerantProfile(): void {
    this.mode.set('custom');
    this.delimiter.set(TOLERANT_PROFILE.delimiter);
    this.quote.set(TOLERANT_PROFILE.quote);
    this.escape.set(TOLERANT_PROFILE.escape);
    this.newLine.set(TOLERANT_PROFILE.newLine);
    this.header.set(TOLERANT_PROFILE.header);
    this.sampleSize.set(TOLERANT_PROFILE.sampleSize);
    this.ignoreErrors.set(TOLERANT_PROFILE.ignoreErrors);
    this.storeRejects.set(TOLERANT_PROFILE.storeRejects);
    this.error.set(null);
    this.canRetryWithTolerantProfile.set(false);
    this.submit();
  }

  protected submit(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const file = this.file();
    if (!tenant || !catalog || !file || !this.canImport()) {
      return;
    }

    const request: CsvImportRequest = {
      schema: this.schema().trim(),
      table: this.table().trim(),
      mode: this.mode(),
      delimiter: this.delimiter(),
      quote: this.quote(),
      escape: this.escape(),
      newLine: this.newLine(),
      header: this.header(),
      sampleSize: this.sampleSize(),
      ignoreErrors: this.ignoreErrors(),
      storeRejects: this.storeRejects(),
    };

    this.busy.set(true);
    this.error.set(null);
    this.canRetryWithTolerantProfile.set(false);
    this.result.set(null);
    this.api.importCsv(tenant, catalog, file, request).subscribe({
      next: (result) => {
        this.result.set(result);
        this.busy.set(false);
        this.imported.emit(result);
      },
      error: (error: Error) => {
        this.error.set(error.message);
        this.canRetryWithTolerantProfile.set(
          this.mode() === 'automatic' &&
            error instanceof ApiError &&
            error.code === 'csv_parse_error' &&
            error.canRetryWithTolerantProfile,
        );
        this.busy.set(false);
      },
    });
  }
}

/** Produces a conservative bare SQL identifier from a browser file name. */
export function suggestTableName(fileName: string): string {
  const withoutExtension = fileName.replace(/\.[^.]+$/, '');
  const normalized = withoutExtension
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^A-Za-z0-9_]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .toLowerCase();
  const prefixed = /^[A-Za-z_]/.test(normalized) ? normalized : `csv_${normalized}`;
  return (prefixed || 'imported_csv').slice(0, 63);
}
