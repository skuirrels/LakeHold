import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MemberAdministrationComponent } from './member-administration.component';
import { TokenAdministrationComponent } from './token-administration.component';

/**
 * Who and what may reach a workspace: the people admitted from the identity provider and the tokens
 * issued to clients.
 *
 * Its own destination rather than a section of System Settings, because the two answer to different
 * credentials. Everything here is `Capability.TenantAdmin` — a workspace owner administers it — while
 * the settings page is instance-scoped. Sharing one page meant an owner opened a surface whose first
 * card they were not allowed to read.
 */
@Component({
  selector: 'lh-people',
  imports: [MemberAdministrationComponent, TokenAdministrationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './people.component.html',
  styleUrl: './admin-page.css',
})
export class PeopleComponent {}
