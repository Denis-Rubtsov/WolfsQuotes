# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Telegram bot (.NET 10, C#, Telegram.Bot 22) serving "Волчьи цитаты" — a curated quote database with user suggestions, admin moderation, like/dislike voting, inline mode, and AI quote generation via the OpenAI API. All comments, log messages, and user-facing strings are in Russian; keep new ones in Russian too.

## Commands

```bash
dotnet build                # from repo root (builds Vlk.sln)
cd Vlk && dotnet run        # run the bot (needs config, see below)
```

There are no tests.

Configuration comes from `Vlk/appsettings.json` overridden by environment variables: `Token`, `ADMIN_IDS` (comma/semicolon/space-separated), `AiApikey`, `QuotesFile`, `BASIC_URL` (voice audio base URL for inline mode), `SystemPromptFile` (optional; re-read on every generation, so prompt edits apply without restart), `StarsPrice` (initial Telegram Stars price of one over-limit generation, default 10, 0 disables selling — overridden at runtime by `/setprice`, persisted in `BotData.stars_price`). Optional S3 storage: with `S3_BUCKET` set (+ `S3_ACCESS_KEY`/`S3_SECRET_KEY`; endpoint derived from `BASIC_URL` unless `S3_URL`), the database and system prompt live in a private bucket; see "S3 sync" below. The Docker image (`Dockerfile`) presets `QuotesFile`/`SystemPromptFile`/`TZ` under the `/data` volume; `deploy/vlk.container` (rootless Podman quadlet) supplies `Token`/`AiApikey`/`S3_ACCESS_KEY`/`S3_SECRET_KEY` from podman secrets `vlk-token`/`vlk-aiapikey`/`vlk-s3-access-key`/`vlk-s3-secret-key` and the rest from `~/.config/vlk/vlk.env`. The README documents these and every bot command — update it when they change.

## Architecture

Single project (`Vlk/`), no DI container and no interfaces — services are concrete classes wired by hand in `Program.cs`, which then blocks forever while `Telegram.Bot` long-polling delivers updates. `BotService` routes messages and callback buttons; `InlineHandler` routes inline queries; both share the same underlying services.

**Persistence and locking.** All state (quotes, suggestions, ratings, the public-generation flag) lives in one `BotData` object held by `DataService` and serialized to a single JSON file. There is no per-entity storage: any code touching `DataService.Data` must hold `DataService.Lock`, and mutations must call `Save()` before releasing it. `Save()` writes to a `.tmp` file and renames so a crash mid-write can't truncate the database. Update handlers run concurrently, so this lock is the only thing guarding the data — never read or write `Data` outside it.

**S3 sync.** `DataService.LoadAsync` picks whichever copy (S3 object or local `QuotesFile`) has the newer `BotData.updated_at` (stamped by every `Save()`), and refuses to start if S3 is unreachable and there is no local copy — an empty base would overwrite S3 on the first save. `Save()` stays synchronous (local tmp+rename) and only hands the latest JSON to a background uploader that debounces ~5 s, retries with backoff, and makes a once-a-day copy under `backups/` (pruned to `S3_BACKUP_DAYS`). `Program` handles SIGTERM/SIGINT and calls `DataService.FlushAsync` so the last version reaches S3 before exit. Assumes a single bot instance — there is no conflict detection. `AiQuoteService` fetches the prompt from S3 per generation with `If-None-Match` and falls back to the last fetched text. `S3Storage` disables SDK v4's default CRC checksums (`WHEN_REQUIRED`) because S3-compatible stores (Beget is Ceph) often reject them.

**Quote identity.** Quotes are plain strings; their stable ID is `QuoteService.HashOf` — the first 10 hex chars of the MD5 of the trimmed lowercased text. This hash is the key of the `ratings` dictionary and is embedded in callback data (Telegram's 64-byte limit is why it's short). Consequence: editing a quote's text changes its hash and orphans its existing votes.

**Multi-step conversations.** `/suggest`, `/addquote`, and `/editquote` put the user into a `UserState` (mode + pending text, 30-minute TTL) stored in an in-memory dictionary in `BotService`; the next plain message from that user is interpreted against that state. These states, like the `RateLimiter` counters, do not survive a restart — only what's in the JSON file does.

**AI generation.** `AiQuoteService` calls `gpt-4o-mini`, few-shotting the style with 10 random existing quotes. Non-admin generation is gated twice: the persisted `allow_public_generation` flag (toggled by `/publicgen`) and a 5-per-hour in-memory `RateLimiter`; admins bypass both. Over the limit the bot sells single generations for Telegram Stars (`SendInvoiceAsync` with currency `XTR` and empty provider token): pre-checkout is auto-approved for the known payload, a successful payment credits `paid_generations` in `BotData` (persisted) and immediately triggers a generation; a failed AI call refunds the credit. Paid credits are honored in `/generate`, the regenerate button, and inline `ai`. Admins can change the price at runtime with `/setprice <n>`, persisted in `BotData.stars_price` (null falls back to the `StarsPrice` config value).

**Admin model.** Admin user IDs come from `ADMIN_IDS` at startup. Admins get an extended Telegram command menu (set per-chat in `BotService.Start()`), can mutate the database directly, moderate the suggestion queue, and are notified of each other's additions.
