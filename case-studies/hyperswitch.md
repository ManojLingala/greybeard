# Hyperswitch — greybeard field findings

[juspay/hyperswitch](https://github.com/juspay/hyperswitch) · Rust payments switch ·
analyzed at commit `a828fb2` (2026-06-19).

**Context first:** Hyperswitch is disciplined about money — it wraps amounts in a
`MinorUnit` newtype used across 300+ files, and most `f64` occurrences are deliberate
connector-boundary conversions (some processor APIs demand float). The findings below
are the genuine residue after filtering those out. Public, non-security only;
security-sensitive candidates are handled privately.

Legend: **confirmed** = read and reproduced/clear · **smell** = real but mild or
partly compensated.

---

## Rung 1 — money (never float)

### 1.1 `f64` surcharge math can be off by a minor unit — **confirmed**
`crates/common_utils/src/types.rs:92–112` (`Percentage::apply_and_ceil_result`)

```rust
let percentage_f64 = f64::from(self.percentage);
let result = (amount as f64 * (percentage_f64 / 100.0)).ceil() as i64;
```

The overflow guard at L96–98 keeps `amount` exactly representable, but
`percentage_f64 / 100.0` is **not** exact in binary float, so the product can sit just
above/below an integer and `.ceil()` rounds the wrong way.

Reproduced (exact vs float path):

| amount (minor) | surcharge | float path | exact | off by |
|---:|---:|---:|---:|---:|
| 10,000,000 | 2.22% | 222,001 | 222,000 | +1 |
| 100,000,000 | 2.22% | 2,220,001 | 2,220,000 | +1 |
| 100,000,000 | 33.33% | 33,330,002 | 33,330,000 | +2 |

**Minimal fix** (uses `rust_decimal`, already a dependency and used elsewhere in this
file):
```rust
let pct = Decimal::from_f64(f64::from(self.percentage)).unwrap_or(Decimal::ZERO);
let amt = Decimal::from_i64(amount).ok_or(/* … */)?;
let result = (amt * pct / Decimal::from(100)).ceil().to_i64().ok_or(/* … */)?;
```
→ greybeard rung 1; the [Refund example](../examples/01-refund-money.md) bug class.

### 1.2 EMI interest: float division before `Decimal` conversion — **smell**
`crates/common_types/src/payments.rs:1353`

```rust
let rate_decimal = Decimal::from_f64(self.0 / PERCENTAGE_BASE)   // f64 divide, THEN Decimal
```

The rest of the EMI formula is computed in `Decimal` (good), and the final `.ceil()`
biases toward the merchant, so impact is small — but the one float division bakes a
rounding error into the rate before the decimal math begins. Convert first, divide in
decimal: `Decimal::from_f64(self.0)? / Decimal::from(100)`.

---

## Rung 3 — external calls (timeout always)

A `reqwest` client has **no request timeout unless one is set**, and `.pool_idle_timeout`
is *not* a request timeout (it controls idle pooled connections). Each call below can
hang on a slow/unreachable dependency.

### 3.1 Key-manager (encryption service) — **confirmed**
`crates/common_utils/src/keymanager.rs` — the client sets only `.pool_idle_timeout(...)`;
the `.request(method, url)…​.send()` has no `.timeout()`. This is on the payment hot
path (encrypt/decrypt PII), so a stalled key-manager can hang request workers.

### 3.2 ClickHouse analytics — **confirmed**
`crates/analytics/src/clickhouse.rs:68` — `reqwest::Client::new()` (bare, no default
timeout) and `.send()` with no timeout, on high-volume analytics queries.

### 3.3 Injector (vault/tokenization proxy) — **confirmed**
`crates/injector/src/injector.rs:323` — `req_builder.send()` with no `.timeout()`; can
hang on a slow vault connection.

**Minimal fix:** set a per-request `.timeout(Duration::from_secs(N))` (or a default
timeout on the client builder), matching the bounded pattern used by Hyperswitch's main
connector HTTP client. → greybeard rung 3; the
[external-timeout example](../examples/03-external-timeout.md).

---

## Rung 5 — reads / N+1

### 5.1 Per-item DB lookup in a loop — **confirmed**
`crates/router/src/core/routing.rs:2261`

```rust
for info in info_vec {
    let mca = db.find_by_merchant_connector_account_merchant_id_merchant_connector_id(
        processor.get_account().get_id(), &info.mca_id, processor.get_key_store(),
    ).await?;
    // …
}
```
One query per `label_info` entry — classic N+1 on a routing-config update. Batch the
ids into a single `find … WHERE mca_id IN (…)`.

### 5.2 / 5.3 Migration & payment-method loops — **smell**
`crates/router/src/core/payment_methods/migration.rs:132` and
`crates/router/src/routes/payment_methods.rs:456` show the same query-in-loop shape on
colder paths (migration; a cache-backed lookup). Lower traffic, same fix.

→ greybeard rung 5; the [pagination/N+1 example](../examples/05-pagination-nplus1.md).

---

## Not shown here

Additional candidates flagged **security-sensitive** (webhook idempotency on
money-moving paths, outbound-webhook SSRF, raw payload/response logging) were found and
routed to **private disclosure** — they are deliberately omitted from this public file.
