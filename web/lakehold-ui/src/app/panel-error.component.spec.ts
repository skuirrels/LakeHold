import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PanelErrorComponent } from './panel-error.component';

describe('PanelErrorComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  async function render(title: string, message: string | null) {
    const fixture = TestBed.createComponent(PanelErrorComponent);
    fixture.componentRef.setInput('title', title);
    fixture.componentRef.setInput('message', message);
    await fixture.whenStable();
    return fixture;
  }

  it('renders nothing at all when there is no failure', async () => {
    // Every panel embeds one unconditionally, so an empty banner would leave a gap above each.
    const fixture = await render('Could not read storage', null);
    expect(fixture.nativeElement.querySelector('.banner')).toBeFalsy();
    expect((fixture.nativeElement.textContent ?? '').trim()).toBe('');
  });

  it('names the operation alongside the message', async () => {
    const fixture = await render('Restore failed', "'x.ducklake' already exists");
    const text = fixture.nativeElement.textContent ?? '';

    expect(text).toContain('Restore failed');
    expect(text).toContain('already exists');
  });

  it('preserves the engine message verbatim, newlines and all', async () => {
    // The engine names the offending token and often suggests a correction. Collapsing its layout
    // is what makes a precise message unreadable.
    const fixture = await render('Query failed', 'Catalog Error: line 3\n  ^');
    const pre = fixture.nativeElement.querySelector('pre') as HTMLElement;

    expect(pre.textContent).toBe('Catalog Error: line 3\n  ^');
  });

  it('treats an empty message as no failure', async () => {
    const fixture = await render('Query failed', '');
    expect(fixture.nativeElement.querySelector('.banner')).toBeFalsy();
  });
});
