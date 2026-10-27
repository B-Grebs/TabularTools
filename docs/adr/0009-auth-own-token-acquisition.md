# ADR-0009: Acquire Entra ID tokens ourselves and pass them to ADOMD

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

Live features (DAX queries over XMLA) need Entra ID auth, interactive or service principal. M0 spike on Linux with ADOMD/TOM 19.118.3:

- Service principal in the connection string reached Entra ID (a real AADSTS error for a fake app ID).
- Without credentials: *"When interactive authentication is not supported, an external access-token is required."* So the built-in interactive login isn't available on Linux.
- `AdomdConnection.AccessToken` and `OnAccessTokenExpired` exist; with a token, ADOMD made the HTTPS call (blocked only by the sandbox proxy).
- `localhost:port` connections (Desktop, SSAS TCP) throw `NotSupportedException` on Linux.

## Decision

- `ITokenProvider` built on **Azure.Identity**, scope `https://analysis.windows.net/powerbi/api/.default`, with modes `default | azureCli | servicePrincipal | interactive | deviceCode`.
- Pass tokens via `AccessToken`; refresh via `OnAccessTokenExpired`.
- Recommend **workload identity federation** in ADO (`AzureCLI@2` + `azureCli` mode), so no secrets are stored.
- Secrets come only from environment variables (`AZURE_*`) or the platform's credential stores. Never from committed files. Profiles hold endpoint, database and auth mode only.
- Optional persistent MSAL token cache.

## Consequences

- One auth model on Windows, Linux, CI and MCP. Device code covers headless and Linux interactive use.
- We depend on Azure.Identity (MIT) and MSAL (MIT); TOM already depends on MSAL.
- Live XMLA on Linux stays "likely but unverified" until tested against a real tenant; the production target is Windows.

## Alternatives considered

- **Use ADOMD's built-in auth (`User ID=app:…;Password=…` or interactive):** puts secrets in connection strings, and interactive login doesn't work on Linux. Rejected.
