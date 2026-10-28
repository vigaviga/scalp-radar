# ScalpRadar

A 24/7 Windows service that watches the most liquid Binance USDⓈ-M USDT perpetuals and sends a
Telegram alert when a coin lines up for a scalp: a structure break, backed by volume, open interest,
healthy funding, CVD, liquidity and the order book. Every alert is saved to Postgres with its outcome
(what happened in the next 60 minutes), so you can measure which signals work and tune the thresholds.

**It never trades.** It places no orders, calls no account or signed (USER_DATA) endpoints and uses no
API keys. It reads public market data only. Alerts never suggest leverage or position size.

- [Setup from zero](#setup-from-zero)
- [Telegram commands](#telegram-commands)
- [How it works](#how-it-works)
- [Settings](#settings)
- [Data and analysis](#data-and-analysis)
- [Binance limits and where the docs differ from the spec](#binance-limits-and-where-the-docs-differ-from-the-spec)
- [Troubleshooting](#troubleshooting)

## Setup from zero

You need Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
[Docker Desktop](https://www.docker.com/products/docker-desktop/) and a Telegram account.
All commands run in PowerShell from the repo folder.

### 1. Build and test

```powershell
dotnet build
dotnet test
```

### 2. Create the Telegram bot

1. In Telegram, open a chat with **@BotFather** and send `/newbot`.
2. Choose a display name and a username ending in `bot`.
3. BotFather replies with a **token** like `123456789:AAH...`. Treat it like a password: don't paste
   it into chats, issues or files in this repo.
4. Optional: send `/setcommands` to BotFather, pick your bot, and paste:
   ```
   status - uptime, streams, data age, REST weight, alerts today
   top - top symbols by current score
   stats - outcome stats per tier and direction, e.g. /stats 30
   mute - mute alerts for N minutes, e.g. /mute 60
   unmute - unmute alerts
   ```

### 3. Get your chat id

1. Open a chat with your new bot and send it any message (e.g. `hi`).
2. In a browser, open `https://api.telegram.org/bot<TOKEN>/getUpdates` (put your token in place of `<TOKEN>`).
3. Find `"chat":{"id":123456789,...}`. That number is your chat id.
   - For a group: add the bot to the group, send a message in the group, and use the group's id
     (it starts with `-`).
   - If the result is empty, send the bot another message and reload. `getUpdates` only works while
     ScalpRadar is **not** running, because ScalpRadar reads the updates itself.

The bot answers commands from this chat id only and ignores everyone else.

### 4. Start Postgres

```powershell
Copy-Item .env.example .env
notepad .env          # set POSTGRES_PASSWORD to a long random value
docker compose up -d
docker compose ps     # scalpradar-postgres should become "healthy"
```

Postgres listens on host port **5433** (not 5432), so it won't clash with a local Postgres. The data
lives in the Docker volume `scalpradar-pgdata`. ScalpRadar creates and upgrades the tables itself at
startup (DbUp).

### 5. Store the secrets for console runs

Secrets never go into `appsettings.json` or the repo. For console runs they live in
`dotnet user-secrets` (in your Windows profile, outside the repo). This reads the token with hidden
input, so it doesn't end up in your PowerShell history:

```powershell
$p = 'src/ScalpRadar.Worker'
$token = [Net.NetworkCredential]::new('', (Read-Host 'Bot token' -AsSecureString)).Password
dotnet user-secrets set "Telegram:BotToken" $token --project $p
Remove-Variable token
dotnet user-secrets set "Telegram:ChatId" "123456789" --project $p   # your chat id
dotnet user-secrets set "Telegram:Enabled" "true" --project $p

$db = [Net.NetworkCredential]::new('', (Read-Host 'Postgres password (same as .env)' -AsSecureString)).Password
dotnet user-secrets set "Database:Password" $db --project $p
Remove-Variable db

dotnet user-secrets list --project $p   # check (this prints the values)
```

Environment variables work too and take precedence: `Telegram__BotToken`, `Telegram__ChatId`,
`Telegram__Enabled`, `Database__Password`.

### 6. Run it in the console

```powershell
dotnet run --project src/ScalpRadar.Worker -c Release
```

You should get "🟢 ScalpRadar started" in Telegram. Send `/status` to check it. Stop with `Ctrl+C`.
On shutdown it saves everything still queued.

**Dry run without Telegram:** alerts are printed to the console instead (still saved to Postgres):

```powershell
dotnet run --project src/ScalpRadar.Worker -c Release -- --Telegram:Enabled=false
```

The first minute is warm-up: it picks the universe, subscribes to the streams and backfills about
4 h of 1m and 24 h of 5m klines plus 24 h of open interest per symbol, spread over about 60 s. No
symbol can alert until its backfill is done.

### 7. Install it as a Windows service

Open PowerShell **as Administrator** in the repo folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass   # this window only
.\install-service.ps1                       # optional: -InstallDir D:\ScalpRadar
```

The script:
- publishes a Release build to `C:\ScalpRadar`;
- asks for the bot token (hidden input) and the chat id, and reads the database password from `.env`;
- stores them as environment variables for this service only, under
  `HKLM\SYSTEM\CurrentControlSet\Services\ScalpRadar\Environment`;
- creates the service (automatic delayed start, LocalSystem) and sets it to restart 1 minute after any
  crash;
- starts it and shows the last log lines.

The service can't see your user-secrets (it runs as LocalSystem), which is why it gets its own copy.
Local accounts on the PC can read a service's registry key, so this suits a personal machine.

**Update** after pulling new code: run `.\install-service.ps1` again. Press Enter at each prompt to
keep the stored values.
**Remove:** `.\uninstall-service.ps1` (add `-RemoveFiles` to delete `C:\ScalpRadar` too). This also
removes the stored secrets. The Postgres data is kept.

Manage it with `Get-Service ScalpRadar`, `Restart-Service ScalpRadar` or `services.msc`.
Logs go to `C:\ScalpRadar\logs\scalpradar-YYYYMMDD.log` (kept 14 days).

### 8. Keep the PC awake

The PC must not sleep, or the streams stop:

```powershell
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
```

Docker Desktop starts only when you sign in to Windows. Turn on *Settings → General → Start Docker
Desktop when you sign in*. The container has `restart: unless-stopped`, so it comes back with Docker.
After a reboot the service starts before Docker. It retries the database for 1 minute, exits, and is
restarted every minute until Postgres is up. You'll see "🟢 ScalpRadar started" when it's running.

## Telegram commands

| Command | What it does |
|---|---|
| `/status` | Uptime, symbols tracked and backfilled, order books, connected streams, oldest trade age, REST weight used, events/s, alerts today, mute state |
| `/top` | The top 5 symbols by current score (must-haves × 10 + confirmations), with the ✓/✗ of each check and any veto |
| `/stats [days]` | Per tier and direction: count, % target hit first, % invalidation hit first, median MFE and MAE. Default 7 days |
| `/mute <minutes>` | Stops sending alerts and notices. They are still saved, tracked and logged |
| `/unmute` | Sends again |

Automatic messages:
- **⚠ feed down** when no trades have arrived for 2 minutes, and **✅ feed recovered** when they resume.
- **📅 Daily summary** at 00:00 UTC: alerts sent per tier and direction, outcomes, near misses and the
  checks that failed most often. Liquidations older than 30 days are deleted at the same time.
- **📋 Digest**: at most 8 alerts go out per 15-minute window. The rest are sent as one digest when
  the window ends.

## How it works

**Universe.** Every hour: USDT perpetuals that are trading, listed at least 3 days, not stablecoins,
with at least $20M 24h quote volume, top 100 by volume. BTCUSDT's 15-minute move is shown in each alert
for context.

**Data.** WebSockets: `aggTrade` per symbol, `!markPrice@arr` (mark price and funding),
`!forceOrder@arr` (liquidations). REST: open interest every 30 s per symbol, klines and OI history for
the backfill, and full local order books (diff stream plus snapshot) for up to 15 shortlisted symbols.
Candles, CVD, pivots, ATR and the liquidation heatmap live in memory only.

**One event loop.** All events go into one bounded channel. A single consumer owns all symbol state, so
there are no locks. Every 10 s a tick closes candles and scores every symbol.

**Rules** (all thresholds in `appsettings.json`, section `Rules`):

| | LONG (SHORT is the mirror) |
|---|---|
| **M1 Structure** | Higher low, then the 1m close breaks the last 5m pivot high |
| **M2 Volume** | Last 5 min volume ≥ 2.5× the 4 h median, and ≥ 60% of the last 15 min on green candles |
| **M3 OI** | OI +1% over 15 min and still rising over 5 min, with price up: new longs. Short covering never alerts |
| **M4 Funding** | 8h-normalised rate ≤ +0.03% (longs not crowded) |
| C1 CVD | 15 min CVD rising and the last 5 min CVD ≥ 10% of volume |
| C2 Liquidity | Estimated short liquidations above ≥ 1.5× long liquidations below (0.3–3% away) |
| C3 Book | 30 s average bid/ask imbalance within ±0.5% ≥ +0.20 |
| V1 Divergence | Price at or above the last pivot high while CVD is below its level at that pivot |
| V2 Wall | An opposing wall (≥ 5× median level, held ≥ 20 s) within 0.3% |
| V3 Chasing | Price more than 1.5 ATR(5m) past the breakout level |
| V4 Spread | Spread over 0.05% |
| V5 Stale data | Trade > 10 s, OI > 90 s, mark > 15 s or book > 5 s old; backfill not finished; gap being refilled |
| V6 Cooldown | Same symbol and direction within 30 min (one ✅ → 🔥 upgrade allowed) |

All 4 must-haves, no veto and 2 of 3 confirmations give ✅ ALERT; 3 of 3 give 🔥 STRONG. When all
must-haves pass but a confirmation or veto fails, that is a **near miss**: saved to `near_misses` at
most once per 5 minutes per symbol, so you can see what nearly fired.

**Shortlist and order books.** Symbols with at least 3 must-haves passing get a local order book (up to
15, scored by the same score). A book is dropped after 10 minutes off the shortlist. An alert waits up
to 30 s for 30 s of book history.

**Outcomes.** Each alert is tracked for 60 minutes from live trades: the price at +5, +15 and +60 min,
the MFE and MAE (best and worst move, in % of the entry price, both ≥ 0) and which came first, target or
invalidation. The first hit is decided on 1-minute high/low; if both happen in the same minute it counts
as invalidation (conservative). After a restart, or if a symbol left the universe, unfinished outcomes
are resolved from 1m klines once their 60 minutes have passed. The first kline may include a few
seconds before the alert.

**Reliability.**
- After a WebSocket reconnect, the affected symbols are marked stale (vetoed) until the gap is backfilled
  from REST klines.
- REST calls go one at a time through a gate that reads `X-MBX-USED-WEIGHT-1M` and keeps usage under
  50% of the cap. It backs off on 429 and pauses all REST calls until a 418 ban ends.
- A failed stream subscription is retried every 30 s.
- On shutdown, queued alerts, outcomes and liquidations are written before the process exits.

## Settings

Every threshold is in `src/ScalpRadar.Worker/appsettings.json` (installed copy:
`C:\ScalpRadar\appsettings.json`; restart the service after editing). Any setting can also be overridden
with an environment variable such as `Rules__MinVolRatio=3`.

Each alert and near miss stores a `config_hash` of the `Rules`, `Heatmap`, `OrderBook` and `State`
sections. When you change a threshold, the hash changes, so stats can be split by threshold version.

| Section | What it controls |
|---|---|
| `Universe` | Size, volume floor, listing age, exclusions, refresh interval, benchmark |
| `Binance` | Polling intervals, backfill depth, REST budget (fraction of the cap), stream subscribe pacing |
| `Engine` | Tick interval, channel size, lag warnings |
| `State` | How much history stays in memory, pivot and ATR settings |
| `Heatmap` | The liquidation estimate: bucket width, leverage mix, decay half-life, query range |
| `OrderBook` | Shortlist size and rules, imbalance window, wall detection |
| `Rules` | All signal thresholds, vetoes, cooldown, tiers, flood guard |
| `Outcomes` | Recovery timing |
| `Telegram` | Feed-down delay, `/top` size, `/stats` default days, mute limit |
| `Database` | Connection (password from secrets), liquidation retention and batching |

## Data and analysis

| Table | Contents |
|---|---|
| `alerts` | Every alert: symbol, direction, tier, price, breakout level, invalidation, target, each check as JSON, all metrics at alert time, `config_hash`, `delivery` (`sent` or `digest`) |
| `alert_outcomes` | One row per alert: `price_5m`, `price_15m`, `price_60m`, `mfe_pct`, `mae_pct`, `first_hit` (`target`, `invalidation`, `none`), `resolved_at` |
| `near_misses` | Checks passed and failed, metrics, `config_hash` |
| `liquidations` | Every forceOrder event (kept 30 days) |

Raw trades, candles and order books are never stored.

```powershell
docker exec -it scalpradar-postgres psql -U scalpradar -d scalpradar
```

```sql
-- Win rate by tier and threshold version
SELECT a.config_hash, a.tier, count(*) AS n,
       round(100.0 * avg((o.first_hit = 'target')::int), 1) AS target_first_pct,
       round(percentile_cont(0.5) WITHIN GROUP (ORDER BY o.mfe_pct)::numeric, 2) AS median_mfe
FROM alerts a JOIN alert_outcomes o ON o.alert_id = a.id
GROUP BY 1, 2 ORDER BY 1, 2;

-- Does the book confirmation (C3) help?
SELECT c->>'passed' AS c3_passed, count(*), round(100.0 * avg((o.first_hit = 'target')::int), 1) AS target_first_pct
FROM alerts a JOIN alert_outcomes o ON o.alert_id = a.id,
     jsonb_array_elements(a.confirmations) c
WHERE c->>'code' = 'C3'
GROUP BY 1;

-- Which checks block near misses most often
SELECT f->>'code' AS code, count(*) FROM near_misses, jsonb_array_elements(failed) f
WHERE created_at > now() - interval '7 days' GROUP BY 1 ORDER BY 2 DESC;
```

## Binance limits and where the docs differ from the spec

Checked against the Binance USDⓈ-M futures docs and the live API:

1. **WebSocket URLs have routed paths.** `aggTrade`, `markPrice` and `forceOrder` are under `/market`,
   and depth is under `/public`. A connection without the path silently gets only `/public` data.
   Binance.Net 13.8.0 adds the paths itself.
2. **REST weight cap is 2400/min**. It is read from `exchangeInfo.rateLimits` at startup, with 2400 as
   the fallback.
3. **`openInterestHist`** has weight 0 but its own limit (1000 requests per 5 min). ScalpRadar keeps it
   under 500 per 5 min. 288 points (24 h of 5m) fit in one call.
4. **`fundingInfo`** shares a separate limit of 500 requests per 5 min with `fundingRate`. It is called
   hourly. Symbols not listed there use the default 8 h interval.
5. **Kline weight depends on `limit`**: under 100 → 1, under 500 → 2, up to 1000 → 5, above → 10.
6. **A connection may send at most 10 messages per second.** Stream subscribes are paced at 4/s.
7. **Matches the spec:**
   - `aggTrade.m` means the buyer is the maker (a sell);
   - up to 1024 streams per connection;
   - connections are closed every 24 h, and ScalpRadar reconnects and backfills the gap;
   - `!markPrice@arr` updates every 3 s.
8. **`!forceOrder@arr` sends at most one liquidation per symbol per second.** The `liquidations` table
   is a sample, not every liquidation. The heatmap is an estimate from OI changes and the leverage mix.
9. **The universe is often smaller than 100.** At the time of writing, 522 USDT perpetuals were trading,
   but only about 86 had $20M+ in 24h volume. Lower `Universe:MinQuoteVolumeUsd` to track more.
10. **Full order books** use a 1000-level REST snapshot (weight 20) plus the diff stream. Partial-depth
    streams are not used, because they don't give a consistent book.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Startup fails: database | `docker compose ps`. Check that the password in secrets matches `.env`. Port 5433 must be free |
| "Telegram is enabled but … missing" | Set `Telegram:BotToken` and `Telegram:ChatId` (step 5), or run with `--Telegram:Enabled=false` |
| No reply to commands | Only the configured chat id is answered (the log says "Ignored command from unknown chat …"). Another process polling the same bot (or a webhook) also blocks it |
| "Stream … lost its connection" | Normal now and then. Binance.Net reconnects in a few seconds and the gap is backfilled. Frequent drops point to the network, a VPN or a proxy |
| `REST weight … PAUSED` | Binance returned 429 or 418. REST calls wait for the ban to end; streams keep running |
| Service won't start | `C:\ScalpRadar\logs`, and Event Viewer → Windows Logs → Application |
