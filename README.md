# CS2 Discord RPC for Linux

Not sophisticated at all, just some vibecoded script that adds a Discord Rich Presence that displays current map, team and score of your game. If you have any suggestions for features feel free to open an issue or better yet make a PR

## Setup

- [Download gamestate_integration_cs2rpc.cfg](https://raw.githubusercontent.com/bl4zee1g/cs2-rpc-linux/main/gamestate_integration_cs2rpc.cfg) and move it into your cfg folder (no need to add it to autostart)
- [Download the latest binary](https://github.com/bl4zee1g/cs2-rpc-linux/releases/download/latest/cs2-rpc) from Github Releases and put it wherever you want
- Add CS2 launch options:
```
setsid [Location of binary] > /dev/null 2>&1 & %command%
```
- Done! Now when you launch the game it should just work.

## Credits
[@antonpup](https://github.com/antonpup) for writing [CounterStrike2GSI](https://github.com/antonpup/CounterStrike2GSI)