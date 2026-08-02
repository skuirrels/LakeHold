import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  effect,
  input,
  output,
  viewChild,
} from '@angular/core';
import { autocompletion, CompletionContext, CompletionResult } from '@codemirror/autocomplete';
import { StreamLanguage } from '@codemirror/language';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { Diagnostic, lintGutter, setDiagnostics } from '@codemirror/lint';
import { sql as sqlLanguage } from '@codemirror/lang-sql';
import { Compartment, EditorState } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { basicSetup } from 'codemirror';
import { QueryDiagnostic, Schema } from './models';

/** CodeMirror boundary for Workbench editing; it has no API or execution dependencies. */
@Component({
  selector: 'lh-query-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<div #host class="query-editor-host"></div>',
  styles: `
    :host { display: block; flex: 1; min-height: 0; overflow: hidden; }
    .query-editor-host { height: 100%; }
    :host ::ng-deep .cm-editor { height: 100%; background: var(--surface-0); color: var(--text); }
    :host ::ng-deep .cm-scroller { overflow: auto; font-family: var(--mono); font-size: 13px; line-height: 1.65; }
    :host ::ng-deep .cm-content { padding: 14px 8px; caret-color: var(--text); }
    :host ::ng-deep .cm-gutters { background: var(--surface-1); color: var(--text-faint); border-right: 1px solid var(--border); }
    :host ::ng-deep .cm-activeLine, :host ::ng-deep .cm-activeLineGutter { background: color-mix(in srgb, var(--accent) 8%, transparent); }
    :host ::ng-deep .cm-focused { outline: none; }
    :host ::ng-deep .cm-tooltip { background: var(--surface-2); color: var(--text); border-color: var(--border-strong); }
  `,
})
export class QueryEditorComponent implements AfterViewInit, OnDestroy {
  readonly value = input.required<string>();
  readonly language = input('sql');
  readonly schemas = input<Schema[]>([]);
  readonly diagnostics = input<QueryDiagnostic[]>([]);
  readonly label = input('Query editor');
  readonly valueChange = output<string>();
  readonly runQuery = output<void>();

  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly languageCompartment = new Compartment();
  private readonly completionCompartment = new Compartment();
  private editor: EditorView | null = null;

  constructor() {
    effect(() => this.syncValue(this.value()));
    effect(() => this.configureLanguage(this.language()));
    effect(() => this.configureCompletions(this.schemas(), this.language()));
    effect(() => this.applyDiagnostics(this.diagnostics()));
    effect(() => this.updateLabel(this.label()));
  }

  ngAfterViewInit(): void {
    this.editor = new EditorView({
      parent: this.host().nativeElement,
      state: EditorState.create({
        doc: this.value(),
        extensions: [
          basicSetup,
          lintGutter(),
          this.languageCompartment.of(this.languageExtension(this.language())),
          this.completionCompartment.of(this.completionExtension(this.schemas(), this.language())),
          EditorView.lineWrapping,
          EditorView.updateListener.of((update) => {
            if (update.docChanged) {
              this.valueChange.emit(update.state.doc.toString());
            }
          }),
          EditorView.domEventHandlers({
            keydown: (event) => {
              if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
                event.preventDefault();
                this.runQuery.emit();
                return true;
              }
              return false;
            },
          }),
        ],
      }),
    });
    this.updateLabel(this.label());
    this.applyDiagnostics(this.diagnostics());
  }

  ngOnDestroy(): void {
    this.editor?.destroy();
  }

  private syncValue(value: string): void {
    if (!this.editor || this.editor.state.doc.toString() === value) {
      return;
    }
    this.editor.dispatch({ changes: { from: 0, to: this.editor.state.doc.length, insert: value } });
  }

  private configureLanguage(language: string): void {
    this.editor?.dispatch({ effects: this.languageCompartment.reconfigure(this.languageExtension(language)) });
  }

  private configureCompletions(schemas: Schema[], language: string): void {
    this.editor?.dispatch({
      effects: this.completionCompartment.reconfigure(this.completionExtension(schemas, language)),
    });
  }

  private applyDiagnostics(diagnostics: QueryDiagnostic[]): void {
    if (!this.editor) {
      return;
    }
    const mapped = diagnostics.map((diagnostic): Diagnostic => ({
      from: this.offset(diagnostic.startLine, diagnostic.startColumn),
      to: this.offset(diagnostic.endLine, diagnostic.endColumn),
      severity: diagnostic.severity === 'warning' ? 'warning' : 'error',
      message: `${diagnostic.code}: ${diagnostic.message}`,
    }));
    this.editor.dispatch(setDiagnostics(this.editor.state, mapped));
  }

  private offset(line: number, column: number): number {
    const document = this.editor!.state.doc;
    const safeLine = document.line(Math.min(Math.max(line, 1), document.lines));
    return Math.min(safeLine.to, safeLine.from + Math.max(column - 1, 0));
  }

  private updateLabel(label: string): void {
    this.editor?.contentDOM.setAttribute('aria-label', label);
  }

  private languageExtension(language: string) {
    return language === 'csharp-linq' ? StreamLanguage.define(csharp) : sqlLanguage();
  }

  private completionExtension(schemas: Schema[], language: string) {
    return autocompletion({ override: [context => this.complete(context, schemas, language)] });
  }

  private complete(
    context: CompletionContext,
    schemas: Schema[],
    language: string,
  ): CompletionResult | null {
    const word = context.matchBefore(/[\w.]*/);
    if (!word || (!context.explicit && word.from === word.to)) {
      return null;
    }

    const catalog = schemas.flatMap((schema) => [
      { label: language === 'csharp-linq' ? pascal(schema.name) : schema.name, type: 'namespace' },
      ...schema.tables.map((table) => ({
        label: language === 'csharp-linq'
          ? `${pascal(schema.name)}.${pascal(table.name)}`
          : `${schema.name}.${table.name}`,
        type: 'class',
      })),
      ...schema.tables.flatMap((table) => table.columns.map((column) => ({
        label: language === 'csharp-linq' ? pascal(column.name) : column.name,
        detail: `${schema.name}.${table.name} · ${column.dataType}`,
        type: 'property',
      }))),
    ]);
    const operators = language === 'csharp-linq'
      ? ['Where', 'Select', 'OrderBy', 'OrderByDescending', 'ThenBy', 'GroupBy', 'Join', 'Take', 'Skip', 'Distinct', 'Count', 'Any', 'Sum', 'Min', 'Max', 'Average']
          .map(label => ({ label, type: 'method' }))
      : [];
    return { from: word.from, options: [...catalog, ...operators], validFor: /^[\w.]*$/ };
  }
}

function pascal(value: string): string {
  const result = value.split(/[^A-Za-z0-9]+/).filter(Boolean)
    .map(part => `${part.charAt(0).toUpperCase()}${part.slice(1)}`).join('');
  return result || 'Value';
}
