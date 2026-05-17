# Discord as an agent control surface

## Goal

Survey the 2025–2026 Discord API and Discord.Net ecosystem to identify concrete upgrades
that make Discord a richer, more capable control surface for ember — and for any future
agent fleet built on the same pattern. Ground every recommendation in ember's actual
architecture (ADR 1, ADR 5, Discord.Net on .NET 9, single-guild, phone-primary operator).

---

## Executive summary

- **Components V2 is stable in Discord.Net 3.18+ (July 2025)** and gives ember an
  interactive gate UX — Approve / Extend / Reject buttons replace the passive emoji-reaction
  countdown with a zero-typing, phone-first control surface.
- **The `ButtonExecuted` event** is the clean seam: wire it alongside `SlashCommandExecuted`
  in `DiscordBotService`, keep handlers off the gateway thread, and button-driven ops
  require zero new infrastructure.
- **Embed live-editing** (`IUserMessage.ModifyAsync`) already exists in `ThreadGateway`;
  using it for a pinned status card that updates in-place is the highest-leverage
  observability improvement available with no API additions.
- **Discord.Net's gateway manager self-resumes**, but the bot has one significant gap:
  `session_id` and `resume_gateway_url` are not persisted across restarts. A cold
  boot always re-identifies, missing any events that fired during the outage window.
- **Forum channels with tags** are a strong upgrade path for a multi-agent future:
  one forum channel per fleet, one thread per task, tags encoding `state:planning /
  state:building / state:done / state:failed` — the channel becomes a visual kanban.
- **Discord comfortably wins vs Slack/Telegram as ember's control surface** for a
  solo operator: native interactive components, thread isolation, always-on mobile app,
  and zero additional infra. The main honest gap is no server-side scheduled delivery
  (cron pings require a host-side timer, which ember already has).

---

## Findings

### 1. Discord UI primitives (2025–2026 state)

#### Components V2 — now stable

Discord released Components V2 as the new message layout system in mid-2025.
Discord.Net 3.18.0 (July 2025) promoted it to stable after a multi-beta cycle.
The new component type registry (23 types as of the current API reference) includes:

| Type | Category | Works in messages | Works in modals |
|------|----------|:-----------------:|:---------------:|
| Button (2) | Interactive | Yes | No |
| String / User / Role / Channel Select (3,5,6,8) | Interactive | Yes | Yes |
| Text Input (4) | Interactive | No | Yes |
| Section (9) | Layout | Yes | No |
| Text Display (10) | Content | Yes | Yes |
| Thumbnail (11) | Content | Yes | No |
| Media Gallery (12) | Content | Yes | No |
| Separator (14) | Layout | Yes | No |
| Container (17) | Layout | Yes | No |
| Label (18) | Layout | No | Yes |
| File Upload (19) | Interactive | No | Yes |
| Radio Group (21) / Checkbox Group (22) / Checkbox (23) | Interactive | No | Yes |

To opt into the V2 layout system, the message must carry the
`MessageFlags.ComponentsV2` flag (bit 15). When that flag is set, `content` and
`embeds` fields are ignored; `TextDisplay` and `Container` components replace them.
**V2 and legacy embeds are mutually exclusive per message.**

Discord.Net 3.19.x (March 2025 / March 2026) added modal radio/checkbox groups and
checkboxes, and finalized the `ComponentBuilderV2` fluent API.

#### Buttons — the key primitive for ember

Buttons (`ButtonStyle.Danger`, `.Success`, `.Primary`, `.Secondary`) carry a
developer-defined `custom_id` (max 100 chars). The `ButtonExecuted` event on
`DiscordSocketClient` fires whenever the button is clicked. Handlers receive a
`SocketMessageComponent` and must respond within 3 seconds (use `DeferUpdateAsync()`
to buy time); the interaction token is then valid for a further 15 minutes for
follow-ups.

**Critical constraint for ember's gate:** buttons on a message live as long as the
message itself — they do not expire. The 15-minute window is for the *response
token*, not the button's clickability. This means an Approve button posted at gate
time remains clickable throughout the 5-minute (or longer) countdown without any
special renewal logic.

#### Modals

