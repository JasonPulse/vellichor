# Vendored XiHeadless sources

These `.cs` files are **copied** from the XiHeadless bot repo
(`github.com/JasonPulse/xiheadless`) so the Vellichor repo builds **standalone** — CI has no
access to the external repo, and the DAT-viewer handoff needs this self-contained. The SDK's
default `**/*.cs` glob compiles everything here; `XiProtocol.csproj` adds no explicit includes.

**Do not hand-edit.** Edit upstream in XiHeadless, then resync.

**Excluded on purpose** (bot-logic — depend on bot-layer enums, not wire protocol / world state):
`Net/BotApi.cs`, `Game/QuestDefs.cs`, `Game/Vendors.cs`, `Game/HuntZones.cs`, `Game/PartyRoles.cs`.

**Resync** (run from this `vendor/` dir, with XiHeadless checked out at the usual relative path):

```sh
SRC=../../../../C#/Personal/XiHeadless    # adjust to your XiHeadless checkout
rsync -a --delete --exclude 'BotApi.cs' "$SRC/Net/" Net/
rsync -a --delete --exclude 'HuntZones.cs' --exclude 'PartyRoles.cs' \
      --exclude 'QuestDefs.cs' --exclude 'Vendors.cs' "$SRC/Game/" Game/
cp "$SRC/Interfaces/ISession.cs" Interfaces/
cp "$SRC/Geometry.cs" .
```
