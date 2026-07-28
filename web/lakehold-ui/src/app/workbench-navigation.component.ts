import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

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
  | 'schedule';

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
    '[attr.aria-hidden]': '!open()',
    '[attr.inert]': "open() ? null : ''",
  },
})
export class WorkbenchNavigationComponent {
  readonly destination = input.required<WorkbenchDestination>();
  readonly open = input(true);
  readonly navigate = output<WorkbenchDestination>();
}
