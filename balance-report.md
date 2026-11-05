# Chaos Arena balance report

Measured on `feature/chaos-arena-balance-pass` by `run-pipeline.ps1`: one build, 100 seeds per lane,
8 lanes, 1,800 s match cap. The 5,600-seed run reuses the first 4,000 outcomes through
`-PriorOutcomesPath`, so no seed was measured twice.

| Field | Value |
|---|---|
| Matches (uniform vs uniform) | 5,600 (seeds 1-5600) |
| Matches (profile vs uniform) | 400 |
| Machine-readable result | `balance-report.json` |
| Raw outcomes (notes folder) | `chaos-arena-balance-report.random.jsonl`, `.bot.jsonl` |

## Gate results

| Gate | Status | Number |
|---|---|---|
| B1 hero equity | **Failed** | 4 heroes outside their 95% interval: iron_warlord 55.82% (n=541), pack_alpha 55.03% (576), lightbringer_monk 54.86% (556), tempest_chaser 43.41% (546). 5 more Inconclusive (stone_guardian 53.78, magma_smith 53.29, tidecaller 53.19, nether_assassin 46.72, razor_dervish 46.56) |
| B2 seat balance | **Failed** | Seat 1 won 46.96% of 5,600 decided matches (z = -4.55, p < 0.001) |
| B3 tile equity | `InsufficientSample` | 360 hero-tile pairs, median 31 picks, 14 Failed. The status stays `InsufficientSample` because not every one of the 360 pairs reaches the 30-pick minimum, so the gate as written cannot report a verdict |
| B4 skill guard | **Passed** | Highest hero win rate 55.82%, below the 60% ceiling |
| G1 bot beats uniform | `InsufficientSample` + red at 42.5% | Seat 1 won 170 of 400. The gate will not issue a verdict below 2,000 matches, but 42.5% is 7.5 points **below** random and reproduces both earlier runs (44.0%, 46.0%) |

## Hero equity: durability is the win driver

| Hero | Win rate | n | Effective HP | DPS |
|---|---:|---:|---:|---:|
| tempest_chaser | 43.41% | 546 | 856 | 68.6 |
| razor_dervish | 46.56% | 567 | 749 | 80.0 |
| nether_assassin | 46.72% | 548 | 972 | 98.9 |
| verdant_druid | 47.25% | 546 | 1,144 | 46.2 |
| frost_warden | 47.30% | 556 | 827 | 76.8 |
| arcane_scholar | 47.40% | 616 | 656 | 47.7 |
| storm_caller | 47.73% | 572 | 648 | 51.5 |
| shadow_weaver | 48.04% | 560 | 1,080 | 64.6 |
| coral_vanguard | 48.07% | 570 | 1,330 | 47.7 |
| flame_archon | 48.46% | 584 | 645 | 52.3 |
| rune_scribe | 50.00% | 510 | 759 | 70.3 |
| sun_priest | 50.18% | 566 | 1,025 | 43.0 |
| thornback_behemoth | 50.62% | 561 | 1,650 | 38.7 |
| glacier_sentinel | 52.25% | 555 | 1,523 | 36.1 |
| tidecaller | 53.19% | 564 | 780 | 65.0 |
| magma_smith | 53.29% | 563 | 1,372 | 53.3 |
| stone_guardian | 53.78% | 543 | 1,428 | 31.2 |
| lightbringer_monk | 54.86% | 556 | 1,450 | 60.0 |
| pack_alpha | 55.03% | 576 | 1,368 | 57.6 |
| iron_warlord | 55.82% | 541 | 1,593 | 52.6 |

The order is durability, not damage: the logit of win rate correlates 0.61 with log effective HP and
-0.53 with log DPS. A two-term fit reaches only R^2 = 0.45 with a slope of 0.21 logit per log unit of
effective HP, so a 25% HP change on a squishy hero is worth about one point of win rate. That is why
eleven hand policies and two trained profiles all landed near 50%: **a numeric patch of identity
preserving size cannot move a hero 7 points.** Closing B1 needs the Phase 2B optimizer to search the
genome on the real harness, or a design lever on how durability converts into victory.

`tempest_chaser` at 43.4% is the single worst case and the one that most needs an identity answer: it
is the lowest effective HP among the melee bruisers (856) with mid damage, so it loses the only axis
that decides these matches.

## Seat equity: the halves are not mirror images

Seat 1 wins 46.96% of 5,600 (z = -4.55). The cause is geometric, not a draft-order rule: the seats draw
first picks from disjoint circle pools, seat 1 averaging x = -0.1, z = 107.96 and seat 2 x = 54.1,
z = 95.9, so the two z bands (101.5-114.5 against 89.5-102.5) are not symmetric. Seat 2's band is worth
about three points. This is a map-layout fix, in the same family as the tile effects below.

## Tile equity: specific hero-tile pairs are broken