Modals (`ModalBuilder`) are validated text-input forms. They are triggered by a
button or slash command interaction and return `SocketModal` via the
`ModalSubmitted` event. In ember's context, a modal could capture freeform
feedback when the operator rejects a plan ("reject + reason"). Discord.Net has
supported modals since v3.x; the October 2025 newsletter notes they now also accept
all select-menu types plus Label and TextDisplay inside modals.

#### Ephemeral messages

`RespondAsync(..., ephemeral: true)` or `DeferAsync(ephemeral: true)` makes the
response visible only to the triggering user. Ember already uses this correctly for
slash-command acknowledgements. Ephemeral messages cannot carry persistent buttons
in a way that works across sessions — another reason the gate approval message should
be a regular thread message, not an ephemeral one.

#### Threads (2025–2026 state)

Threads remain first-class API citizens. Key points for ember:
- Auto-archive duration is configurable up to 1 week (ember sets 1 day, which is
  appropriate).
- Sending a message to an archived thread unarchives it automatically (important for
  gate re-arm on restart — ember's `ReconcileOnBootAsync` works correctly here).
- `THREAD_CREATE / UPDATE / DELETE` gateway events keep state in sync.
- Forum channels introduce thread tagging: `available_tags` on the channel,
  `applied_tags` on individual threads. Tags can be set at creation or via
  `ModifyAsync`.

#### Discord.Net version summary

| Version | Date | Key additions relevant to ember |
|---------|------|--------------------------------|
| 3.18.0 | Jul 2025 | Components V2 stable, `ComponentBuilderV2`, breaking changes (migration guide available) |
| 3.19.0-beta.1 | Jan 2026 | Modal refactoring, select menu `IsRequired`, `.NET 8+` only (legacy SDK dropped) |
| 3.19.1 | Mar 2026 | Modal radio/checkbox groups, gradient role colors |

Ember's `src/Ember/Ember.csproj` targets .NET 9, so all 3.19.x features are
available. The one flag to watch: the `ComponentsV2` message flag must be set
manually until Discord.Net auto-detects it for 5+ action rows.

---

### 2. Discord as an ops console — patterns in the wild

#### Approval gates with buttons

The most common pattern across agent-adjacent bots (CI notification bots, moderation
bots, AI workflow bots) is:

1. Post a message with an embed summarizing the pending action.
2. Attach Approve / Reject buttons (and optionally an Extend button for time-bounded
   gates).
3. Subscribe to `ButtonExecuted`; validate the clicker is the owner; transition state.
4. Edit the original message to remove the buttons and replace with a status string
   ("Approved by @operator at 14:32").

This pattern works fully within Discord.Net's existing event model and fits exactly
into ember's `DiscordBotService` alongside `SlashCommandExecuted`.

#### Live-updating status messages

A small set of agent-tracking bots post a single pinned message per session and call
`IUserMessage.ModifyAsync` periodically to update it. `ThreadGateway.CreateMessageAsync`
already returns an `IUserMessage` handle for exactly this purpose. The missing step is
persisting the message ID across restarts (store it in the `Session` row in SQLite) and
using `ModifyAsync` with rich embed fields instead of `PostAsync` for every state change.
Rate limit: Discord allows editing a message arbitrarily, but the global 50 req/s limit
and thread-specific per-user rate limits apply. For a single-operator bot polling every
20 seconds (ember's `GateService` interval), this is nowhere near a problem.

#### Fan-out: one thread per agent

The thread-per-session model (ADR 1) naturally extends to multi-agent fleets. The
pattern is: one parent channel (or forum channel) as the fleet dashboard, one thread per
running task. The channel's thread list is itself a visual task list. Tagging threads
(Planning / Building / Done / Failed) turns it into a filterable kanban visible on
mobile with zero additional tooling.

#### Mobile-first ops

Discord's mobile app renders embeds, buttons, and thread previews well. Buttons are
large touch targets; ephemeral responses don't clutter the thread. The key mobile UX
failures to avoid: don't require typing long custom IDs or slash-command arguments
(pre-fill or use select menus), and don't send walls of unformatted text (use embed
fields with short values).

---

### 3. Gateway resilience

#### What Discord.Net handles automatically

Discord.Net's `ConnectionManager` (internal) is designed to be "bulletproof": on any
disconnect that is not a fatal auth failure (4004) or shard misconfiguration (4010), it
attempts to reconnect and resume automatically. If resume is rejected (Invalid Session,
4009), it falls back to a fresh Identify. This is appropriate for ember's single-guild,
single-shard deployment.

Ember already handles the most important special case correctly: a 401 Unauthorized
response triggers `_fatalAuthSignaled` and calls `_lifetime.StopApplication()` rather
than entering an infinite retry loop.

#### The gap: session state does not survive a cold restart

The `session_id` and `resume_gateway_url` emitted by Discord in the `Ready` event are
held in-process by Discord.Net but are not persisted to disk. When ember restarts, it
always re-identifies rather than resuming. Consequence: any gateway events that fired
between the crash and the reconnect are lost — no `ReactionAdded` or `ButtonExecuted`
callback will fire for those events.

For the abort reaction (current design), this is partially mitigated because the gate
poller (`GateService.ReconcileOnBootAsync`) re-arms expired gates rather than auto-
firing them. But it means: if the operator reacted 🛑 during a crash window, that
reaction is silently lost. The re-arm message will appear, and the operator must react
again.

**With button-based gates (recommended below), this gap is less critical**, because
button clicks generate interactions that Discord retries for 15 minutes, and ember's
SQLite-backed session state means the gate deadline is persistent regardless. The missing
click just means the gate expires naturally and re-arms on boot.

#### Preventing gateway deadlocks

Discord.Net explicitly warns: do not run long-running work on the gateway thread. Ember
correctly uses `PlanningLoopRunner` and `BuildQueue` as background services, and all
gateway handlers (`OnSlashCommandAsync`, `OnReactionAddedAsync`) do minimal work and
return quickly. This must be maintained as new handlers (e.g., `ButtonExecuted`) are
added.

**Pattern:** respond to the interaction immediately (within 3 seconds), then dispatch
actual work to a `Channel<T>` or `Task.Run`. Never `await` a slow operation inside a
gateway callback.

#### Rate limits for a single-guild bot

- Gateway send limit: 120 events / 60 seconds (2/sec average). Ember's posting cadence
  is well below this.
- REST global limit: 50 requests / second. Ember's current design (periodic polling,
  no fan-out) is well within bounds.
