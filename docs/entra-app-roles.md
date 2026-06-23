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

## 4. Authorize Built-in Agent Tools (`bash`, `read_file`, `write_file`)

The in-process built-in tools run shell commands and read/write files inside the gateway pod, so they are treated as a **privileged capability** rather than an ordinary resource. Unlike adapters/tools, they have no per-resource `requiredRoles`; access is gated on the **caller's role** — at agent create/update time *and* again at run time (tool resolution and invocation) — with **no creator bypass**. Even the author of an agent must hold the required role to reference or invoke a built-in.

- **Default (fail-closed):** only callers holding `mcp.admin` may reference or invoke built-in tools.
- To grant built-in access without full admin, create a dedicated app role (e.g. `mcp.builtin`), assign it (Section 2), then configure it on the gateway:

  ```jsonc
  // appsettings.json
  {
    "BuiltinToolSettings": {
      "RequiredRoles": [ "mcp.builtin" ]
    }
  }
  ```

  The same setting via environment variables (e.g. in the pod spec) uses the array index form:

  ```
  BuiltinToolSettings__RequiredRoles__0=mcp.builtin
  ```

  `mcp.admin` is always permitted in addition to any configured roles. Leaving `RequiredRoles` empty keeps built-ins admin-only.
