# Books to Kindle for Jellyfin

[![From Jellyfin to Kindle](docs/assets/social-card.jpg)](https://adambkovacs.github.io/jellyfin-send-to-kindle/)

Send EPUB and PDF books from Jellyfin Web using your own SMTP account. Add each Kindle in the dashboard, then choose its label when you send a book. This plugin is a fork of [vitorpy's Send to Kindle](https://github.com/vitorpy/jellyfin-send-to-kindle).

[Project site](https://adambkovacs.github.io/jellyfin-send-to-kindle/) · [Download 2.0.0.1](https://github.com/adambkovacs/jellyfin-send-to-kindle/releases/tag/v2.0.0.1) · [Install](#install) · [Kindle access](#kindle-access) · [Remote delivery API](docs/delivery-v1.md)

![Delivery from Jellyfin Web to Kindle](docs/assets/delivery-route.svg)

## Install

1. Use Jellyfin Server 12.4.0 with Jellyfin Web. In **Dashboard → Plugins → Repositories**, add both catalog URLs below. Install **File Transformation 3.0.3.0** and **Books to Kindle 2.0.0.1**, then restart Jellyfin. [File Transformation release](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/tag/3.0.3.0)
2. Open **Dashboard → Plugins → My Plugins → Books to Kindle**. Choose **SMTP mail server** and enter your provider's SMTP settings. Add each Kindle's label and Send-to-Kindle address. Save your settings and select **Test saved connection**.
3. Add your sender to the Amazon **Approved Personal Document Email List** for each Kindle. Use a Jellyfin Web account that can download the book and send to that Kindle. On the book page, select **Send to Kindle**. Choose the Kindle and select **Send**. [Amazon sender approval](https://digprjsurvey.amazon.com/csad/help/node/GX9XLEVV8G4DB28H)

<details>
<summary>View the SMTP settings</summary>

![Books to Kindle administrator settings for SMTP, recipients and optional metadata](docs/assets/admin-settings.png)

</details>

**Catalog URLs**

```text
https://www.iamparadox.dev/jellyfin/plugins/manifest.json
https://adambkovacs.github.io/jellyfin-send-to-kindle/manifest.json
```

**Switching from v1 or upstream?** Version 2 has its own plugin ID. Privately back up the existing plugin configuration, finish or reconcile pending sends, uninstall the previous Send to Kindle variant, and restart before installing version 2. Both variants implement the same Jellyfin Web action and routes. Enter SMTP settings and devices in the new dashboard. Existing remote-service installations can retain their job store. Set the service URL, token and identity-header name for version 2 before switching.

For manual installation, download the plugin ZIP and the File Transformation ZIP for Jellyfin 12.4. Stop Jellyfin, extract each ZIP to its own versioned folder under the plugins directory, then start Jellyfin. To roll back, uninstall version 2 and restart. Restore the previous version and settings if needed. [Jellyfin plugin guide](https://jellyfin.org/docs/general/server/plugins/)

## Kindle access

Starting in 2.0.0.1, administrators can choose which accounts can send to each Kindle in **SMTP mail server** mode. In the plugin dashboard, open **Kindle recipients** and select accounts under **Jellyfin users** for that device. Save the settings.

- Leave the list empty to allow Jellyfin administrators only.
- Select accounts to limit sending to those accounts. Include the administrator's account if they should be able to use that Kindle.

Users who can see and download a supported book can send it to the enabled Kindles they have access to. The chooser shows those devices by label.

In 2.0.0.0, only Jellyfin administrators can send. **External delivery service** mode still requires a Jellyfin administrator and applies the service's access checks.

## Configure your mail provider

| Setting | Purelymail | Other SMTP providers |
| --- | --- | --- |
| Server | `smtp.purelymail.com` | Your provider's authenticated outgoing mail host |
| Connection | Port 465 with TLS, or 587 with STARTTLS | Select the port and TLS mode specified by your provider |
| Credentials | Full mailbox address and an SMTP-scoped app password | SMTP username and password or provider-issued app password |
| Sender | An address the mailbox is allowed to send from | An address approved by the provider and by Amazon |

Sending books only needs outgoing mail. The connection test checks the server connection and configured authentication without sending a book. Use an SMTP service supporting the authentication offered by your installation. [Purelymail settings](https://support.purelymail.com/support/solutions/articles/159000430778-server-settings-imap-smtp-pop3-and-managesieve-protocols) · [Purelymail app passwords](https://purelymail.com/docs/twoFactorAuthentication)

## Sending, status and metadata

| Book page | Device and format chooser |
| --- | --- |
| ![Send to Kindle on a Jellyfin Book page](docs/assets/book-action.png) | ![Device and format fields](docs/assets/chooser.png) |

The chooser displays device labels. Administrators manage recipient addresses, SMTP passwords and Google Books keys in the dashboard. Disable a device to remove it from the chooser. Password and API key fields are write-only. Protect the Jellyfin data directory and its backups. Direct SMTP uses on-disk receipts to recover retries after a restart. Receipt storage grows with use.

**Direct SMTP** sends the original supported format. Optional Google Books lookup enriches a delivery copy of an eligible EPUB using its ISBN. Enable lookup and enter your API key in **Book metadata**. Ambiguous or unsuccessful lookups leave the attachment unchanged. Standalone PDF delivery preserves the original PDF. The library file is never edited.

To use **External delivery service** mode, set `KINDLE_DELIVERY_BASE_URL` and `KINDLE_DELIVERY_TOKEN` on the Jellyfin server. Select the mode in the plugin dashboard. The service supplies devices, available formats and job status. Configure metadata lookup, conversion and identification on that service. [Delivery API contract](docs/delivery-v1.md)

**Accepted by mail provider** confirms SMTP submission. Check the Kindle library and open the document to verify delivery. After a lost response, use **Retry** or **Check previous request** to recover the same job. An **unknown** status means submission may already have happened. Check the receiving side before sending again.

## Troubleshooting

- If the button is missing, confirm File Transformation is active and refresh Jellyfin Web. Open a book you can download. In SMTP mode, check access to an enabled Kindle. In external delivery service mode, sign in as an administrator.
- If the connection test fails, check your provider's port and TLS settings. Check certificate trust and whether your provider requires an app password.
- If your provider accepted the email and the book is missing, check the selected Kindle address and attachment size. Confirm Amazon approved the sender and look for a rejection notice.

## Build

With the .NET 10 SDK installed:

```sh
dotnet build -c Release
dotnet test
```

## Upstream and license

This project is a fork of [vitorpy/jellyfin-send-to-kindle](https://github.com/vitorpy/jellyfin-send-to-kindle), based on [upstream v1.0.0.5](https://github.com/vitorpy/jellyfin-send-to-kindle/releases/tag/v1.0.0.5). The original plugin and this fork are licensed under the GNU General Public License, version 3 or later. [License notice](LICENSE) · [Full GPL text](https://www.gnu.org/licenses/gpl-3.0.html)

Downstream changes published November 2026 add the Jellyfin 12.4 build, service-backed device and format chooser, authorization checks, and retry handling. This downstream was primarily developed with AI coding agents. The website uses original generated artwork. Font licenses are included alongside the [self-hosted font files](docs/assets/fonts/).
