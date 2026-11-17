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
import {
  autocompletion,
  closeCompletion,
  CompletionContext,
  CompletionResult,
} from '@codemirror/autocomplete';
import { StreamLanguage } from '@codemirror/language';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { Diagnostic, lintGutter, setDiagnostics } from '@codemirror/lint';
import { sql as sqlLanguage } from '@codemirror/lang-sql';
import { Compartment, EditorState, Prec } from '@codemirror/state';
import { EditorView, keymap } from '@codemirror/view';
import { basicSetup } from 'codemirror';
import { QueryDiagnostic, Schema } from './models';

/**
 * What a run submits: the selection when there is one, otherwise the whole buffer, trimmed.
 *
 * `line` and `column` are where `source` starts in the editor, 1-based. The planner reports
 * diagnostics against the text it was sent, so a run of a selection that starts on line 9 has to
 * move them down eight lines before they mark the right characters.
 */
export interface EditorRunRequest {
  readonly source: string;
  readonly line: number;
  readonly column: number;
  readonly selection: boolean;
}

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
  readonly runQuery = output<EditorRunRequest>();
  /** Whether the editor holds a non-empty selection, so the Run button can say what it runs. */
  readonly selectionChange = output<boolean>();

  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly languageCompartment = new Compartment();
  private readonly completionCompartment = new Compartment();
  private editor: EditorView | null = null;
  private hadSelection = false;

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
          // basicSetup's default keymap binds Mod-Enter to insertBlankLine, and a keymap entry
          // registered later at the same precedence never sees the key. Run has to outrank it, or
          // the shortcut printed on the Run button edits the query instead of running it.
          Prec.highest(
            keymap.of([
              {
                key: 'Mod-Enter',
                preventDefault: true,
                run: (view) => {
                  closeCompletion(view);
                  this.runQuery.emit(runRequestFrom(view.state));
                  return true;
                },
              },
            ]),
          ),
          basicSetup,
          lintGutter(),
          this.languageCompartment.of(this.languageExtension(this.language())),
          this.completionCompartment.of(this.completionExtension(this.schemas(), this.language())),
          EditorView.lineWrapping,
          EditorView.updateListener.of((update) => {
            if (update.docChanged) {
              this.valueChange.emit(update.state.doc.toString());
            }
            if (update.selectionSet || update.docChanged) {
              const selected = !update.state.selection.main.empty;
              if (selected !== this.hadSelection) {
                this.hadSelection = selected;
                this.selectionChange.emit(selected);
              }
            }
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

  /**
   * The text a run should submit, and where it starts.
   *
   * A non-empty main selection runs on its own, which is how a buffer of several statements is
   * used one statement at a time. Leading and trailing whitespace is trimmed, and the start
   * position moves past what was trimmed so diagnostics still land on the right characters.
   * Null before the editor exists.
   */
  runRequest(): EditorRunRequest | null {
    return this.editor ? runRequestFrom(this.editor.state) : null;
  }

  /** Closes an open completion list, which would otherwise sit over the editor after a run. */
  dismissCompletion(): void {
    if (this.editor) {
      closeCompletion(this.editor);
    }
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

function runRequestFrom(state: EditorState): EditorRunRequest {
  const range = state.selection.main;
  const selection = !range.empty;
  const from = selection ? range.from : 0;
  const raw = state.sliceDoc(from, selection ? range.to : state.doc.length);
  const start = from + (raw.length - raw.trimStart().length);
  const line = state.doc.lineAt(start);
  return { source: raw.trim(), line: line.number, column: start - line.from + 1, selection };
}

function pascal(value: string): string {
  const result = value.split(/[^A-Za-z0-9]+/).filter(Boolean)
    .map(part => `${part.charAt(0).toUpperCase()}${part.slice(1)}`).join('');
  return result || 'Value';
}
