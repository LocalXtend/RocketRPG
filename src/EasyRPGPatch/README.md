# EasyRPG Player (RocketRPG edition)

RPG Maker 2000/2003 games run on a modified **EasyRPG Player 0.8.1.1**. Its full source (upstream 0.8.1.1 as the
first commit, RocketRPG's changes on top) and build scripts live in:

**https://github.com/LocalXtend/EasyRPG_Patch** — tags match RocketRPG versions (`v0.6.1`, …).

- `scripts/build_easyrpg.ps1` checks that repository out to `build/easyrpg/Player` and builds `build/easyrpg/dist`
  (picked up by `scripts/setup_runtimes.ps1`). Edit, commit and push Player changes in `build/easyrpg/Player`.
- EasyRPG Player is GPL v3+; each RocketRPG release attaches the matching source as
  `RocketRPG-<version>-easyrpg-source.zip`.
