export type OrganizationId = 'kirma' | 'bukinistka';

export type OrganizationAuthType = 'shopify' | 'odoo';

export type Organization = {
  id: OrganizationId;
  name: string;
  subtitle: string;
  authType: OrganizationAuthType;
};

export const organizations: Organization[] = [
  {
    id: 'kirma',
    name: 'Kirma.sh',
    subtitle: 'Уваход праз Shopify',
    authType: 'shopify',
  },
  {
    id: 'bukinistka',
    name: 'Bukinistka',
    subtitle: 'Логін і пароль Odoo',
    authType: 'odoo',
  },
];

export function getOrganization(
  id: string | null | undefined
): Organization | undefined {
  return organizations.find((org) => org.id === id);
}
