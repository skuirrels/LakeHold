import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { splitQualified } from './format';
import { LakehouseService } from './lakehouse.service';
import { ChangePage, Subscription } from './models';
import { PanelErrorComponent } from './panel-error.component';

/**
 * The change feed and the webhook subscriptions that push it.
 *
 * One panel, because reading changes and being pushed them are the same question asked two ways: a
 * subscription's `lastDeliveredSnapshot` only means something next to the snapshots the feed is
 * showing.
 */
@Component({
  selector: 'lh-changes-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelErrorComponent],
  templateUrl: './changes-panel.component.html',
  styleUrls: ['./panel-shared.css', './changes-panel.component.css'],
})
export class ChangesPanelComponent {
  private readonly api = inject(LakehouseService);

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  /** Base tables as `schema.table`, for the pickers. Owned by the workbench, which loads the tree. */
  readonly tables = input.required<string[]>();
  /** Hides subscription mutations for reader and demo access. */
  readonly readOnly = input(false);

  protected readonly changes = signal<ChangePage | null>(null);
  protected readonly loading = signal(false);
  protected readonly table = signal('');
  protected readonly fromSnapshot = signal(0);

  protected readonly subscriptions = signal<Subscription[]>([]);
  protected readonly formOpen = signal(false);
  protected readonly endpoint = signal('');
  protected readonly secret = signal('');
  protected readonly subTable = signal('');
  /** The subscription whose delete is awaiting confirmation. */
  protected readonly pendingUnsubscribe = signal<number | null>(null);

  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Could not read changes');

  constructor() {
    // `untracked` so the effect depends on exactly the two inputs above and not on anything
    // the reload happens to read — see storage-panel for the bug that taught us this.
    effect(() => {
      this.tenant();
      this.catalog();
      untracked(() => {
        this.changes.set(null);
        this.table.set('');
        this.subscriptions.set([]);
        // A catalog change does not destroy this panel the way a tab change does, so a failure that
        // belonged to the previous catalog has to be cleared by hand or it stands over this one.
        this.error.set(null);
        this.pendingUnsubscribe.set(null);
        this.reload();
      });
    });
  }

  /** Re-reads the subscription list. The feed is only read on demand — it is a query, not a status. */
  reload(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog) {
      this.subscriptions.set([]);
      return;
    }

    this.api.listSubscriptions(tenant, catalog).subscribe({
      next: (subscriptions) => this.subscriptions.set(subscriptions),
      error: (err: Error) => this.fail('Could not list subscriptions', err.message),
    });
  }

  protected loadChanges(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const qualified = this.table() || this.tables()[0];
    if (!tenant || !catalog || !qualified) {
      return;
    }

    const [schema, table] = splitQualified(qualified);

    this.loading.set(true);
    this.error.set(null);

    this.api.getChanges(tenant, catalog, schema, table, this.fromSnapshot()).subscribe({
      next: (page) => {
        this.changes.set(page);
        this.loading.set(false);
      },
      error: (err: Error) => {
        // A range whose end predates the table's creation is refused by the engine rather than
        // returning nothing, so this message is worth showing.
        this.fail('Could not read changes', err.message);
        this.changes.set(null);
        this.loading.set(false);
      },
    });
  }

  /** Column names for the change grid, taken from the first row that has any. */
  protected readonly columns = computed(() => {
    const first = this.changes()?.changes.find((c) => Object.keys(c.row).length > 0);
    return first ? Object.keys(first.row) : [];
  });

  protected createSubscription(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const endpoint = this.endpoint().trim();
    const secret = this.secret();
    if (!tenant || !catalog || !endpoint || !secret) {
      return;
    }

    const qualified = this.subTable();
    const [schema, table] = qualified ? splitQualified(qualified) : ['main', null];

    this.error.set(null);
    this.api
      .createSubscription(tenant, catalog, { endpointUrl: endpoint, secret, schema, table })
      .subscribe({
        next: () => {
          // The secret is write-only — no endpoint returns it — so the field is cleared rather than
          // left holding a credential the page has no further use for.
          this.endpoint.set('');
          this.secret.set('');
          this.subTable.set('');
          this.formOpen.set(false);
          this.reload();
        },
        error: (err: Error) => this.fail('Could not create the subscription', err.message),
      });
  }

  protected confirmUnsubscribe(id: number): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog) {
      return;
    }

    this.api.deleteSubscription(tenant, catalog, id).subscribe({
      next: () => {
        this.pendingUnsubscribe.set(null);
        this.reload();
      },
      error: (err: Error) => this.fail('Could not delete the subscription', err.message),
    });
  }

  private fail(title: string, message: string): void {
    this.errorTitle.set(title);
    this.error.set(message);
  }
}
