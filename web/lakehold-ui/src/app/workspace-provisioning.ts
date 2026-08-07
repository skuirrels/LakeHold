/** The browser affordance for the API's workspace-slug contract; the server remains authoritative. */
export const WORKSPACE_SLUG_PATTERN = '[a-z0-9][a-z0-9-]{0,62}';

const workspaceSlug = new RegExp(`^${WORKSPACE_SLUG_PATTERN}$`);

export interface WorkspaceIdentity {
  slug: string;
  displayName: string;
}

export function isWorkspaceSlug(value: string): boolean {
  return workspaceSlug.test(value.trim());
}

/** Trims and defaults the two fields shared by first-run and later workspace provisioning. */
export function normalizeWorkspaceIdentity(
  slugInput: string,
  displayNameInput: string,
): WorkspaceIdentity | null {
  const slug = slugInput.trim();
  const displayName = displayNameInput.trim() || slug;
  return isWorkspaceSlug(slug) && displayName.length <= 200 ? { slug, displayName } : null;
}
