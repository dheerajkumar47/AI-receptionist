# CLAUDE.md

AI receptionist for social DMs (WhatsApp, Messenger, Instagram, X) in C# / .NET 10 (LTS): Azure OpenAI intent + replies,
Azure Speech voice, Microsoft Graph calendar booking with SMTP fallback, Blazor Server admin dashboard.

**Before helping the owner, read [docs/PROJECT-JOURNAL.md](docs/PROJECT-JOURNAL.md)**. It records what is already
configured (in order), daily-use commands and every problem solved so far. Keep it updated when a setup step is completed
or a new problem is fixed. The repository is public: never write secrets, emails, phone numbers, account IDs or live
URLs (tunnel, Azure host) into any file. Use placeholders such as `https://<your-app>.azurewebsites.net`.

- Live on Azure App Service (Linux, .NET 10); every push to `main` is built, tested and deployed by
  `.github/workflows/deploy-azure.yml`. Commits are authored as the owner, with no AI co-author trailers.
- Owner runs Windows + VS Code + PowerShell at `D:\AI-receptionist` for local development (`devtunnel host ai-receptionist`
  only when testing webhooks locally).
- Secrets: Azure App Service settings or the dashboard's Connections page in production; `dotnet user-secrets --project
  src/AiReceptionist.Web` locally. Never in appsettings.json.
- Build and test: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`. Keep all tests passing.
- Layout: `src/AiReceptionist.Core` (domain, orchestrator, scheduling), `src/AiReceptionist.Infrastructure`
  (OpenAI, Speech, Graph, SMTP, channel connectors), `src/AiReceptionist.Web` (host, webhooks, dashboard), `tests/`.
- Docs: docs/CLIENT-ONBOARDING.md (setting up a client), docs/SETUP.md (setup), docs/ARCHITECTURE.md (design), docs/ACCEPTANCE-TEST.md (test and demo script).
