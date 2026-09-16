# ShopFast

A small e-commerce platform used as the SecureFlow demo repository. It is deliberately flawed.

Services:

- **Storefront** (ASP.NET Core web app) renders the shop and calls Orders API.
- **Orders API** takes orders, reserves stock via Inventory API, charges through the payment provider, and pings the Notification Worker.
- **Inventory API** manages stock levels in the same SQL database as Orders.
- **Notification Worker** sends order confirmation e-mails via SendGrid.

Infrastructure: SQL Server (single container), Redis for sessions, nginx as the API gateway. Deployed to Kubernetes from `deploy/k8s`, or locally with `docker compose up`.

Known shortcuts (do not copy):

- One SQL Server instance, no backups, shared by two services.
- Orders API runs as a single replica and has no health probe.
- Connection strings and API keys live in `appsettings.json`.
- HTTP everywhere inside the cluster; no timeouts, retries or circuit breakers on outbound calls.
- The worker is called synchronously over HTTP instead of through a queue.
