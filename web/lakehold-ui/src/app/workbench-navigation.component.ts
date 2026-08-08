import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  input,
  output,
} from '@angular/core';

export type WorkbenchDestination =
  | 'workbench'
  | 'catalog'
  | 'queries'
  | 'history'
  | 'snapshots'
  | 'storage'
  | 'changes'
  | 'backups'
  | 'ejects'
  | 'schedule'
  | 'connectors'
  | 'users'
  | 'settings';

/** The product-level Workbench rail; its destinations remain owned by the parent shell. */
@Component({
  selector: 'lh-workbench-navigation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-navigation.component.html',
  styleUrl: './workbench-navigation.component.css',
  host: {
    role: 'navigation',
    'aria-label': 'Workbench navigation',
    '[class.closed]': '!open()',
    '[attr.aria-hidden]': "!open() && compact() ? 'true' : 'false'",
    '[attr.inert]': "open() || !compact() ? null : ''",
  },
})
export class WorkbenchNavigationComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly destination = input.required<WorkbenchDestination>();
  readonly open = input(true);
  readonly compact = input(false);
  readonly systemAdmin = input(false);

  /**
   * Whether this principal administers its own workspace — a tenant owner.
   *
   * Separate from systemAdmin because they reach different things: an instance credential
   * provisions tenants and owns System Settings, an owner administers the users and tokens of the
   * workspace they belong to. Both need the section; only the former needs every item in it.
   */
  readonly canAdminister = input(false);
  readonly navigate = output<WorkbenchDestination>();

  focusCurrentDestination(): void {
    this.host.nativeElement
      .querySelector<HTMLButtonElement>('.nav-item[aria-current="page"]')
      ?.focus();
  }

  contains(element: Element | null): boolean {
    return element !== null && this.host.nativeElement.contains(element);
  }
}
