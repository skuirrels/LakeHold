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
import {
  CsvNewLine,
  Schema,
  TabularImportMode,
  TabularImportRequest,
  TabularImportResult,
} from './models';

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
 * Browser CSV/XLSX-to-table workflow.
 *
 * The component owns only transient upload state. The server performs parsing and table creation in
 * one request, so no staged upload id or node-local path has to survive between requests.
 */
@Component({
  selector: 'lh-tabular-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './tabular-import.component.html',
  styleUrl: './tabular-import.component.css',
})
export class TabularImportComponent {
  private readonly api = inject(LakehouseService);

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  readonly schemas = input.required<Schema[]>();
  readonly imported = output<TabularImportResult>();

  protected readonly open = signal(false);
  protected readonly file = signal<File | null>(null);
  protected readonly schema = signal('main');
  protected readonly table = signal('');
  protected readonly mode = signal<TabularImportMode>('automatic');
  protected readonly worksheet = signal('');
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
  protected readonly result = signal<TabularImportResult | null>(null);
  protected readonly fileFormat = computed<'csv' | 'xlsx' | null>(() => {
    const name = this.file()?.name.toLowerCase();
    if (name?.endsWith('.csv')) {
      return 'csv';
    }

    return name?.endsWith('.xlsx') ? 'xlsx' : null;
  });
  protected readonly canImport = computed(
    () =>
      Boolean(
        this.tenant() &&
          this.catalog() &&
          this.file() &&
          this.fileFormat() &&
          this.schema() &&
          this.table(),
      ) &&
      !this.busy(),
  );

  protected begin(): void {
    const available = this.schemas().map((schema) => schema.name);
    this.schema.set(available.includes('main') ? 'main' : (available[0] ?? 'main'));
    this.file.set(null);
    this.table.set('');
    this.worksheet.set('');
    this.mode.set('automatic');
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
    if (selected?.name.toLowerCase().endsWith('.xlsx')) {
      this.mode.set('automatic');
    }
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

    const request: TabularImportRequest = {
      schema: this.schema().trim(),
      table: this.table().trim(),
      mode: this.mode(),
      worksheet: this.worksheet().trim(),
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
    this.api.importFile(tenant, catalog, file, request).subscribe({
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
  const prefixed = /^[A-Za-z_]/.test(normalized) ? normalized : `file_${normalized}`;
  return (prefixed || 'imported_file').slice(0, 63);
}
