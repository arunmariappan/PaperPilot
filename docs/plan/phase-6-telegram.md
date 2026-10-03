# Phase 6: Telegram bot

**Goal:** the Telegram bot runs inside `PaperPilot.Api` as a hosted service (as it did inside the Python API lifespan) and answers questions through the shared `RagService` (C12).

**Size:** S · **Depends on:** phase 3

## Behaviour that must be preserved

From `services/telegram/bot.py` (copy all user-facing text verbatim):
- **Enabled** only when `Telegram:Enabled` is true **and** `Telegram:BotToken` is set. Otherwise log "Telegram bot not configured - skipping initialization" and don't register it. A failure to start is logged and never stops the API.
- **`/start`:** "Welcome to arXiv Paper Curator!\n\nAsk me questions about CS papers and I'll provide answers with sources.\n\nCommands:\n/help - Show this help\n/search <keywords> - Search papers"
- **`/help`:** "Send me any question about computer science research papers.\n\nExamples:\n- What are transformer architectures?\n- How does BERT work?\n- Explain attention mechanisms\n\nUse /search to find specific papers."
- **`/search <keywords>`:** no arguments → "Usage: /search <keywords>\nExample: /search neural networks". Otherwise send the typing action, run a hybrid search with size 10, de-duplicate by `arxiv_id`, keep the first 5, and reply `Found {n} papers:\n\n` + `{i}. {title}\n{abs url}\n\n` per paper, with link previews off. No hits → "No papers found. Try different keywords.", error → "Search failed: {e}".
- **Free text:** send the typing action, then ask with `top_k 3`, `use_hybrid true` and the default model (the answer cache applies). No chunks → "No relevant papers found. Try rephrasing your question." Otherwise reply `*Answer:*\n{answer}\n` + (if there are sources) `\n*Sources:*\n` + `{i}. https://arxiv.org/abs/{id}\n` for up to 5 sources. Send with legacy `Markdown` parse mode and **fall back to plain text** if Telegram rejects the Markdown. Errors → "Error: {e}".
- Updates are processed **one at a time** (python-telegram-bot's default). That's fine with a single local GPU.

## Tasks

- [x] Package `Telegram.Bot` (22.x). Use the long-polling receiver; no webhook, so no public URL is needed.
- [x] `TelegramBotService : BackgroundService` in `PaperPilot.Api/Telegram`. Before polling, delete any webhook (long polling fails while one is set). Keep pending updates; Python's `start_polling()` didn't drop them. Stop cleanly on shutdown.
- [x] `ITelegramSender`, a thin wrapper over the bot client (`SendTextAsync(chatId, text, markdown)`, `SendTypingAsync(chatId)`), so handlers can be unit-tested. Telegram.Bot's send methods are extension methods, which NSubstitute can't mock.
- [x] `TelegramUpdateHandler` routes `/start`, `/help`, `/search` and free text as above. Questions call `RagService.AskAsync` and searches call `PaperRetriever`/`OpenSearchClient`, so there's no duplicated pipeline (C12).
- [x] `TelegramMessageFormatter` builds the answer and search messages. **B19:** split anything over 4,096 characters at paragraph boundaries (then line, then hard cut) and send the parts in order. The Markdown → plain fallback applies to each part.
- [x] Outages (B6): `SearchUnavailableException` → "Search is temporarily unavailable. Please try again later." instead of a raw exception message.
- [x] Nice to have: keep the typing indicator alive while the LLM is generating (resend every 4 s), because answers can take longer than Telegram's 5-second typing window.
- [x] Optional (N5): `/agent <question>` runs `IAgenticRagService` and replies with the answer plus reasoning steps.

## As built

- **Telegram.Bot 22.10.3.** `TelegramBotService` calls `getMe` and `deleteWebhook` (keeping pending updates), then
  `ReceiveAsync` with `allowed_updates = ["message"]`. The receiver awaits each update before polling again, so messages
  are handled one at a time.
  - Anything that fails while starting, a bad token included, is logged as "Failed to start Telegram bot: …" and the API
    keeps running.
  - After a failed poll it waits 5 seconds. A 409 means another process is polling the same token, for example the
    Python stack's bot.
- **The bot token is in every Bot API URL** (`/bot{token}/{method}`). So:
  - The client gets its own `HttpClient`, not one from the factory, which logs request URLs and whose standard
    resilience handler would cut long polls off after 10 seconds.
  - `getUpdates` calls aren't traced, and other calls are traced with `url.full` showing `/bot{token}/`
    (`TelegramRegistration.HideBotToken`, tested).
- **Commands** are recognised the way python-telegram-bot did: a bot-command entity at offset 0, case-insensitive, and
  ignored when addressed to another bot (`/help@OtherBot`). Unknown commands get no reply. Arguments are the rest of
  the text split on whitespace.
- **Questions** call `RagService.AskAsync` with `top_k 3` and hybrid search, traced as `rag_request` for user
  `telegram:{chatId}`. `/search` uses `IPaperRetriever` (10 chunks) under a `telegram_search` span with the same user.
  Differences from Python that follow from this (C12):
  - sources are listed in retrieval order rather than set order;
  - `/search` falls back to BM25 when the query can't be embedded, where Python failed;
  - questions over 1,000 characters are answered, where Python's `AskRequest` validation turned them into an error;
  - abstract links never carry a version (B8).
- **`/agent <question>`** (N5) replies with the answer, sources and the reasoning steps. `/start` and `/help` keep
  Python's text, so they don't mention it.
- **Typing** is resent every 4 seconds while the answer is generated and stops before the reply is sent.
- **`Telegram:BaseUrl`** (new, optional) points the client at another Bot API server. Tests use it to run the whole
  polling loop against WireMock: `getMe`, `deleteWebhook`, one `/help` update, then `sendMessage`.

## Tests
- [x] Unit: formatter (answer with and without sources, sources capped at 5, abs URLs built with `ArxivId`; search de-duplication and top 5; splitting a 9,000-character answer gives 3 parts, each ≤4,096, split on paragraph breaks); handler routing with fake `ITelegramSender`, `RagService` and retriever (usage message, no-chunks message, Markdown failure → plain resend, search outage message).
- [x] No integration test against real Telegram. Check it manually. **As built:** a unit test runs the real polling loop against a fake Bot API (WireMock); see As built.

## Done when
- [ ] With the token set in AppHost secrets, `/start`, `/help`, `/search transformers` and a free-text question all work from your phone.
- [ ] A question whose answer exceeds 4,096 characters arrives in several messages.
- [x] With no token, the API starts normally and logs that the bot was skipped. *Checked 2026-10-03:* "Telegram bot not configured - skipping initialization" in the `api` log.
- [ ] Telegram questions show up as `rag_request` traces (user `telegram:{chatId}`) in the Aspire dashboard.
