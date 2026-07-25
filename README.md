# CS2 Discord RPC for Linux

Not sophisticated at all, just some vibecoded script that adds a Discord Rich Presence that displays current map, team and score of your game. If you have any suggestions for features feel free to open an issue or better yet make a PR

## Setup

- Download [gamestate_integration_cs2rpc.cfg](https://github.com/bl4zee1g/cs2-rpc-linux/blob/main/gamestate_integration_cs2rpc.cfg) and move it into your cfg folder (no need to add it to autostart)
- Download the latest binary from [Github Releases](https://github.com/bl4zee1g/cs2-rpc-linux/releases/latest), put it wherever you want and make it executable:
```
chmod +x cs2-rpc
```
- Make some sort of autostart daemon for it. For me on Hyprland, I just add it to the autostart bit of my hyprland.lua:
```lua
hl.on("hyprland.start", function () 
  hl.exec_cmd([path to cs2-rpc binary])
end)
```
If you're not on Hyprland but use systemd I recommend writing a systemd unit for starting cs2-rpc.
- Done! Now when you launch the game it should just work.

## Credits
[@antonpup](https://github.com/antonpup) for writing [CounterStrike2GSI](https://github.com/antonpup/CounterStrike2GSI)