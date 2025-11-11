# Azure Entra ID Application Roles Setup

Follow these steps to enable application roles, assign them to identities, and pass the role value into adapter/tool definitions for authorization.

## 1. Enable and Configure Roles on the App Registration
- Open the Azure portal and navigate to **Entra ID > App registrations**; select the app the gateway trusts (e.g., `McpGateway`).
- Go to **App roles** and choose **Create app role**.
  - **Display name**: Friendly label shown in the portal (e.g., `Adapter Reader`).
  - **Allowed member type**: `Users/Groups` for people or `Applications` for service principals.
  - **Value**: Immutable string used by Mcp Gateway authorization (e.g., `mcp.engineer`).
  - Provide a meaningful description so administrators know when to grant it.
  - Save the role. Repeat for each logical permission your org needs; you can create any value pattern (e.g., `mcp.engineer`, `mcp.scientist`).
- **Always create the admin role** with the value `mcp.admin`. This value is used by the gateway to grant elevated write access beyond the resource creator.
- Make sure **App roles** shows the new entries in the table; this automatically updates the app manifest.

## 2. Assign Roles to Identities
- In the same app registration, open **Enterprise applications** (service principal) entry.
- Navigate to **Users and groups > Add user/group**.
  - Select a user, group, or service principal.
  - Choose the desired application role value.
  - Confirm the assignment.
- Users receive the role via their next sign-in; apps inherit the claims immediately once the service principal is updated.

## 3. Provide the Role Value When Creating Adapters or Tools
- When calling the management APIs (or CLI) to create adapters/tools, populate the `requiredRoles` collection with the exact **Value** strings created above.
- Example payload fragment:
  ```json
  {
    "name": "sample-adapter",
    // ...
    "requiredRoles": ["mcp.engineer", "mcp.scientist"]
  }
  ```
- The gateway’s `SimplePermissionProvider` grants:
  - **Read** access if the caller is the creator, holds `mcp.admin`, or matches one of the `requiredRoles` entries.
  - **Write** access if the caller is the creator or holds `mcp.admin`.

  > If no `requiredRoles` is configured, it by default ALLOW ALL READ access.
