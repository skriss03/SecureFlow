# Demo assets

| Asset | Use |
|---|---|
| `demo-repo/` | The "ShopFast" repository with deliberate flaws. Point the **Git repository** card at this folder's full path (or push it to GitHub and use the URL). Expected findings include hardcoded secrets in `src/Orders/appsettings.json`, a single SQL instance shared by two services, no timeouts or circuit breakers, a worker called synchronously. |
| `shopfast.drawio` | The same architecture as a draw.io diagram. Upload it on the **draw.io** card. Parsed with no AI. Also open it in draw.io and export as PNG to get a clean diagram image for the **image** card. |
| `ci/secureflow-gate.yml` | GitHub Actions workflow that fails a pull request when the committed `secureflow-model.json` has Critical findings. |
| `diagram.html` | A self-contained HTML rendering of the architecture. Open it in a browser and screenshot it to produce `diagram.png` for the image demo when you do not have draw.io handy. |

## Whiteboard photo

Draw the ShopFast architecture on a whiteboard (boxes: Shopper, CDN, API Gateway, Storefront, Orders API, Inventory API, Orders DB, Redis, Notification Worker, Payments, Email; arrows between them; two dashed boundaries "Internet" and "Private network"). Photograph it in good light, as square-on as possible. Upload it with the hint "e-commerce, Kubernetes, SQL Server".

Rehearse each input once while online so the replay cache holds the responses, then rehearse again with `.\scripts\run.ps1 -Offline`.
