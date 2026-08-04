# Development identity provider

`lakehold-realm.json` is imported by the `keycloak` service in `compose.yaml` at start-up. It exists
so `make dev` demonstrates the sign-in path a real deployment uses, rather than leaving browser
login as the one surface nobody exercises. **It is for development only.** The client secret and
both passwords are in this file, in source control, on purpose.

## The two seeded users

Both sign in at <http://localhost:5399> with the password `lakehold`.

| User | Reaches | How it is granted |
|---|---|---|
| `admin` | Instance administration — provisions tenants, catalogs, and tokens. Deliberately **cannot read tenant data**. | Member of the `lakehold-administrators` group, which the `groups` claim carries |
| `analyst` | The `demo` workspace as an owner — queries, writes, maintenance, eject | User attributes `tenant=demo` and `lakehold_role=owner` |

Two users rather than one because the distinction is the thing people find confusing: an instance
credential provisions but cannot query, and a tenant credential queries but cannot provision. Seeing
both makes that concrete instead of surprising.

## Giving either user the other capability

Nothing is special about which user has which. Every user carries both mechanisms; the seeded pair
simply differ in which are populated.

**To let `admin` also use the `demo` workspace** — Keycloak admin console → Users → `admin` →
Attributes → add `tenant` = `demo` and `lakehold_role` = `owner`. Sign out and back in.

**To make `analyst` an instance administrator** — Users → `analyst` → Groups → Join
`lakehold-administrators`. Sign out and back in.

A user with both is an administrator *and* a workspace owner; LakeHold reads the administrator claim
first, so that identity administers the instance. If you want to see tenant data with such an
account, leave the group.

## The claim contract

LakeHold reads exactly three claims. The mapper names in this realm are cosmetic; the **claim
names** are the contract, and they are what any other provider has to emit:

| Claim | Emitted by | LakeHold setting |
|---|---|---|
| `tenant` | `lakehold-tenant` mapper, from the `tenant` user attribute | `Lakehold:Oidc:TenantClaim` |
| `role` | `lakehold-role` mapper, from the `lakehold_role` user attribute | `Lakehold:Oidc:RoleClaim` |
| `groups` | `lakehold-groups` mapper, group membership | `Lakehold:Oidc:SystemAdminClaim`, matched against `SystemAdminValue` |

`RealmClaimContractTests` asserts these names match the configuration the development stack passes,
so editing one without the other fails the build rather than producing a login that silently lands
back on the sign-in panel.

The `lakehold-audience` mapper puts `lakehold-workbench` in the access token's audience. Without it
`Lakehold:Oidc:Audience` would have to be empty, which disables audience validation and accepts every
token the realm issued — including one minted for a different application.

## Replacing it

This realm is a worked example, not a dependency. Any OIDC provider that can emit those three claims
works; see [`docs/IDENTITY-PROVIDER-SETUP.md`](../../docs/IDENTITY-PROVIDER-SETUP.md) for pointing
LakeHold at your own, and for turning this service off.