- Webhook limit: 30 requests / minute per webhook URL.
- Identify limit: 1000 per 24 hours (not a concern for a single shard).

The main rate-limit risk for ember at scale is `ModifyAsync` calls on the status
message if the polling loop becomes very tight. A 20-second minimum edit interval is
safe; a 1-second interval would not be.

#### Intents

Ember needs at minimum: `GatewayIntents.Guilds | GatewayIntents.GuildMessages |
GatewayIntents.GuildMessageReactions`. `MESSAGE_CONTENT` (privileged) is NOT needed if
ember reads operator messages via `GetMessagesAsync` rather than via the gateway
`MessageReceived` event. This avoids the privileged intent verification requirement.

---

### 4. Forward primitives

#### Scheduled triggers

Discord has no server-side scheduled message delivery. Scheduled pings to the operator
require a host-side timer — which ember already has (the `GateService` background
service). The pattern for "reminder after N hours" or "nightly digest" is simply a
`CancellationTokenSource` with a delay, posting via `ThreadGateway.PostAsync`. No
external cron tooling is needed.

#### Webhooks and embeds for rich status

Webhooks are an alternative to bot-sent messages: they POST directly to a channel with
no bot client needed, carry embed payloads, and do not require the bot to be connected.
For ember, webhooks are most useful for external event ingress (CI systems, GitHub
Actions posting build results into an ember thread). They're not a better choice than
the bot client for ember's own status posts, since the bot is always running.

Embed structure for status messages:

```
EmbedBuilder
  .WithTitle("ember · plan: {brief}")
  .WithColor(Color.Gold)           // Gold = at gate; Green = building; Red = failed
  .AddField("State", session.State, inline: true)
  .AddField("Round", session.CurrentRound, inline: true)
  .AddField("Gate expires", "<t:1234567890:R>", inline: true)  // Discord relative timestamp
  .WithFooter("Gate reason: {gate_reason}")
```

Discord's `<t:unix:R>` timestamp syntax renders as a live relative time ("in 3
minutes") in every client without any polling — a free countdown display.

#### Richer gate UX with buttons

The current gate UX is a passive countdown — the operator must remember to react with
🛑 or run `/abort`. A button-based gate message is strictly better:

