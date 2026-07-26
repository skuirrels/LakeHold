import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ChangesPanelComponent } from './changes-panel.component';
import { LakehouseService } from './lakehouse.service';
import { FakeLakehouseService } from './test-doubles';

describe('ChangesPanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<ChangesPanelComponent>;

  async function mount(tables: string[] = ['main.orders']) {
    fixture = TestBed.createComponent(ChangesPanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('tables', tables);
    await fixture.whenStable();
    return fixture;
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  function click(selector: string): void {
    (fixture.nativeElement.querySelector(selector) as HTMLButtonElement).click();
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('lists subscriptions on load but does not read the feed unasked', async () => {
    await mount();

    // The feed is a query, not a status: reading every table's changes on tab open would be a
    // surprising amount of work for something nobody asked for.
    expect(api.countOf('listSubscriptions')).toBe(1);
    expect(api.countOf('getChanges')).toBe(0);
  });

  it('splits a schema-qualified table on the first dot when reading changes', async () => {
    await mount(['warm.my.table']);
    click('.panel-controls .btn-primary');
    await fixture.whenStable();

    // DuckLake stores tables whose names contain dots. Splitting on the last one asks for a schema
    // that does not exist.
    expect(api.lastArgs('getChanges')).toEqual(['demo', 'analytics', 'warm', 'my.table', 0]);
  });

  it('derives its columns from the first row that has any', async () => {
    api.changes = {
      schema: 'main',
      table: 'orders',
      fromSnapshot: 0,
      toSnapshot: 9,
      truncated: false,
      changes: [
        { snapshotId: 7, rowId: 0, changeType: 'insert', row: { id: 1, status: 'new' } },
        { snapshotId: 9, rowId: 0, changeType: 'update_postimage', row: { id: 1, status: 'shipped' } },
      ],
    };

    await mount();
    click('.panel-controls .btn-primary');
    await fixture.whenStable();

    const headers = [...fixture.nativeElement.querySelectorAll('thead th')].map((th) =>
      (th as HTMLElement).textContent?.trim(),
    );
    expect(headers).toContain('id');
    expect(headers).toContain('status');
    expect(text()).toContain('shipped');
  });

  it('marks the two halves of an update differently from a delete', async () => {
    api.changes = {
      ...api.changes,
      changes: [
        { snapshotId: 9, rowId: 2, changeType: 'update_preimage', row: { id: 3 } },
        { snapshotId: 9, rowId: 2, changeType: 'update_postimage', row: { id: 3 } },
      ],
    };

    await mount();
    click('.panel-controls .btn-primary');
    await fixture.whenStable();

    // An update arrives as two rows sharing a row id. Styling the pre-image like a delete would
    // misread the feed — it is not a deletion.
    expect(fixture.nativeElement.querySelector('.ct-update_preimage')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.ct-update_postimage')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.ct-delete')).toBeFalsy();
  });

  it('reports truncation, so a short list is not read as the whole story', async () => {
    api.changes = { ...api.changes, truncated: true, changes: [] };
    await mount();
    click('.panel-controls .btn-primary');
    await fixture.whenStable();

    expect(text()).toContain('truncated');
  });

  describe('creating a subscription', () => {
    async function fillAndSubmit() {
      await mount();
      click('.panel-head .btn');
      await fixture.whenStable();

      const endpoint = fixture.nativeElement.querySelector('input[type=url]') as HTMLInputElement;
      const secret = fixture.nativeElement.querySelector('input[type=password]') as HTMLInputElement;
      endpoint.value = 'https://example.com/hook';
      endpoint.dispatchEvent(new Event('input'));
      secret.value = 'shhh';
      secret.dispatchEvent(new Event('input'));
      await fixture.whenStable();

      click('.sub-form .btn-primary');
      await fixture.whenStable();
    }

    it('sends the endpoint, the secret, and the whole catalog when no table is chosen', async () => {
      await fillAndSubmit();

      expect(api.lastArgs('createSubscription')).toEqual([
        'demo',
        'analytics',
        { endpointUrl: 'https://example.com/hook', secret: 'shhh', schema: 'main', table: null },
      ]);
    });

    it('does not keep the secret afterwards', async () => {
      await fillAndSubmit();

      // Asserting the field is gone would pass for the wrong reason: submitting closes the form, so
      // the input is removed whether or not the value was cleared. Reopen it and read the field —
      // that is the only way to see what the component still holds.
      click('.panel-head .btn');
      await fixture.whenStable();

      // The secret is write-only — no endpoint returns it — so the page has no further use for it,
      // and holding a credential it cannot re-read is pure risk.
      const secret = fixture.nativeElement.querySelector('input[type=password]') as HTMLInputElement;
      expect(secret).toBeTruthy();
      expect(secret.value).toBe('');
    });

    it('does not keep the endpoint either, so the next subscription starts clean', async () => {
      await fillAndSubmit();
      click('.panel-head .btn');
      await fixture.whenStable();

      const endpoint = fixture.nativeElement.querySelector('input[type=url]') as HTMLInputElement;
      expect(endpoint.value).toBe('');
    });
  });

  describe('deleting a subscription', () => {
    beforeEach(() => {
      api.subscriptions = [
        {
          id: 4,
          catalog: 'analytics',
          schema: 'main',
          table: 'orders',
          endpointUrl: 'https://example.com/hook',
          active: true,
          lastDeliveredSnapshot: 9,
          consecutiveFailures: 0,
          lastAttemptUtc: null,
          lastError: null,
          createdUtc: '2026-07-26T00:00:00Z',
        },
      ];
    });

    it('asks first', async () => {
      await mount();
      click('.subs table .row-btn');
      await fixture.whenStable();

      expect(api.countOf('deleteSubscription')).toBe(0);
      expect(text()).toContain('Confirm');
    });

    it('deletes once confirmed', async () => {
      await mount();
      click('.subs table .row-btn');
      await fixture.whenStable();
      click('.subs table .btn-danger');
      await fixture.whenStable();

      expect(api.lastArgs('deleteSubscription')).toEqual(['demo', 'analytics', 4]);
    });

    it('shows delivery trouble without needing the operator to go looking', async () => {
      api.subscriptions = [{ ...api.subscriptions[0], consecutiveFailures: 3, lastError: 'refused' }];
      await mount();

      // A subscription you cannot observe is one you do not trust.
      expect(text()).toContain('3 failures');
    });

    it('carries neither the list nor a failure across to another catalog', async () => {
      await mount();
      expect(text()).toContain('https://example.com/hook');

      api.failures.set('listSubscriptions', 'boom');
      fixture.componentRef.setInput('catalog', 'other');
      await fixture.whenStable();

      // A catalog change does not destroy this panel the way a tab change does. A subscription
      // listed under the wrong catalog invites deleting one that belongs to a different catalog.
      expect(text()).not.toContain('https://example.com/hook');
      expect(text()).toContain('Could not list subscriptions');
      expect(text()).not.toContain('No subscriptions');

      api.failures.clear();
      fixture.componentRef.setInput('catalog', 'third');
      await fixture.whenStable();
      expect(text()).not.toContain('boom');
    });
  });
});
