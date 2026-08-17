# Container duplication

Two clients took items from the same container at roughly the same time, and both got the item.

## Why it happened

The old flow was:

```
client -> host: take item -> host applies -> host broadcasts
```

No version check. Two clients could operate on the same container state without knowing about each other, so the host applied both requests and the item duplicated.

## What we do now

Containers carry a revision number. The host accepts the first request that matches the current revision and rejects stale ones; the rejected client restores its local container from the last host state.

This does not solve the problem in general — it just makes one of the two players lose the item instead of both getting it. That is the intended trade-off: duplication was worse.