```
**Plan ready — gate open**
[plan summary embed]

[Approve ✅] [Extend ⏱ +15 min] [Reject ❌]
```

- **Approve**: immediately transitions state to Building, removes buttons, posts
  "Approved by @operator."
- **Extend**: resets `GateExpiresAt` by a configured increment; updates the embed's
  timestamp field.
- **Reject** (with optional modal): sets `Aborted`; optionally opens a modal for
  rejection reason to log in the session row.

All three are `ButtonExecuted` handlers keyed on `custom_id` strings like
`gate:approve:{threadId}`, `gate:extend:{threadId}`, `gate:reject:{threadId}`. The
`threadId` suffix makes each button unambiguous across concurrent sessions.

#### Multi-operator / permissions

ADR 1 is a single-operator design. Discord does support role-based access control at
the channel, thread, and command-permission level. If ember ever needs a second operator
(e.g., a trusted human reviewer for the plan before build), Discord's built-in command
permission system (`DefaultMemberPermissions`, per-guild command permission overrides)
can gate commands to specific roles without any code changes.

---

### 5. Alternatives sanity-check (ADR 1 stays honest)

| Criterion | Discord | Slack | Telegram |
|-----------|---------|-------|----------|
| Native interactive components (buttons, selects) | Yes (V2, stable) | Yes (Block Kit) | Inline keyboards (limited) |
| Thread-per-task isolation | Yes (first class) | Yes (channels/threads) | Groups only, no true threads |
| Mobile app quality | Excellent | Good | Excellent |
| Bot hosting model | Long-running WebSocket (Gateway) | Long-running WebSocket or Events API | Long-polling or webhook |
| Setup complexity | Medium (Developer Portal, intents) | High (OAuth2, app approval, workspace) | Low (BotFather, 5 minutes) |
| Free tier limits | Generous | Limited history | Unlimited |
| serverless-compatible | No (WebSocket required) | Yes (Events API) | Yes |
| Self-hosted server | No | No | Yes (Telegram native or Matrix bridge) |
| Developer ecosystem (C#/.NET) | Discord.Net (mature) | Slack.NET (thin) | Telegram.Bot (good) |

**Where Discord wins for ember:**
- Thread-per-session as a native primitive (ADR 1's core bet) — Telegram has no
  equivalent, Slack threads are second-class.
- Interactive buttons without any webhook server overhead — Telegram's inline
  keyboards are narrower.
- The operator already lives in Discord and built their first bot there (zero
  cognitive switching cost).
- Discord.Net on .NET 9 is the most mature C# Discord library and is already in use.

**Where Discord genuinely loses:**
- No server-side scheduled delivery (requires host timer — which ember has anyway).
- Gateway WebSocket is stateful; serverless deployment is not viable (not a constraint
  for ember's always-on model).
- `MESSAGE_CONTENT` privileged intent required if you want to read message text via
  gateway events (not needed if using REST fetch, which ember does in
  `CollectOperatorMessagesAsync`).
- 2000-char message limit vs Slack's 40,000 — mitigated by ember's existing chunking
  in `ThreadGateway.Chunk`.

**ADR 1 remains a sound choice.** The thread-per-session architecture would be painful
to replicate in Telegram, and Slack's enterprise slant adds friction for a solo-operator
personal tool.

---

## Surprising / novel

1. **Discord `<t:unix:R>` timestamps are a free live countdown.** Storing
   `GateExpiresAt` as a Unix timestamp and emitting `<t:{expiry}:R>` in the gate embed
   gives every Discord client a live relative display ("in 4 minutes") with zero polling
   or server-side logic. This is strictly better than posting a static "5 minutes from
   now" string.

2. **Buttons don't expire; only response tokens do.** The commonly cited "15-minute
   limit" applies to following up on an interaction token, not to the button remaining
   clickable. A gate button posted at the start of the countdown is still fully
   interactive after 4 minutes 59 seconds. This eliminates a class of complexity (re-
   posting buttons) that would otherwise arise for gates longer than 15 minutes.

3. **Discord.Net dropped all pre-.NET 8 targets in 3.19.0-beta.1 (January 2026).**
   Ember targets .NET 9, so this has no impact, but it is a clean-break signal that the
   library is tracking modern runtime targets. It also means Discord.Net packages are
   now AOT-publish-compatible (important if ember ever moves to a single-binary deploy).

4. **Components V2 and legacy embeds are mutually exclusive per message.** You cannot
   mix `TextDisplay` components with `embeds` fields in the same message. The migration
   path is: new status messages use V2 layout; old embed messages continue as-is. No
   flag is needed unless you want to use Section/Container/MediaGallery.

5. **Forum channels turn a thread list into a visual kanban** with no additional tooling.
   Creating a single `#ember-tasks` forum channel with pre-defined tags (Planning,
   AtGate, Building, Done, Failed) gives the operator a mobile-friendly task dashboard
   that is maintained purely by the bot modifying `applied_tags` on each thread.

6. **Discord natively supports per-guild bot profiles (October 2025).** Bots can set
   unique display names and avatars per guild. For a future multi-tenant version of
   ember (one bot, multiple personal guilds), each instance could present a distinct
   identity with no separate bot application required.

---

## Where this uniquely aligns with ember

### ADR 1 (Discord as control surface)

ADR 1's thread-per-session model already exploits the right Discord primitive. The
recommended upgrades (button gate, live-editing status embed, forum-channel fleet
dashboard) are layered on top of the same model without structural change. The arch
stays consistent.

### ADR 5 (soft gate with resumable countdown)

The gate is ember's most operator-visible feature and currently the roughest UX: the
operator must remember to watch the thread and react with an emoji. Replacing this with
three labeled buttons (Approve / Extend / Reject) is a direct improvement to the ADR 5
intent. The `GateExpiresAt` field and `ReconcileOnBootAsync` restart-resilience both
survive the migration — they operate at the session layer, not the Discord-message layer.

The `<t:unix:R>` countdown timestamp embeds the gate deadline directly in the message
so the operator sees "gate closes in 3 minutes" on their phone without context-switching
to check logs or run `/status`.

### Discord.Net maturity and velocity

Discord.Net 3.18–3.19 landed stable Components V2 support and dropped legacy SDK
targets in the 9–10 months since ember's ADRs were written. The library is actively
maintained and tracking Discord API v10 closely. The operator can adopt buttons and
Component V2 layout today without version risk.

### Phone-first operator

The operator frequently uses Discord from their phone. Buttons are large touch targets;
the approval flow becomes: open Discord notification → tap Approve. No typing required.
The `<t:unix:R>` timestamp renders correctly in the mobile app. The forum-channel
kanban view is equally readable on mobile. All recommended upgrades score well on
the phone-first constraint.

### Systems-engineer operator who pre-empts failure modes

The session-state gap (missed gate reaction during a crash window) is a real failure
mode that the button architecture partially closes: button clicks generate interactions
that Discord queues for 15 minutes, and the SQLite gate deadline is restart-resilient
regardless. The re-arm logic in `ReconcileOnBootAsync` remains valid as a backstop.

The one new failure mode introduced by buttons: if the gate message is deleted (e.g.,
thread pruned), the buttons are gone. Mitigation: keep `/abort` as a slash-command
fallback (it already exists) and never auto-archive the gate thread while the session
is active.

---

## Recommendations

Ordered by impact / effort ratio. Each item is independent and can be shipped
incrementally.

### Rec 1 — Replace reaction gate with button gate (HIGH impact, LOW effort)

**What:** In `GateService.FireGateAsync`, post the gate message via a new
`ThreadGateway.PostGateMessageAsync(session, gateMessage)` that includes a
`ComponentBuilder` with three buttons:
- `gate:approve:{threadId}` — green Success button, label "Approve ✅"
- `gate:extend:{threadId}` — grey Secondary button, label "Extend +15 min ⏱"
- `gate:reject:{threadId}` — red Danger button, label "Reject ❌"

In `DiscordBotService`, subscribe to `_client.ButtonExecuted += OnButtonExecutedAsync`.
The handler validates `reaction.User.Id == _ownerId`, parses the `custom_id`, and
dispatches to a `GateButtonHandler` service (by analogy with `ISlashCommand`).

**Keep `/abort` as a fallback** — it handles orphaned sessions even if the gate message
is gone.

**Stop using `ReactionAdded`** for gate control once button handling is wired — reactions
are less reliable (require `MESSAGE_REACTIONS` intent, harder to validate, no structured
data).

**Code seam:** `DiscordBotService._client.ButtonExecuted`, `GateService.FireGateAsync`,
new `GateButtonHandler` class.

### Rec 2 — Embed a `<t:unix:R>` countdown in the gate message (HIGH impact, ZERO effort)

**What:** In the gate embed or text, include the Discord timestamp:
```
Gate closes <t:{session.GateExpiresAt / 1000}:R>
```
(`GateExpiresAt` is in milliseconds; Discord timestamps are in seconds.)

**Why:** The operator sees a live countdown on any client — phone, desktop, browser —
with zero polling. This is the single highest-leverage Discord feature that ember is not
using.

### Rec 3 — Persist and live-edit the status embed (MEDIUM impact, LOW effort)

**What:** Add a `StatusMessageId` (ulong?) column to the `Session` SQLite table. When
a session's state changes (Planning → AwaitingGate → Building → etc.), call
`IUserMessage.ModifyAsync` on the stored message to update the embed color and fields
in-place, rather than posting a new message. On restart, look up the message by ID and
re-attach.

The embed color encodes state: Blue = Planning, Gold = AwaitingGate, Orange =
Building, Green = Done, Red = Failed.

**Rate-limit guard:** only edit if at least 10 seconds have passed since the last edit
(store `LastStatusEditAt` in memory, not SQLite).

**Code seam:** `ThreadGateway.UpdateStatusMessageAsync(IUserMessage, Session)`,
`Session.StatusMessageId`.

### Rec 4 — Migrate to a Forum channel for ember sessions (MEDIUM impact, MEDIUM effort)

**What:** Create a `#ember-tasks` forum channel in the personal Discord server with
pre-defined tags: Planning (grey), AtGate (yellow), Building (orange), Done (green),
Failed (red). Modify `ThreadHelpers.CreateSessionThreadAsync` to create a forum post
with `applied_tags: ["Planning"]` instead of a public thread in a text channel. Update
`applied_tags` on state transitions.

**Why:** The thread list becomes a visual task history the operator can browse on mobile.
Filtering by tag shows all sessions in a given state across all time. Forum posts also
support a pinned first message (the plan snapshot) that does not scroll away.

**Caveat:** Forum threads require `ForumChannel` not `ITextChannel`; `ThreadHelpers`
must detect the channel type. This is a one-time migration that changes the setup
instructions.

### Rec 5 — Add a Reject-with-reason modal (LOW impact, LOW effort)

**What:** When the operator clicks Reject on the gate message, respond to the button
interaction by opening a `ModalBuilder` with a single multi-line `TextInputBuilder`
("Reason for rejection"). On `ModalSubmitted`, store the reason in
`session.LastError`, post it to the thread, and set `State = Aborted`.

**Why:** The rejection reason becomes part of the session record (visible in
`/status`, logged, queryable). Useful when reviewing why a plan was rejected later.

### What NOT to do

- **Do not use `MESSAGE_CONTENT` privileged intent.** `CollectOperatorMessagesAsync`
  correctly uses REST (`GetMessagesAsync`) rather than the gateway event. Do not
  "simplify" it by adding the `MessageContent` intent — that adds a verification
  burden if the bot is ever added to a second guild (100-server threshold).

- **Do not run build logic inside `ButtonExecuted` handlers.** The gateway callback
  must return in < 3 seconds. Dispatch to the existing `BuildQueue` / `Channel<T>`
  pattern; the callback only updates session state and ACKs the interaction.

- **Do not post a new thread message for every state transition.** Use embed edits for
  minor updates; post a new message only for major state changes (gate opened, build
  started, build finished). Message spam makes the thread unreadable on mobile.

- **Do not switch to Components V2 layout for the status embed yet.** The `embeds`-based
  status card in `StatusCommand` is clear and well-structured. The V2 migration
  (`ComponentsV2` flag, `TextDisplay` instead of `content`) adds complexity with no UX
  gain for a single-field status view. Revisit when richer layout (e.g., multi-agent
  dashboard) justifies the change.

- **Do not add sharding.** Ember is a single-guild personal bot and will never approach
  the 2500-guild sharding threshold. One shard, one process, one SQLite file — keep it
  simple.

- **Do not switch to Telegram or Slack.** ADR 1 is sound. The thread-per-session model,
  native button components, .NET ecosystem fit, and the operator's existing comfort with
  Discord are decisive advantages. Neither alternative offers a comparable thread
  primitive.

---

## References

1. **Discord Component Reference (official, current)**
   https://docs.discord.com/developers/components/reference
   Full type registry (23 component types), flag requirements, interactive vs layout
   distinction. The authoritative source on `IS_COMPONENTS_V2`.

2. **Discord Gateway documentation (official, current)**
   https://docs.discord.com/developers/events/gateway
   Resume vs re-identify protocol, heartbeat mechanics, session_id / resume_gateway_url,
   rate limits (120 events/60s), close codes and their resumability.

3. **Discord.Net GitHub Releases**
   https://github.com/discord-net/Discord.Net/releases
   Release notes for 3.18.0 (Jul 2025, CV2 stable), 3.19.0-beta.1 (Jan 2026, .NET 8+
   only, modal refactor), 3.19.1 (Mar 2026, modal checkbox/radio). Primary source for
   library version timelines.

4. **Discord.Net — Getting Started with Components**
   https://docs.discordnet.dev/guides/int_basics/message-components/intro.html
   `ComponentBuilder`, `ButtonBuilder`, button custom IDs. Official Discord.Net docs.

5. **Discord.Net — Components V2 Advanced Guide**
   https://docs.discordnet.dev/guides/components_v2/advanced.html
   `ComponentBuilderV2`, `MessageFlags.ComponentsV2` requirement, breaking change notes,
   production-ready status.

6. **Discord.Net — Responding to Buttons**
   https://github.com/discord-net/Discord.Net/blob/dev/docs/guides/int_basics/message-components/responding-to-buttons.md
   `client.ButtonExecuted`, `SocketMessageComponent`, `component.Data.CustomId` switch
   pattern. The exact handler seam ember should wire.

7. **Discord.Net — Connection Management**
   https://docs.discordnet.dev/guides/concepts/connections.html
   "Avoid running long-running code on the gateway." Deadlock risk, connection manager
   auto-resume behavior, "designed to be bulletproof" claim.

8. **Discord Developer Newsletter — October 2025**
   https://discord.com/developer-newsletter/october-2025
   Unique server profiles for bots, modal select-menu expansion, gateway member-fetch
   rate limit tightening. Confirms select menus and Label/TextDisplay in modals.

9. **Discord 2025 API Year-in-Review**
   https://discord-media.com/en/news/development-2025-the-complete-year-in-review-api-migration-guide.html
   Token deprecation (Nov 2025), `POST /guilds` removal (Jul 2025), `PIN_MESSAGES` as
   standalone permission (Aug 2025), Mobile Activity Safe Area enforcement. Breaking-
   change reference.

10. **Discord Threads — Official Documentation**
    https://docs.discord.com/developers/topics/threads
    Thread lifecycle, auto-archive, forum tags (`available_tags`, `applied_tags`),
    gateway events, rate limiting per thread. Covers forum channel thread creation.

11. **Discord API Changes 2026 — Space-Node**
    https://space-node.net/blog/discord-api-changes-whats-new-2026
    API v10 stability confirmation, MESSAGE_CONTENT privileged intent guidance, native
    polls, per-route rate limiting. Secondary source; useful 2026 summary.

12. **Telegram vs Slack vs Discord for AI agents — GetClaw**
    https://getclaw.sh/blog/telegram-slack-discord-ai-bot-comparison
    Comparative analysis of API architectures, message limits, rate limits, and
    interaction model trade-offs for AI agent use cases.

13. **Discord Interactions: Buttons, Selects, Modals 2025 — Friendify**
    https://friendify.net/blog/discord-interactions-buttons-selects-modals-2025.html
    Interaction token 15-minute window, 3-second response requirement, ephemeral
    message constraints. Secondary source confirming timing constraints.

14. **Components V2 — Discord4J docs**
    https://docs.discord4j.com/interactions/components-v2
    Java ecosystem perspective on CV2, useful for cross-library validation of feature
    scope and flag semantics.

15. **GitHub Issue — Reconnect() after InvalidSession**
    https://github.com/discord-net/Discord.Net/issues/938
    Known Discord.Net issue where resume after Invalid Session may not always recover;
    relevant context for the gateway resilience section.
