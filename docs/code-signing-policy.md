# Code signing policy

> Страница для заявки в SignPath Foundation. Заполнено 12.11.2026 (ROSHIQ 0.21.5).
>
> Перед подачей заявки владельцу репозитория осталось:
> 1. Выбрать открытую лицензию OSI (например MIT) и положить файл `LICENSE` в корень — это решение автора, программа его не принимает за вас.
> 2. Включить двухфакторную аутентификацию в GitHub (Settings → Password and authentication).
> 3. Подать заявку на https://signpath.org/ (форма «Apply») со ссылкой на репозиторий и эту страницу.
> 4. После одобрения — добавить шаг подписи в `.github/workflows/release.yml` (см. RELEASING.md, раздел SignPath).

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## Team roles

- Committers and reviewers: [Members team](https://github.com/baramobatare-rgb/ROSHIQ/graphs/contributors)
- Approvers: [baramobatare-rgb](https://github.com/baramobatare-rgb)

## Privacy policy

ROSHIQ does not send any personal data. Online features are optional and can be turned off in Settings.
When enabled, it requests public data from OpenDota (api.opendota.com): matchups, item and skill builds from
public and pro matches, percentiles for the post-match comparison and, if the user links a Steam account,
that account's public match history. Item icons come from Valve's CDN and release information from GitHub.
Linking Steam uses the official Steam OpenID sign-in page (steamcommunity.com); ROSHIQ only receives the Steam ID
and never asks for a password. All network requests are listed in the app's network log.
Game data comes only from Dota 2 Game State Integration (an official Valve mechanism) on the local computer.

## Releases

Signed binaries are built by GitHub Actions from this repository and published in GitHub Releases.
