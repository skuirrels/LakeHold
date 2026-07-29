import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { CsvImportMode, CsvImportRequest, CsvImportResult, CsvNewLine, Schema } from './models';

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
  protected readonly delimiter = signal(';');
  protected readonly quote = signal('"');
  protected readonly escape = signal('');
  protected readonly newLine = signal<CsvNewLine>('crlf');
  protected readonly header = signal(true);
  protected readonly sampleSize = signal(-1);
  protected readonly ignoreErrors = signal(true);
  protected readonly storeRejects = signal(true);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
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
    this.result.set(null);
    if (selected && (!this.table().trim() || this.table() === previousSuggestion)) {
      this.table.set(suggestTableName(selected.name));
    }
  }

  protected setMode(value: string): void {
    this.mode.set(value === 'custom' ? 'custom' : 'automatic');
    this.error.set(null);
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
    this.result.set(null);
    this.api.importCsv(tenant, catalog, file, request).subscribe({
      next: (result) => {
        this.result.set(result);
        this.busy.set(false);
        this.imported.emit(result);
      },
      error: (error: Error) => {
        this.error.set(error.message);
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
