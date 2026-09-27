# CLAUDE.md

AI receptionist for social DMs (WhatsApp, Messenger, Instagram, X) in C# / .NET 10 (LTS): Azure OpenAI intent + replies,
Azure Speech voice, Microsoft Graph calendar booking with SMTP fallback, Blazor Server admin dashboard.

**Before helping the owner, read [docs/PROJECT-JOURNAL.md](docs/PROJECT-JOURNAL.md)**. It records what is already
configured (in order), the live tunnel URL, daily-use commands and every problem solved so far. Keep it updated when a
setup step is completed or a new problem is fixed. Never write secrets into it.

- Owner runs Windows + VS Code + PowerShell at `D:\AI-receptionist`; two terminals: the app and `devtunnel host ai-receptionist`.
- Secrets live in `dotnet user-secrets --project src/AiReceptionist.Web`, never in appsettings.json.
- Build and test: `dotnet build`, `dotnet test`. Keep all tests passing.
- Layout: `src/AiReceptionist.Core` (domain, orchestrator, scheduling), `src/AiReceptionist.Infrastructure`
  (OpenAI, Speech, Graph, SMTP, channel connectors), `src/AiReceptionist.Web` (host, webhooks, dashboard), `tests/`.
- Docs: docs/SETUP.md (setup), docs/ARCHITECTURE.md (design), docs/ACCEPTANCE-TEST.md (test and demo script).
