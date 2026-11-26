# Books to Kindle for Jellyfin

[![From Jellyfin to Kindle](docs/assets/social-card.jpg)](https://adambkovacs.github.io/jellyfin-send-to-kindle/)

Send an EPUB or PDF from its Jellyfin Web book page using your own SMTP account. Name each Kindle once, then choose its label whenever you send a book. This is a fork of [vitorpy’s Send to Kindle](https://github.com/vitorpy/jellyfin-send-to-kindle), with multi-device delivery, persistent job receipts, and optional book metadata lookup.

[Project site](https://adambkovacs.github.io/jellyfin-send-to-kindle/) · [Download 2.0.0.0](https://github.com/adambkovacs/jellyfin-send-to-kindle/releases/tag/v2.0.0.0) · [Install](#install) · [Remote delivery API](docs/delivery-v1.md)

![Delivery from Jellyfin Web to Kindle](docs/assets/delivery-route.svg)

## Install

1. Use Jellyfin Server and Web **12.4**. In **Dashboard → Plugins → Repositories**, add both catalog URLs below. Install **File Transformation 3.0.3.0** and **Books to Kindle 2.0.0.0**. Restart Jellyfin. [File Transformation release](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/tag/3.0.3.0)
2. Open **Dashboard → Plugins → My Plugins → Books to Kindle**. Choose **SMTP mail server**, enter your provider settings, sender address and credentials, and add each device’s label and personal document address. Save your settings and test the SMTP connection.
3. Add the sender to your Amazon **Approved Personal Document Email List**. Sign in to Jellyfin Web as an administrator who can download the book. Open **Books → book → Send to Kindle**, choose your device, and send. [Amazon sender approval](https://www.amazon.com/gp/help/customer/display.html?nodeId=GX9XLEVV8G4DB28H)

<details>
<summary>View the Jellyfin admin settings</summary>

![Books to Kindle administrator settings for SMTP, recipients and optional metadata](docs/assets/admin-settings.png)

</details>

**Catalog URLs**

```text
https://www.iamparadox.dev/jellyfin/plugins/manifest.json
https://adambkovacs.github.io/jellyfin-send-to-kindle/manifest.json
```

**Switching from v1 or upstream?** Version 2 has its own plugin ID. Privately back up the existing plugin configuration, finish or reconcile pending sends, uninstall the previous Send to Kindle variant, and restart before installing version 2. Both variants implement the same Jellyfin Web action and routes. Enter SMTP settings and devices in the new dashboard. Existing remote-service installations can retain their job store. Set the service URL, token and identity-header name for version 2 before switching.

**Manual install:** download the plugin ZIP and the File Transformation ZIP for Jellyfin 12.4. Stop Jellyfin, extract each ZIP to its own versioned folder under the plugins directory, then start Jellyfin. For rollback, uninstall version 2 and restart. Restore the previous version and settings if needed. [Jellyfin plugin guide](https://jellyfin.org/docs/general/server/plugins/)

## Configure your mail provider

| Setting | Purelymail | Other SMTP providers |
| --- | --- | --- |
| Server | `smtp.purelymail.com` | Your provider’s authenticated outgoing mail host |
| Connection | Port 465 with TLS, or 587 with STARTTLS | Select the port and TLS mode specified by your provider |
| Credentials | Full mailbox address and an SMTP-scoped app password | SMTP username and password or provider-issued app password |
| Sender | An address the mailbox is allowed to send from | An address approved by the provider and by Amazon |

Sending books only needs outgoing mail. The connection test checks the server connection and configured authentication without sending a book. Use an SMTP service supporting the authentication offered by your installation. [Purelymail settings](https://support.purelymail.com/support/solutions/articles/159000430778-server-settings-imap-smtp-pop3-and-managesieve-protocols) · [Purelymail app passwords](https://purelymail.com/docs/twoFactorAuthentication)

## Sending, status and metadata

| Book page | Device and format chooser |
| --- | --- |
| ![Send to Kindle on a Jellyfin Book page](docs/assets/book-action.png) | ![Device and format fields](docs/assets/chooser.png) |

Device labels and opaque IDs appear in the Book chooser. Recipient addresses belong in the administrator settings. Disable a device to remove it from the chooser. SMTP passwords and Google Books keys are write-only in the dashboard. Protect the Jellyfin data directory and its backups. Direct SMTP keeps job receipts on disk to recover retries after a restart; allow for receipt storage to grow with usage.

**Direct SMTP** sends the original supported format. Optional Google Books lookup enriches a delivery copy of an eligible EPUB using its ISBN. Enable lookup and enter your API key in **Book metadata**. Ambiguous or unsuccessful lookups leave the attachment unchanged. Standalone PDF delivery preserves the original PDF. The library file is never edited.

**Remote service mode** connects to a separately operated service implementing the [delivery API](docs/delivery-v1.md). Set `KINDLE_DELIVERY_BASE_URL` and `KINDLE_DELIVERY_TOKEN` on the Jellyfin server and select remote service mode. The service supplies devices, available derivatives and job status. Its enrichment, conversion and optional identification providers are configured on that service.

**Accepted by mail provider** confirms SMTP submission. Check the Kindle library and open the document to verify delivery. After a lost response, use **Retry** or **Check previous request** to recover the same job. An **unknown** status means submission may already have happened. Check the receiving side before sending again.

## Troubleshooting

- **Missing book action:** confirm File Transformation is active, refresh Jellyfin Web and sign in as an administrator. Select a visible, downloadable Book with a physical file.
- **Connection test fails:** check TLS mode, port, certificate trust, authentication, and whether your provider requires an app password.
- **Mail accepted but the book is missing:** verify Amazon approved the sender, check the selected device address and attachment size, and look for an Amazon rejection notice.

## Build

With the .NET 10 SDK installed:

```sh
dotnet build -c Release
dotnet test
```

## Upstream and license

This project is a fork of [vitorpy/jellyfin-send-to-kindle](https://github.com/vitorpy/jellyfin-send-to-kindle), based on [upstream v1.0.0.5](https://github.com/vitorpy/jellyfin-send-to-kindle/releases/tag/v1.0.0.5). The original plugin and this fork are licensed under the GNU General Public License, version 3 or later. [License notice](LICENSE) · [Full GPL text](https://www.gnu.org/licenses/gpl-3.0.html)

Downstream changes published November 2026 add the Jellyfin 12.4 build, service-backed device and format chooser, authorization checks, and retry handling. This downstream was primarily developed with AI coding agents. The website uses original generated artwork. Font licenses are included alongside the [self-hosted font files](docs/assets/fonts/).