14 of 360 pairs fail, and the failures are large rather than marginal (n = 30-38, CI about +/-17):
tempest_chaser at (0, 108) wins 13.33%, coral_vanguard at (0, 114.5) 25.0%, flame_archon at (0, 114.5)
27.27%, against pack_alpha at (6.5, 114.5) 72.97% and lightbringer_monk at (47.5, 102.5) 71.88%. The
z = 114.5 column and the (0, ·) column both look systematically loaded, which points at the element or
terrain assigned to those circles rather than at the heroes.

## G1: the profile drafts the losing archetype

The profile seat is not failing on economy or positioning. Against the uniform seat it spends 300 vs 290
gold, buys the same 19.99 upgrades, deals 216.6k vs 221.1k damage, and its first-pick tiles are identical
(mean z 108.08 vs 107.96).

It loses because of what it drafts. Its first picks are rune_scribe 543, tidecaller 504, flame_archon 465,
storm_caller 463, arcane_scholar 437 and frost_warden 425, and it never takes iron_warlord, stone_guardian
or magma_smith. Drafting the 46-48% cluster every match reproduces the 42.5% observed exactly, and it is
why the result is significantly worse than random (z = -3.0) rather than flat.

The weights, not the feature vector, are mis-signed for this content: `survivability` and `killSpeed` are
both in the vector, but the trained weights reward the ranged/DPS terms that the measurement says lose.
Re-fit the profile against these numbers before blaming feature fidelity, and evaluate it with the same
seed set as G1.

## The policy ceiling: 60%, and why 65% cannot come from hero choice

Six weight vectors were played against uniform on the same 200 seeds, after the measurement showed
that durability decides these matches:

| Weights (seat 1) | Win rate | 95% CI |
|---|---:|---:|
| `survivability` only `[0,1,0,0,0,0,0,0]` | **60.0%** | ±6.8 |
| durability `[0,1,.2,0,0,.3,.1,.3]` | 56.0% | ±6.9 |
| durability + counter `[0,1,.2,.5,0,.3,.1,.3]` | 52.5% | ±6.9 |
| hand prior `chaos_arena_bot.json` | 50.0% | ±6.9 |
| trained `v2` (control, reproduces 46.0%) | 46.0% | ±6.9 |
| `v2` with every sign flipped | 39.5% | ±6.8 |

The ladder is monotone in durability and in the role-counter weight, so the counter term is harmful and
one feature carries the whole result. `Realm` now takes `--train-initial-weights` so an evolution can
start from that vector instead of noise in `[-1,1]` with four matches per candidate.

The structural point: if B1 holds every hero at 50% ± 3, the bot's only lever is which hero and circle it
takes from three offers, and that lever measures about ten points. **G1 at ≥65% therefore cannot coexist
with a flat B1 through hero choice alone.** Reaching it needs the decisions the map never scores
(research 3.14, elixirs and rerolls 3.15) or a re-scoped gate. The 2,000-match run against the
survivability vector is what turns this from an argument into a number.

## Two gate definitions that cannot be satisfied

Both are worth fixing before the next measurement, because they cost hours and return no verdict:

1. **B1 samples.** Two first picks per match means 4,000 matches average exactly 400 picks per hero, so
   half of the heroes necessarily land below the 400-pick minimum. 5,600 is the smallest count that puts
   every hero safely above it (~540).
2. **B3 and G1 samples.** B3 requires every one of 360 hero-tile pairs to reach 30 picks, which needs
   about 5,400 matches and even then leaves a tail. G1 requires 2,000 bot matches for a verdict while the
   pipeline plays 400.

## Reproduce

```
powershell -NoProfile -ExecutionPolicy Bypass -File run-pipeline.ps1 `
  -MatchCount 5600 -SeedCount 400 -Parallel 8

powershell -NoProfile -ExecutionPolicy Bypass -File run-pipeline.ps1 `
  -MatchCount 5600 -SeedCount 400 -Parallel 8 -SeedStart 4001 `
  -PriorOutcomesPath <notes>/chaos-arena-balance-report.random.n4000.jsonl
```

The second form resumes without re-measuring earlier seeds: the pipeline merges prior outcomes by seed,
so an interrupted run costs only the seeds in flight.

## Shipped profile — 2026-11-05

The map now ships `Chaos Arena_MeasuredSurvivabilityPrior` (weights `[0, 1, 0, 0, 0, 0, 0, 0]`). The
200-seed sweep put it at 60.0% ± 6.8; the 2,000-seed confirmation puts it at **56.7% ± 2.2** (1,134 wins),
so the sweep was optimistic by about three points and 56.7% is the figure to quote. Against the G1 gate
that is still eight points short of 65%. It replaces `Chaos Arena_AutoTrained`, which won 44.0% and
therefore lost to random play. The vector is a measured prior from the weight sweep,
not trainer output; `--train-initial-weights` now seeds evolution from it so the next training run starts
from a policy that already beats random. `Chaos Arena_bot_v2.json` (46.0%) stays out of the product path.
