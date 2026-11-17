import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EditorView } from '@codemirror/view';
import { EditorRunRequest, QueryEditorComponent } from './query-editor.component';

describe('QueryEditorComponent', () => {
  let fixture: ComponentFixture<QueryEditorComponent>;
  let runs: EditorRunRequest[];
  let selections: boolean[];

  async function mount(value: string): Promise<EditorView> {
    fixture = TestBed.createComponent(QueryEditorComponent);
    fixture.componentRef.setInput('value', value);
    runs = [];
    selections = [];
    fixture.componentInstance.runQuery.subscribe((request) => runs.push(request));
    fixture.componentInstance.selectionChange.subscribe((selected) => selections.push(selected));
    await fixture.whenStable();
    return EditorView.findFromDOM(fixture.nativeElement.querySelector('.cm-editor'))!;
  }

  /** CodeMirror reads Mod as Command on Apple platforms and Control everywhere else. */
  function pressModEnter(view: EditorView): KeyboardEvent {
    const apple = /Mac|iPhone|iPad|iPod/.test(navigator.platform);
    const event = new KeyboardEvent('keydown', {
      key: 'Enter',
      keyCode: 13,
      metaKey: apple,
      ctrlKey: !apple,
      bubbles: true,
      cancelable: true,
    });
    view.contentDOM.dispatchEvent(event);
    return event;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  it('runs on Mod-Enter instead of inserting a blank line', async () => {
    // basicSetup binds Mod-Enter to insertBlankLine. The shortcut printed on the Run button has to
    // outrank it, or pressing it edits the query.
    const view = await mount('SELECT 1');
    view.dispatch({ selection: { anchor: 3 } });

    const event = pressModEnter(view);

    expect(event.defaultPrevented).toBe(true);
    expect(view.state.doc.toString()).toBe('SELECT 1');
    expect(runs).toEqual([{ source: 'SELECT 1', line: 1, column: 1, selection: false }]);
  });

  it('runs only the selection, and says where it starts', async () => {
    const source = 'SELECT 1;\n\n  SELECT 2;\nSELECT 3;';
    const view = await mount(source);
    const start = source.indexOf('  SELECT 2');
    view.dispatch({ selection: { anchor: start, head: start + '  SELECT 2;\n'.length } });

    pressModEnter(view);

    // Leading whitespace is trimmed and the position moves past it, so diagnostics from the
    // planner, which counts from the text it was sent, still land on the right characters.
    expect(runs).toEqual([{ source: 'SELECT 2;', line: 3, column: 3, selection: true }]);
    expect(fixture.componentInstance.runRequest()).toEqual(runs[0]);
  });

  it('runs the whole buffer, trimmed, when nothing is selected', async () => {
    const view = await mount('\n\n  SELECT 1  \n');

    expect(fixture.componentInstance.runRequest()).toEqual({
      source: 'SELECT 1',
      line: 3,
      column: 3,
      selection: false,
    });
    view.dispatch({ selection: { anchor: 0 } });
    expect(fixture.componentInstance.runRequest()?.selection).toBe(false);
  });

  it('reports when a selection appears and goes away, once each', async () => {
    const view = await mount('SELECT 1');

    view.dispatch({ selection: { anchor: 0, head: 6 } });
    view.dispatch({ selection: { anchor: 0, head: 3 } });
    view.dispatch({ selection: { anchor: 2 } });

    expect(selections).toEqual([true, false]);
  });
});
