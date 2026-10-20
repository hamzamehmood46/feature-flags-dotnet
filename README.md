# Flagpole: feature flags with targeting and safe rollouts

[![Test and deploy](https://github.com/hamzamehmood46/feature-flags-dotnet/actions/workflows/pages.yml/badge.svg)](https://github.com/hamzamehmood46/feature-flags-dotnet/actions/workflows/pages.yml)

**Live demo: [hamzamehmood46.github.io/feature-flags-dotnet](https://hamzamehmood46.github.io/feature-flags-dotnet/)**

A feature-flag engine in C# for shipping code dark, rolling it out gradually, targeting specific users, and switching it off in seconds. It is built around the questions a team asks when a flag misbehaves: *Why did this user get that? Did turning the rollout up take the feature away from anyone? What happens to the app if the flag service goes down?* Blazor WebAssembly runs the same engine in your browser.

## Try it (3 minutes)

1. **Playground:** change the user and read the **Why** column. Set the user id to `staff-ava` and *new-checkout* turns on through a rule. Set seats to 2 and it turns off through a different rule.
2. **Rollout:** drag the slider from 20% to 60%. Users are only ever **added**; nobody who had the feature loses it. Drag back down and users are only removed.
3. **Flags:** press **Kill switch** on *payments-v2*. *apple-pay* depends on it, so it goes off too, with the reason shown.
4. **Client:** take the flag service **down**, change a flag, and see the app keep serving the last known values. Bring it back and refresh.
5. **Safety:** try saving a rollout that adds up to 90%, or a prerequisite loop. Both are refused before they can ship.

## What it does

| Capability | Behavior |
|---|---|
| **Targeting rules** | Ordered rules, each with conditions that must all match (`equals`, `in`, `starts with`, `contains`, numeric comparisons, segment membership). The first match wins. A user who lacks an attribute never matches a condition on it, so a missing value cannot accidentally let someone in. |
| **Segments** | Reusable groups: named users, plus anyone matching rules, minus an exclusion list (exclusions win). |
| **Percentage rollouts** | Each user is hashed with the flag name into one of 10,000 buckets. The same user always gets the same answer, raising a rollout only ever adds users, and two flags are bucketed independently (two 50% rollouts overlap by about 25%, not 50%). Can bucket by any attribute, for example by company so everyone in one company sees the same thing. |
| **Variants** | Not just on/off: string or number values, with weighted multi-way splits for experiments. |
| **Prerequisites** | A flag can depend on another flag being a given variant. Loops are refused, and a flag others depend on cannot be deleted. |
| **Schedules** | Switch on and off automatically at set times. |
| **Environments** | Independent configuration per environment (dev, prod). |
| **Explainable results** | Every evaluation returns the variant, the value, the reason (off, scheduled, prerequisite failed, rule match, rollout, default) and a sentence saying why, including the bucket. |
| **Safe changes** | Every save is validated as a whole (weights add up to 100%, variants exist, rules are well formed, no prerequisite loops) and refused entirely if anything is wrong. Valid changes are published as a new immutable snapshot with a version, and written to an audit log. |
| **Client SDK** | Caches the last flags it received, refreshes on a poll interval, never throws, serves the caller's defaults before the first fetch, and keeps serving the last known values during an outage while reporting that it is stale. An older snapshot never replaces a newer one. |
| **Flags as code** | Export and import as JSON. An import is validated as a single unit. |

## How it is built

```
src/Flagpole.Engine   C#: evaluator, bucketing, store with validation and audit, client, JSON. No UI.
src/Flagpole.Web      Blazor WebAssembly front end (runs the same engine in the browser)
tests/                xUnit tests
```

## Tests

```bash
dotnet test
```

125 tests, including:

- every operator, missing attributes, numeric versus text comparison, and segment inclusion and exclusion;
- **statistical checks on 40,000 users**: rollouts land within one point of the requested share at 1, 10, 25, 50, 75, 90 and 99 percent; a 20/30/50 split follows its weights; two flags overlap by about a quarter; bucketing by company keeps companies together;
- **monotonic rollouts**: raising 5 to 100 percent step by step never removes anyone, and lowering only removes;
- a pinned hash value, so bucketing can never change by accident and reshuffle production users;
- a user exactly on a rollout boundary lands in the right share;
- validation of every kind of bad flag, all errors reported at once, and a refused change leaving the store exactly as it was;
- prerequisites, loops through several flags, schedules to the second, and deletion protection;
- the client during an outage, staleness, poll timing, and never throwing;
- concurrency: readers never see two flags that were changed together disagree, and parallel writers never lose an update or skip a version;
- a randomized test: hundreds of random valid flags evaluated for hundreds of users must always give an existing variant with the matching value.

I also broke the rules on purpose to check the tests notice: ignoring the kill switch, bucketing without the flag name, an off-by-one at a rollout edge, a rule needing only one condition, ignoring prerequisites, a schedule off by one, missing attributes matching "not equals", ignoring segment exclusions, not checking rollout weights, allowing prerequisite loops, saving without validation, a client that drops its flags during an outage, and an older snapshot overwriting a newer one. Each made tests fail.

## Run it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Flagpole.Web
```

## Scope, honestly

- Everything lives in one browser tab and resets on reload. The "service" and the "client" are two objects in the same page, with an outage you can switch on.
- There is no network API, authentication or persistence. A real deployment would put the store behind an API, persist it, and push changes to clients (for example with server-sent events) on top of the polling shown here.
- Rollouts are percentage-based on a hash, not an experimentation platform: there is no event tracking or statistics.

## Related projects

- [Multi-tenant SaaS API](https://github.com/hamzamehmood46/tenant-saas-api-dotnet)
- [Job Scheduler](https://github.com/hamzamehmood46/job-scheduler-dotnet)
- [API Rate Limiter](https://github.com/hamzamehmood46/api-rate-limiter-dotnet)

## License

MIT
