# Bank reconciliation design

## Statement import

A statement batch is scoped to one registered bank account. Preview parses and validates the submitted CSV as a transient draft and returns row errors and warnings. Import repeats those checks under the company lock and saves an immutable statement and its lines atomically. Each accepted statement has a period, opening and closing balance, bank statement identifier, content fingerprint, request key, row count, actor and timestamp. Lines retain booking and value dates, signed amount, reference, description, optional stable bank ID and the original field values.

Default columns are `booking_date,value_date,amount,reference,description`. Dates use `yyyy-MM-dd` and a blank value date is allowed. The decimal separator is a period. Positive signed amounts are credits into the bank account and negatives are debits. Quote CSV fields containing delimiters or newlines. The optional `columns` mapping supports other header names, a stable ID column, and a direction column. With a direction column, use positive amounts and `CREDIT` or `DEBIT`. Delimiters can be comma, semicolon or tab. Requests support up to 1,000,000 characters and 5,000 data rows.

Import requires the closing balance to equal opening plus signed movements. Booking dates must fall in the statement range. Subsequent statements must continue from the previous end date and closing balance. An identical retry with the same import key returns the existing batch. A conflicting retry, repeated statement or stable ID, overlapping date range, bad precision or broken continuity fails validation. Identical rows without a stable bank ID and differing value dates are flagged for review. Bank-issued correcting entries are retained as subsequent evidence. The workflow supports immutable imports and does not provide file replacement or an automatic gap fill.

## Matching and finalization

Start a session for the next statement in order. A bank account has at most one open session. Matching identifies posted GL lines on the dedicated bank account by line ID. It reserves a whole statement line and one to fifty whole same-direction book lines whose signed sum equals it exactly. The reservation indexes prevent reuse. Matching and release affect reconciliation evidence only. The existing payment, transfer or journal remains the accounting source.

Suggestions are read-only candidates. They require an exact signed amount, matching normalized reference, a booking-date difference of at most three days and a unique result on both sides. A user confirms any suggestion or selects the lines manually and provides a reason. The match records actor, time, reason, statement line and GL line IDs. Concurrent edits use a company lock and a session version. An Owner can release a confirmed match while matching is open, retaining the group, released items, reason and actor.

The lifecycle is Matching → Review → Finalized. Review can return to Matching. Only an Owner can finalize. The finalization requires all statement lines matched and zero unexplained difference at the inclusive statement end date:

    adjusted bank = statement closing + deposits in transit − outstanding withdrawals
    difference = adjusted bank − posted book closing

The first statement anchors the pre-start posted GL balance to its opening balance. Start earlier when existing uncleared movements need individual matching. Subsequent sessions carry forward earlier outstanding book items, and reserve only matches whose statement end is at or before the report cutoff. The inquiry exposes unmatched statement and book lines, age buckets and adjusted balances. A finalized session records the book and timing amounts. The posting policy and database contract prevent a new GL posting from changing a finalized bank cutoff. An Owner can reopen the latest session with a reason. The audit retains the prior finalization totals and any released match evidence.

## Adjustments and recording errors

Matching an existing receipt adds no journal. An Owner can post a fee or interest adjustment for an unmatched line in a Matching session. The amount and date come from the statement line. A fee debits expense and credits bank. Interest debits bank and credits revenue. If a book movement with the same reference and amount exists near the booking date, investigate or match it first. Adjustment posting uses the shared Posting Engine, requires an open period and requires explicit backdate approval for earlier dates. After posting, confirm the match to the new line.

Correct a treasury accounting error with an exact linked reversal and a new authorized transaction when needed. Release an active match before reversing within an open reconciliation. A later reversal of an already finalized bank item stays in a later cutoff and preserves the earlier match. The original GL evidence is retained.

## HTTP endpoints

Relative to `/api/v1/organizations/{orgId}/companies/{companyId}/banking/accounts/{bankId}`:

| Path | Methods |
| --- | --- |
| `/statements/preview`, `/statements` | POST preview or import |
| `/statements`, `/statements/{id}/lines` | GET history and original statement lines |
| `/reconciliations/statements/{statementId}` | POST start |
| `/reconciliations`, `/reconciliations/{id}` | GET history and cutoff progress |
| `/reconciliations/{id}/matches` | GET match history, POST confirm |
| `/reconciliations/{id}/matches/{matchId}/release` | POST release |
| `/reconciliations/{id}/state/{review,resume,finalize,reopen}` | POST transition |
| `/reconciliations/{id}/adjustments` | POST linked fee or interest |
